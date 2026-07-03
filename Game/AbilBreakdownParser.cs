using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using FujinTerm.Services;
using FujinTerm.Terminal;

namespace FujinTerm.Game;

/// <summary>One source slice of an <c>abil &lt;code&gt;</c> breakdown — where a
/// portion of an ability's current total comes from and how much that source
/// contributes. Sources seen in GreaterMUD/Paradigm output:
/// <c>granted</c> (quest awards), <c>worn</c> (equipment), <c>spells</c> (live
/// spell affects), <c>race</c> (racial bonus). The server only prints the
/// sources that actually affect the character, so a breakdown carries a
/// variable subset.</summary>
public readonly record struct AbilContribution(string Source, int Value);

/// <summary>
/// Parsed result of a GreaterMUD/Paradigm <c>abil &lt;code&gt;</c> query — the
/// per-source breakdown of one ability's current total. The
/// <see cref="Spells"/> slice is the live-rolled spell contribution (e.g. a
/// nature-tap / mana-flux mana-regen roll under code 145), which the
/// regen-buff automation reads to decide whether a roll landed below the
/// reroll threshold.
/// </summary>
public sealed record AbilBreakdown(
    int Code,
    string Label,
    IReadOnlyList<AbilContribution> Contributions,
    int? ReportedTotal)
{
    /// <summary>Sum of every per-source contribution. When the server printed a
    /// <c>total:</c> line this equals <see cref="ReportedTotal"/>; otherwise it
    /// is the client's own roll-up.</summary>
    public int Total
    {
        get
        {
            int sum = 0;
            foreach (AbilContribution c in Contributions) sum += c.Value;
            return sum;
        }
    }

    /// <summary>Contribution from a named source (case-insensitive), or 0 when
    /// that source didn't appear — the server omits unaffected sources, so a
    /// missing line means no contribution.</summary>
    public int From(string source)
    {
        foreach (AbilContribution c in Contributions)
            if (string.Equals(c.Source, source, System.StringComparison.OrdinalIgnoreCase))
                return c.Value;
        return 0;
    }

    /// <summary>Quest-awarded contribution (<c>granted:</c> line).</summary>
    public int Granted => From("granted");

    /// <summary>Equipment contribution (<c>worn:</c> line).</summary>
    public int Worn => From("worn");

    /// <summary>Live spell contribution (<c>spells:</c> line) — the rolled
    /// value of a code-145 mana-regen buff (nature tap / mana flux), positive
    /// or negative.</summary>
    public int Spells => From("spells");

    /// <summary>Racial contribution (<c>race:</c> line).</summary>
    public int Race => From("race");
}

/// <summary>
/// Stateful parser for GreaterMUD/Paradigm <c>abil &lt;code&gt;</c> output. The
/// command lists, per source, how much of a single ability's total comes from
/// each place:
/// <code>
/// [HP=899/MA=573]: (Resting) abil 145
/// granted:  ManaRegen(145)              0005
/// worn:     ManaRegen(145)              0185
/// spells:   ManaRegen(145)              0011
/// race:     ManaRegen(145)              0010
/// </code>
/// Only the sources that actually affect the character are printed, so the row
/// set is variable and there is no header to key on — the parser detects the
/// block purely by the distinctive <c>source: Name(code) value</c> row shape
/// and closes it on the first non-matching line or prompt. Each block yields
/// one <see cref="AbilBreakdown"/> via <see cref="BreakdownParsed"/>.
/// </summary>
/// <remarks>
/// The source label text (<c>ManaRegen</c>) is GreaterMUD's spelling and can
/// differ from <see cref="GameData.AbilityNames"/>' canonical name
/// (<c>ManaRgn</c> for 145), so the ability is keyed off the numeric
/// <c>(code)</c> in each row, never the name text. A <c>total:</c> row is kept
/// out of <see cref="AbilBreakdown.Contributions"/> and surfaced as
/// <see cref="AbilBreakdown.ReportedTotal"/> so the summed contributions stay a
/// clean granted+worn+spells+race roll-up.
/// </remarks>
public sealed partial class AbilBreakdownParser : IDisposable
{
    private LineExtractor? _lines;
    private readonly LogService? _log;

    private int _pendingCode = -1;
    private string _pendingLabel = string.Empty;
    private int? _pendingTotal;
    private readonly List<AbilContribution> _pending = new();

    /// <summary>Fires once per completed <c>abil</c> block.</summary>
    public event Action<AbilBreakdown>? BreakdownParsed;

    public AbilBreakdownParser(LogService? log = null) => _log = log;

    /// <summary>Bind to the current session's line stream, re-binding across
    /// session swaps (same shape as the other line-fed parsers).</summary>
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_lines is not null) _lines.LineEmitted -= OnLineEmitted;
        _lines = lines;
        _lines.LineEmitted += OnLineEmitted;
    }

    public void Dispose()
    {
        if (_lines is not null) _lines.LineEmitted -= OnLineEmitted;
    }

    /// <summary>Test hook — drive one line through the parser without a live
    /// <see cref="LineExtractor"/>. Pass <paramref name="isPromptLine"/> to
    /// replay the prompt row that flushes a block.</summary>
    internal void FeedTestLine(string text, bool isPromptLine = false)
        => HandleLine(text, isPromptLine);

    /// <summary>Test hook — drive several non-prompt content lines. Does not
    /// flush; feed a prompt or a non-matching line afterwards to close the
    /// block, exercising the real terminator paths.</summary>
    internal void FeedTestLines(IEnumerable<string> lines)
    {
        foreach (string text in lines) HandleLine(text, isPromptLine: false);
    }

    private void OnLineEmitted(LineExtractor.EmittedLine line)
        => HandleLine(line.Text, line.IsPromptLine);

    private void HandleLine(string text, bool isPromptLine)
    {
        // The prompt is the universal end-of-response marker; close any block.
        if (isPromptLine)
        {
            Flush();
            return;
        }

        Match m = RowPattern().Match(text);
        if (!m.Success)
        {
            // First non-row line ends an in-progress block. The command echo
            // line ("… abil 145") arrives before any rows, so an empty pending
            // set just no-ops here.
            Flush();
            return;
        }

        int code = int.Parse(m.Groups["code"].Value, CultureInfo.InvariantCulture);
        // Back-to-back queries for different codes: close the previous block
        // before starting the new one.
        if (_pending.Count > 0 && code != _pendingCode) Flush();

        _pendingCode = code;
        _pendingLabel = m.Groups["name"].Value.Trim();
        int value = int.Parse(m.Groups["val"].Value, CultureInfo.InvariantCulture);
        string source = m.Groups["src"].Value.ToLowerInvariant();

        // The total row is a summary, not a source — hold it aside so the
        // contribution sum stays granted+worn+spells+race.
        if (source == "total")
            _pendingTotal = value;
        else
            _pending.Add(new AbilContribution(source, value));
    }

    private void Flush()
    {
        if (_pending.Count == 0 && _pendingTotal is null) return;

        AbilBreakdown breakdown = new(
            _pendingCode,
            _pendingLabel,
            _pending.ToArray(),
            _pendingTotal);

        _pending.Clear();
        _pendingTotal = null;
        _pendingCode = -1;
        _pendingLabel = string.Empty;

        _log?.Debug("AbilParser",
            $"abil {breakdown.Code} → total {breakdown.Total} (spells {breakdown.Spells})");
        BreakdownParsed?.Invoke(breakdown);
    }

    // source label (letters) + ':' + ability name (up to the '(') + '(code)' +
    // right-aligned, zero-padded, optionally-signed value.
    [GeneratedRegex(
        @"^\s*(?<src>[A-Za-z]+):\s+(?<name>[^()]+?)\s*\((?<code>\d+)\)\s+(?<val>-?\d+)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex RowPattern();
}
