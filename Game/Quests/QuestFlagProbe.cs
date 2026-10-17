using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Quests;

// Reads the character's live quest-flag values off the wire so the completion sync can
// tell which quests are done. Two realm-specific shapes (see GAME_MECHANICS.md):
//   • Paradigm (built-in, ungated): `abil <flag>` → one line per flag, "GoodQuest(126)   8".
//     We iterate the target flags, paced, and parse each reply — the flag number is IN the
//     reply, so replies don't have to be correlated to sends by order.
//   • Stock (behind sys-god access): `sys god <name> abil` → one wrapped line dumping every
//     ability, "User abilities: 126(17) 2(1) …". LineExtractor reassembles the soft-wrap, so
//     one HandleLine sees the whole list.
// Collects into flag → value over a settle window and returns the map; the parsers are pure
// statics. The caller picks the realm path
// (and, for stock, has already confirmed sys-god powers).
public sealed partial class QuestFlagProbe : IDisposable
{
    private readonly Action<string> _send;
    private readonly LogService? _log;
    private LineExtractor? _lines;
    private readonly Dictionary<int, int> _collected = new();
    private bool _collecting;
    private bool _disposed;

    // How long to keep collecting after the last command before returning.
    public TimeSpan SettleWindow { get; set; } = TimeSpan.FromSeconds(2);

    // Gap between paradigm per-flag sends, so a batch of `abil` queries doesn't trip a
    // "typing too fast" guard.
    public TimeSpan PerFlagPace { get; set; } = TimeSpan.FromMilliseconds(250);

    // Paradigm: "GoodQuest(126)   8" — a name, the flag id in parens, then the value.
    // Non-greedy name so the LAST parenthesised group before the trailing number is the id.
    [GeneratedRegex(@"^\s*(?<name>.+)\((?<flag>\d+)\)\s+(?<value>-?\d+)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ParadigmLine();

    // Stock: each "flag(value)" pair inside "User abilities: 126(17) 2(1) …".
    [GeneratedRegex(@"(?<flag>\d+)\((?<value>-?\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex StockPair();

    public QuestFlagProbe(Action<string> send, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(send);
        _send = send;
        _log = log;
    }

    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    // Stock bulk read: one `sys god <name> abil`, then wait the settle window. The caller has
    // already confirmed sys-god powers; without them the command yields nothing and this
    // returns an empty map (→ no auto-marks).
    public async Task<IReadOnlyDictionary<int, int>> ReadStockAsync(string characterName, CancellationToken ct = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(characterName)) return Snapshot();
        BeginCollect();
        _send($"sys god {characterName} abil");
        try { await Task.Delay(SettleWindow, ct).ConfigureAwait(true); }
        catch (OperationCanceledException) { /* return whatever arrived */ }
        return EndCollect();
    }

    // Paradigm per-flag read: `abil <flag>` for each target flag, paced, then a settle window.
    public async Task<IReadOnlyDictionary<int, int>> ReadParadigmAsync(IReadOnlyList<int> flags, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flags);
        if (_disposed || flags.Count == 0) return Snapshot();
        BeginCollect();
        try
        {
            foreach (int flag in flags)
            {
                if (ct.IsCancellationRequested) break;
                _send($"abil {flag}");
                await Task.Delay(PerFlagPace, ct).ConfigureAwait(true);
            }
            await Task.Delay(SettleWindow, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { /* return whatever arrived */ }
        return EndCollect();
    }

    // ----- pure parsers -----

    // Every flag→value pair in a stock `sys god … abil` dump line. Returns nothing for a line
    // that isn't the ability dump.
    public static IEnumerable<(int Flag, int Value)> ParseStockBulk(string line)
    {
        if (string.IsNullOrWhiteSpace(line)
            || line.IndexOf("abilities", StringComparison.OrdinalIgnoreCase) < 0)
            yield break;
        foreach (Match m in StockPair().Matches(line))
            if (int.TryParse(m.Groups["flag"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int f)
                && f > 0
                && int.TryParse(m.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                yield return (f, v);
    }

    // One paradigm `abil` reply line → (flag, value); false when the line isn't one.
    public static bool TryParseParadigmLine(string line, out int flag, out int value)
    {
        flag = 0; value = 0;
        if (string.IsNullOrWhiteSpace(line)) return false;
        Match m = ParadigmLine().Match(line);
        if (!m.Success) return false;
        return int.TryParse(m.Groups["flag"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out flag)
            && flag > 0
            && int.TryParse(m.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
    }

    private void OnLine(LineExtractor.EmittedLine emitted)
    {
        if (!_collecting) return;
        string line = emitted.Text;
        // Try the stock bulk shape first (its own distinctive header); fall back to a
        // single paradigm reply line. A given line is only ever one shape.
        bool anyStock = false;
        foreach ((int flag, int value) in ParseStockBulk(line)) { _collected[flag] = value; anyStock = true; }
        if (!anyStock && TryParseParadigmLine(line, out int f, out int v)) _collected[f] = v;
    }

    private void BeginCollect()
    {
        _collected.Clear();
        _collecting = true;
    }

    private IReadOnlyDictionary<int, int> EndCollect()
    {
        _collecting = false;
        _log?.Info("QuestFlags", $"read {_collected.Count} flag value(s) from the game");
        return Snapshot();
    }

    private IReadOnlyDictionary<int, int> Snapshot() => new Dictionary<int, int>(_collected);
}
