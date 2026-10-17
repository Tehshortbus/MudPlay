using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game;

// One source slice of an `abil <code>` breakdown — where a portion of an
// ability's current total comes from and how much that source contributes.
// Sources: granted (quest awards), worn (equipment), spells (live spell
// affects), race (racial bonus). The server only prints the sources that
// actually affect the character, so a breakdown carries a variable subset.
public readonly record struct AbilContribution(string Source, int Value);

// Parsed result of an `abil <code>` query — the per-source breakdown of one
// ability's current total. The Spells slice is the live-rolled spell
// contribution (e.g. a nature-tap / mana-flux mana-regen roll under code 145),
// which the regen-buff automation reads to decide whether a roll landed below
// the reroll threshold.
public sealed record AbilBreakdown(
    int Code,
    IReadOnlyList<AbilContribution> Contributions)
{
    // Sum of every per-source contribution, the same figure as the server's
    // `total:` line when it prints one.
    public int Total
    {
        get
        {
            int sum = 0;
            foreach (AbilContribution c in Contributions) sum += c.Value;
            return sum;
        }
    }

    // Contribution from a named source (case-insensitive), or 0 when that
    // source didn't appear — the server omits unaffected sources, so a missing
    // line means no contribution.
    public int From(string source)
    {
        foreach (AbilContribution c in Contributions)
            if (string.Equals(c.Source, source, System.StringComparison.OrdinalIgnoreCase))
                return c.Value;
        return 0;
    }

    // Live spell contribution (`spells:` line) — the rolled value of a code-145
    // mana-regen buff (nature tap / mana flux), positive or negative.
    public int Spells => From("spells");
}

// Stateful parser for `abil <code>` output. The command lists, per source, how
// much of a single ability's total comes from each place:
//
//   [HP=899/MA=573]: (Resting) abil 145
//   granted:  ManaRegen(145)              0005
//   worn:     ManaRegen(145)              0185
//   spells:   ManaRegen(145)              0011
//   race:     ManaRegen(145)              0010
//
// Only the sources that actually affect the character are printed, so the row
// set is variable and there is no header to key on — the parser detects the
// block purely by the distinctive `source: Name(code) value` row shape and
// closes it on the first non-matching line or prompt. Each block yields one
// AbilBreakdown via BreakdownParsed.
//
// The source label text (ManaRegen) is the server's spelling and can differ
// from GameData.AbilityNames' canonical name (ManaRgn for 145), so the ability
// is keyed off the numeric (code) in each row, never the name text. A `total:`
// row is kept out of Contributions so the summed contributions stay a clean
// granted+worn+spells+race roll-up.
public sealed partial class AbilBreakdownParser : IDisposable
{
    private LineExtractor? _lines;
    private readonly LogService? _log;

    private int _pendingCode = -1;
    private bool _pendingTotal;
    private readonly List<AbilContribution> _pending = new();

    // Fires once per completed `abil` block.
    public event Action<AbilBreakdown>? BreakdownParsed;

    public AbilBreakdownParser(LogService? log = null) => _log = log;

    // Bind to the current session's line stream, re-binding across session
    // swaps (same shape as the other line-fed parsers).
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
        int value = int.Parse(m.Groups["val"].Value, CultureInfo.InvariantCulture);
        string source = m.Groups["src"].Value.ToLowerInvariant();

        // The total row is a summary, not a source — keep it out so the
        // contribution sum stays granted+worn+spells+race.
        if (source == "total")
            _pendingTotal = true;
        else
            _pending.Add(new AbilContribution(source, value));
    }

    private void Flush()
    {
        if (_pending.Count == 0 && !_pendingTotal) return;

        AbilBreakdown breakdown = new(_pendingCode, _pending.ToArray());

        _pending.Clear();
        _pendingTotal = false;
        _pendingCode = -1;

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
