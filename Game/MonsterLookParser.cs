using System.Text.RegularExpressions;
using FujinTerm.Services;
using FujinTerm.Terminal;

namespace FujinTerm.Game;

// Turns a `look <monster>` response into an estimated HP window for the status
// bar. Unlike LookParser (players — a bracketed "[ Name ]" header ending in
// "He is unwounded."), a monster look has no header: the monster's name is the
// first response line, a prose description follows, and the LAST line is
// "(It|He|She) appears to be <wound>." — the "appears to be" phrasing is what
// distinguishes it from a player look's "is unwounded."
//
// The game only ever tells you a monster's condition as a coarse wound band, not
// a number. Each band is a fixed percentage window of the monster's max HP
// (confirmed against live play: a 70-HP cave worm reading "heavily wounded" is
// 35–48 HP). So max HP (from game data) × the band gives an absolute HP range —
// invaluable for a high-HP boss with fast regen / self-heal, where you can't
// tally its HP by counting damage lines as they scroll past.
//
// Name → max HP resolution goes through RoomEntityClassifier
// (ResolveLookedMonsterNumber), which prefers the variant actually in the room so
// shared names ("giant rat", "skeleton") resolve to the right HP. Detection scans
// the lines buffered since the last prompt for the first that resolves to a
// monster — the server echoes the typed command as its own content line
// ("look ca") ahead of the name, so a blind "first line after prompt" would grab
// the echo.
public sealed partial class MonsterLookParser : IDisposable
{
    // Cap the per-block line buffer. A monster look is name + a few description
    // lines + the wound line; a generous cap bounds the classify scan without
    // ever truncating a real block.
    private const int MaxBufferedLines = 24;

    private readonly LineExtractor _lines;
    private readonly Func<string, int?> _resolveMonsterNumber;
    private readonly Func<int, int?> _maxHpForNumber;
    private readonly LogService? _log;

    private readonly List<string> _block = new();

    // Fires when a `look <monster>` response resolves to an HP window. Runs on
    // the LineExtractor's thread (UI thread in normal app use).
    public event Action<MonsterLookObserved>? TargetObserved;

    public MonsterLookParser(
        LineExtractor lines,
        Func<string, int?> resolveMonsterNumber,
        Func<int, int?> maxHpForNumber,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(resolveMonsterNumber);
        ArgumentNullException.ThrowIfNull(maxHpForNumber);
        _lines = lines;
        _resolveMonsterNumber = resolveMonsterNumber;
        _maxHpForNumber = maxHpForNumber;
        _log = log;
        _lines.LineEmitted += OnLineEmitted;
    }

    public void Dispose() => _lines.LineEmitted -= OnLineEmitted;

    // Test hook — feed plain lines without a live LineExtractor. isPrompt marks a
    // prompt row (block boundary); the echoed command and the name arrive as
    // non-prompt content lines just as they do on the wire.
    internal void Feed(string text, bool isPrompt = false)
        => HandleLine(text, isPrompt);

    private void OnLineEmitted(LineExtractor.EmittedLine line)
        => HandleLine(line.Text, line.IsPromptLine);

    private void HandleLine(string text, bool isPrompt)
    {
        if (isPrompt)
        {
            _block.Clear();
            return;
        }

        if (TryParseWoundLine(text, out string wound))
        {
            ResolveAndEmit(wound);
            _block.Clear();
            return;
        }

        if (_block.Count >= MaxBufferedLines) _block.RemoveAt(0);
        string trimmed = text.Trim();
        if (trimmed.Length > 0) _block.Add(trimmed);
    }

    private void ResolveAndEmit(string wound)
    {
        // First buffered line that resolves to a monster is the look target's
        // name — the command echo ("look ca") and prose description lines don't
        // classify as a monster, so they're skipped naturally.
        foreach (string candidate in _block)
        {
            int? number = _resolveMonsterNumber(candidate);
            if (number is not int n) continue;

            int? maxHp = _maxHpForNumber(n);
            if (maxHp is not int hp) return;   // known monster, but no usable HP row

            MonsterHpEstimate? est = EstimateHp(hp, wound);
            if (est is not { } estimate) return;

            _log?.Info("MonsterLook",
                $"look target '{candidate}' (#{n}, {hp} HP) {wound} → {estimate.Describe()}");
            TargetObserved?.Invoke(new MonsterLookObserved(candidate, estimate));
            return;
        }
    }

    // "(It|He|She|They) appears to be <wound>." — the monster-look condition line.
    // Player looks read "He is unwounded." (no "appears to be"), so this never
    // false-matches a player. Longest wound phrase first so "very critically"
    // isn't shadowed by "critically".
    public static bool TryParseWoundLine(string text, out string wound)
    {
        wound = string.Empty;
        if (string.IsNullOrEmpty(text)) return false;
        Match m = WoundLinePattern().Match(text);
        if (!m.Success) return false;
        wound = WhitespaceRun().Replace(m.Groups["wound"].Value.Trim(), " ").ToLowerInvariant();
        return true;
    }

    // Map a wound descriptor to an absolute HP window against a known max HP.
    // Bands (percentage of max HP, lower bound inclusive), validated live:
    //   unwounded ≥100 · slightly [85,100) · moderately [70,85) · heavily [50,70)
    //   · severely [30,50) · critically [20,30) · very critically (0,20)
    //   · mortally ≤0.
    // For a band [lo,hi): Low = ceil(lo·M/100), High = ceil(hi·M/100)−1, so the
    // window is exactly the integer HP values that read as that band. Returns
    // null for an unknown phrase or a non-positive max HP.
    public static MonsterHpEstimate? EstimateHp(int maxHp, string woundPhrase)
    {
        if (maxHp <= 0 || string.IsNullOrWhiteSpace(woundPhrase)) return null;
        string w = WhitespaceRun().Replace(woundPhrase.Trim(), " ").ToLowerInvariant();

        if (w == "unwounded") return new MonsterHpEstimate(maxHp, maxHp, Mortal: false);
        if (w == "mortally wounded") return new MonsterHpEstimate(0, 0, Mortal: true);

        (int Lo, int Hi)? band = w switch
        {
            "slightly wounded"        => (85, 100),
            "moderately wounded"      => (70, 85),
            "heavily wounded"         => (50, 70),
            "severely wounded"        => (30, 50),
            "critically wounded"      => (20, 30),
            "very critically wounded" => (0, 20),
            _                         => null,
        };
        if (band is not (int lo, int hi)) return null;

        int low  = Math.Max(1, CeilDiv(lo * maxHp, 100));
        int high = Math.Min(maxHp, CeilDiv(hi * maxHp, 100) - 1);
        if (high < low) high = low;   // tiny-max-HP bands can collapse; keep sane
        return new MonsterHpEstimate(low, high, Mortal: false);
    }

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;

    [GeneratedRegex(
        @"^\s*(?:It|He|She|They)\s+appears\s+to\s+be\s+(?<wound>very\s+critically\s+wounded|critically\s+wounded|mortally\s+wounded|severely\s+wounded|heavily\s+wounded|moderately\s+wounded|slightly\s+wounded|unwounded)\s*\.\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WoundLinePattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();
}

// An estimated HP window for a looked-at monster. Low..High are inclusive HP
// bounds; Low == High for an unwounded (full-HP) target. Mortal flags the
// ≤0 band — a monster at that point is effectively dead, so no positive window
// applies.
public readonly record struct MonsterHpEstimate(int Low, int High, bool Mortal)
{
    public bool IsFull => !Mortal && Low == High;

    public string Describe()
        => Mortal      ? "≤0"
         : IsFull      ? Low.ToString()
                       : $"{Low}-{High}";
}

// A resolved look observation: the monster's display name plus its estimated HP
// window. Name is the wire name the look echoed (may carry a size prefix).
public readonly record struct MonsterLookObserved(string Name, MonsterHpEstimate Estimate);
