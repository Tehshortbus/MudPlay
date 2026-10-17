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
// action). The parsing lives in TokenCatalog (pure); this owns the
// send/collect/observe plumbing, modelled on QuestFlagProbe.
public sealed class TokenTracker : IDisposable
{
    private readonly Action<string> _send;                 // raw wire sender (SendGameCommand)
    private readonly Func<IReadOnlyList<string>> _carried; // Inventory.Snapshot.CarriedItems
    private readonly Func<bool> _onParadigm;
    // Recognise the token use lines via the seeded token spell messages (one place
    // for the wording): self-use → the matched token's place; someone else's use in
    // our room → the actor's name (the WitnessMessage's {source} capture).
    private readonly Func<string, string?> _matchSelfUse;
    private readonly Func<string, string?> _matchMemberDeparted;
    private readonly LogService? _log;
    private LineExtractor? _lines;

    private readonly object _lock = new();
    private readonly Dictionary<string, TokenCharge> _charges = new();   // key = normalized place
    private bool _collecting;
    private string? _currentLookPlace;   // token-name line seen during a look burst
    private bool _disposed;

    // Fired when a token USE succeeds (the "You invoke the token…" line). Carries the
    // place: the charge re-look, the buff pause and a token route's landing all hang
    // off it (the use also wipes buffs via negate magic).
    public event Action<string>? TokenUsed;

    // Fired when someone else uses a token in the room we're in (their WitnessMessage,
    // "<name> invokes a token and summons a gryphon to <place>!"). Carries the departing
    // player's name. The party-regroup coordinator — the leader tokens LAST — watches
    // this in its own room to confirm each member ported before it follows.
    public event Action<string>? MemberDeparted;

    public TimeSpan PerLookPace { get; set; } = TimeSpan.FromMilliseconds(400);
    public TimeSpan SettleWindow { get; set; } = TimeSpan.FromSeconds(2);
    // Let the use + its teleport resolve before re-looking for the true count.
    public TimeSpan RelookDelay { get; set; } = TimeSpan.FromMilliseconds(1500);

    public readonly record struct TokenCharge(string Place, int Remaining, DateTimeOffset ReadAt);

    public TokenTracker(
        Action<string> send,
        Func<IReadOnlyList<string>> carried,
        Func<bool> onParadigm,
        Func<string, string?> matchSelfUse,
        Func<string, string?> matchMemberDeparted,
        Action<int, Action> schedule,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(carried);
        ArgumentNullException.ThrowIfNull(onParadigm);
        ArgumentNullException.ThrowIfNull(matchSelfUse);
        ArgumentNullException.ThrowIfNull(matchMemberDeparted);
        ArgumentNullException.ThrowIfNull(schedule);
        _send = send;
        _carried = carried;
        _onParadigm = onParadigm;
        _matchSelfUse = matchSelfUse;
        _matchMemberDeparted = matchMemberDeparted;
        _schedule = schedule;
        _log = log;
    }

    // ----- Buff-pause window -----
    // A token use casts negate magic, wiping every buff — so rebuffing in the moments
    // BEFORE it fires is wasted. When we see a token use go out (our own, or one a
    // leader relays to us via @party/@do), we hold buffing until the use actually
    // succeeds (TokenUsed) or a safety timeout elapses (a use blocked by an NPC / gold
    // / level prints no success line, so the hold must self-release). The buff engine
    // gates on IsBuffPausedForToken; the Buff Watchdog surfaces it via BuffPauseChanged.
    private const int BuffPauseTimeoutMs = 30_000;
    private readonly Action<int, Action> _schedule;
    private bool _buffPauseActive;
    private int _buffPauseGen;

    public bool IsBuffPausedForToken => _buffPauseActive;
    public event Action? BuffPauseChanged;

    private void ArmBuffPause(string place)
    {
        int gen = ++_buffPauseGen;
        if (!_buffPauseActive)
        {
            _buffPauseActive = true;
            _log?.Info("Tokens", $"buffing paused — token use to {place} imminent (negate magic will wipe buffs)");
            BuffPauseChanged?.Invoke();
        }
        // (Re-)arm the safety release: if no success line lands within the window the
        // use failed, so let buffing resume.
        _schedule(BuffPauseTimeoutMs, () =>
        {
            if (gen == _buffPauseGen) ClearBuffPause("timed out (use never confirmed)");
        });
    }

    private void ClearBuffPause(string why)
    {
        if (_disposed || !_buffPauseActive) return;
        _buffPauseActive = false;
        _buffPauseGen++;
        _log?.Info("Tokens", $"buffing resumed — token pause cleared: {why}");
        BuffPauseChanged?.Invoke();
    }

    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    // The master switch (true = off): off, no token is looked at, at login or
    // after a use. Using a token by hand still arms and clears its buff pause;
    // only the look that reads the charges is skipped.
    public Func<bool>? MasterSwitchOff { get; set; }

    // Read the charges of every held token — one paced `look` each, then a settle
    // window. Called once on login (Paradigm only). No-op with no tokens held.
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_disposed || !_onParadigm()) return;
        if (MasterSwitchOff?.Invoke() == true) return;
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
            // Resolve the `use <arg>` against held tokens — full "token of <place>" OR
            // a shorthand partial (`use lost`, relayed by a leader's @party/@do) — so a
            // relayed token use still arms the buff-pause + re-look.
            if (ResolveHeldTokenUseArg(line[4..]) is { } place)
            {
                ArmBuffPause(place);
                _ = RelookAfterDelayAsync(place);
            }
        }
    }

    // The place of the held token a `use <arg>` refers to (full name or shorthand
    // partial), or null when the arg matches no held token. Match is a case-insensitive
    // substring of the token's full name or its normalized place — the same loose
    // resolution the game does for a partial `use`.
    private string? ResolveHeldTokenUseArg(string arg)
    {
        string a = arg.Trim();
        if (a.Length < 3) return null;
        string aLower = a.ToLowerInvariant();
        foreach ((string lookName, string place) in TokenCatalog.HeldTokens(_carried()))
        {
            if (lookName.ToLowerInvariant().Contains(aLower)
                || TokenCatalog.NormalizePlace(place).Contains(aLower))
                return place;
        }
        return null;
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
        if (MasterSwitchOff?.Invoke() == true) return;
        BeginCollect();
        _send($"look {TokenCatalog.LookName(place)}");
        try { await Task.Delay(SettleWindow).ConfigureAwait(true); }
        catch (OperationCanceledException) { /* keep whatever arrived */ }
        EndCollect();
    }

    private void OnLine(LineExtractor.EmittedLine emitted)
    {
        string line = emitted.Text;

        // A successful use prints the token spell's CasterMessage regardless of whether
        // we're mid-collect — always catch it: signal listeners and reconcile the count.
        if (_matchSelfUse(line) is { } usedPlace)
        {
            _log?.Info("Tokens", $"token use succeeded → {usedPlace} (buffs wiped by negate magic)");
            ClearBuffPause("token used");   // the use fired — let buffing resume (rebuff after landing)
            TokenUsed?.Invoke(usedPlace);
            _ = RelookAfterDelayAsync(usedPlace);
            return;
        }

        // Someone else used a token in our room (their WitnessMessage) — surface the
        // actor for the regroup coordinator.
        if (_matchMemberDeparted(line) is { } departed)
        {
            MemberDeparted?.Invoke(departed);
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
