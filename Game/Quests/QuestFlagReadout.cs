using System.Text;
using System.Text.RegularExpressions;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Quests;

// Picks up a quest flag's value from any `abil` the game answers, not only the client's
// own reads: an `abil <flag>` typed on Paradigm or Anomaly, or a `sys god <name> abil`
// naming this character on Stock (GAME_MECHANICS "Quest-flag values (`abil` / `sys god
// abil`)"). The reply shapes are loose enough to match other output, so they are read
// only for a short window after such a command went out. While the client's own sync or
// `@quest` read is collecting, its reader has the lines and this one stays out.
public sealed partial class QuestFlagReadout
{
    // From the command going out to its reply.
    public static readonly TimeSpan ReplyWindow = TimeSpan.FromSeconds(5);

    private readonly Func<string?> _selfName;
    private readonly Func<bool> _clientReadUnderWay;
    private readonly Action<IReadOnlyDictionary<int, int>> _read;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private LineExtractor? _lines;
    private DateTimeOffset _armedUntil = DateTimeOffset.MinValue;

    // read: each reply's flag values, handed on as they arrive.
    public QuestFlagReadout(
        Func<string?> selfName, Func<bool> clientReadUnderWay, Action<IReadOnlyDictionary<int, int>> read,
        Func<DateTimeOffset>? now = null, LogService? log = null)
    {
        _selfName = selfName ?? throw new ArgumentNullException(nameof(selfName));
        _clientReadUnderWay = clientReadUnderWay ?? throw new ArgumentNullException(nameof(clientReadUnderWay));
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        _log = log;
    }

    // Bound per connection, so the previous extractor is let go.
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnEmitted;
        _lines = lines;
        _lines.LineEmitted += OnEmitted;
    }

    // Every line on its way out.
    public void ObserveOutbound(ReadOnlySpan<byte> data)
    {
        if (data.Length is 0 or > 300) return;
        foreach (string line in Encoding.Latin1.GetString(data).Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries))
            if (AsksForFlags(line)) _armedUntil = _now() + ReplyWindow;
    }

    private bool AsksForFlags(string command)
    {
        if (ParadigmAbil().IsMatch(command)) return true;
        Match god = StockAbil().Match(command);
        if (!god.Success) return false;
        string self = Recovery.CorpseRecoveryLines.GivenName(_selfName());
        return self.Length > 0 && string.Equals(god.Groups["name"].Value, self, StringComparison.OrdinalIgnoreCase);
    }

    private void OnEmitted(LineExtractor.EmittedLine line) => OnLine(line.Text);

    public void OnLine(string text)
    {
        if (_now() > _armedUntil || _clientReadUnderWay()) return;
        Dictionary<int, int> values = new();
        foreach ((int flag, int value) in QuestFlagProbe.ParseStockBulk(text)) values[flag] = value;
        if (values.Count == 0 && QuestFlagProbe.TryParseParadigmLine(text, out int f, out int v)) values[f] = v;
        if (values.Count == 0) return;
        _log?.Debug("QuestFlags", $"flag read off the game: {string.Join(", ", values.Select(kv => $"{kv.Key}={kv.Value}"))}");
        _read(values);
    }

    [GeneratedRegex(@"^\s*abil(?:\s+\d+)?\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ParadigmAbil();

    [GeneratedRegex(@"^\s*sys\s+god\s+(?<name>\S+)\s+abil\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StockAbil();
}
