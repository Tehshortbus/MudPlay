using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Tokens;

// Tracks the daily-charge count of the Paradigm transport tokens the character
// holds. The authoritative source is always the game's `look <token>` (which
// prints "Uses remaining: N"): we read every held token once on login, and
// re-look a token whenever a `use` for it is observed (or its success line is
// seen), so a failed use — blocked by an NPC in the room, too little gold, too
// low a level — never mis-decrements. Charges reset server-side at cleanup; the
// next login's look picks that up. Paradigm-only (Func onParadigm gates every
// action). The parsing lives in TokenCatalog (pure, unit-tested); this owns the
// send/collect/observe plumbing, modelled on QuestFlagProbe.
public sealed class TokenTracker : IDisposable
{
    private readonly Action<string> _send;                 // raw wire sender (SendGameCommand)
    private readonly Func<IReadOnlyList<string>> _carried; // Inventory.Snapshot.CarriedItems
    private readonly Func<bool> _onParadigm;
    private readonly LogService? _log;
    private LineExtractor? _lines;

    private readonly object _lock = new();
    private readonly Dictionary<string, TokenCharge> _charges = new();   // key = normalized place
    private bool _collecting;
    private string? _currentLookPlace;   // token-name line seen during a look burst
    private bool _disposed;

    // Fired after the charge map changes (login read, a re-look landing, a clear),
    // so a future UI can refresh. Fired on the UI thread.
    public event Action? Changed;

    // Fired when a token USE succeeds (the "You invoke the token…" line). Carries the
    // place. PR 1 uses it to reconcile charges; later phases hang the pause-rebuffs
    // and party-confirm logic off it (the use also wipes buffs via negate magic).
    public event Action<string>? TokenUsed;

    public TimeSpan PerLookPace { get; set; } = TimeSpan.FromMilliseconds(400);
    public TimeSpan SettleWindow { get; set; } = TimeSpan.FromSeconds(2);
    // Let the use + its teleport resolve before re-looking for the true count.
    public TimeSpan RelookDelay { get; set; } = TimeSpan.FromMilliseconds(1500);

    public readonly record struct TokenCharge(string Place, int Remaining, DateTimeOffset ReadAt);

    public TokenTracker(
        Action<string> send,
        Func<IReadOnlyList<string>> carried,
        Func<bool> onParadigm,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(carried);
        ArgumentNullException.ThrowIfNull(onParadigm);
        _send = send;
        _carried = carried;
        _onParadigm = onParadigm;
        _log = log;
    }

    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    // Read the charges of every held token — one paced `look` each, then a settle
    // window. Called once on login (Paradigm only). No-op with no tokens held.
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_disposed || !_onParadigm()) return;
        IReadOnlyList<(string LookName, string Place)> held = TokenCatalog.HeldTokens(_carried());
        if (held.Count == 0) return;

        _log?.Info("Tokens", $"reading charges for {held.Count} held token(s): {string.Join(", ", held.Select(h => h.Place))}");
        BeginCollect();
        try
        {
            foreach ((string lookName, _) in held)
            {
                if (ct.IsCancellationRequested) break;
                _send($"look {lookName}");
                await Task.Delay(PerLookPace, ct).ConfigureAwait(true);
            }
            await Task.Delay(SettleWindow, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { /* keep whatever arrived */ }
        EndCollect();
        Changed?.Invoke();
    }

    // Detect an outbound `use <token>` (the observed user/engine send path) and
    // re-look that token to reconcile its charges. Own raw-path sends bypass this.
    public void ObserveOutbound(byte[] data)
    {
        if (_disposed || !_onParadigm() || data is null || data.Length == 0) return;
        string text = System.Text.Encoding.Latin1.GetString(data);
        foreach (string raw in text.Split('\r', '\n'))
        {
            string line = raw.Trim();
            if (line.Length < 5 || !line.StartsWith("use ", StringComparison.OrdinalIgnoreCase)) continue;
            if (TokenCatalog.PlaceOf(line[4..].Trim()) is { } place)
                _ = RelookAfterDelayAsync(place);
        }
    }

    // Charges last read for a token, addressed by "token of X" or bare "X". null when
    // no look has landed for it this session.
    public int? ChargesFor(string placeOrName)
    {
        if (string.IsNullOrWhiteSpace(placeOrName)) return null;
        string place = TokenCatalog.PlaceOf(placeOrName) ?? placeOrName;
        lock (_lock)
            return _charges.TryGetValue(TokenCatalog.NormalizePlace(place), out TokenCharge c) ? c.Remaining : null;
    }

    // Every token whose charges we've read this session, place-sorted — for the bug report.
    public IReadOnlyList<TokenCharge> KnownCharges()
    {
        lock (_lock)
            return _charges.Values.OrderBy(c => c.Place, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Clear()
    {
        lock (_lock) _charges.Clear();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
    }

    private async Task RelookAfterDelayAsync(string place)
    {
        try { await Task.Delay(RelookDelay).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; }
        if (_disposed || !_onParadigm()) return;
        BeginCollect();
        _send($"look {TokenCatalog.LookName(place)}");
        try { await Task.Delay(SettleWindow).ConfigureAwait(true); }
        catch (OperationCanceledException) { /* keep whatever arrived */ }
        EndCollect();
        Changed?.Invoke();
    }

    private void OnLine(LineExtractor.EmittedLine emitted)
    {
        string line = emitted.Text;

        // A successful use prints "You invoke the token…" regardless of whether we're
        // mid-collect — always catch it: signal listeners and reconcile the count.
        if (TokenCatalog.MatchUseMessage(line) is { } usedPlace)
        {
            _log?.Info("Tokens", $"token use succeeded → {usedPlace} (buffs wiped by negate magic)");
            TokenUsed?.Invoke(usedPlace);
            _ = RelookAfterDelayAsync(usedPlace);
            return;
        }

        if (!_collecting) return;

        // Inside a look burst: a token-name line arms the current place; the next
        // "Uses remaining: N" records against it.
        if (TokenCatalog.MatchLookNameLine(line) is { } place)
        {
            _currentLookPlace = place;
            return;
        }
        int uses = TokenCatalog.ParseUsesRemaining(line);
        if (uses >= 0 && _currentLookPlace is { } current)
        {
            Record(current, uses);
            _currentLookPlace = null;
        }
    }

    private void Record(string place, int remaining)
    {
        lock (_lock)
            _charges[TokenCatalog.NormalizePlace(place)] = new TokenCharge(place, remaining, DateTimeOffset.Now);
        _log?.Info("Tokens", $"token of {place}: {remaining} use(s) remaining");
    }

    private void BeginCollect() { _collecting = true; _currentLookPlace = null; }
    private void EndCollect() { _collecting = false; _currentLookPlace = null; }
}
