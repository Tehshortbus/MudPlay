using System.Text;
using System.Text.RegularExpressions;
using FujinTerm.Services;
using FujinTerm.Terminal;

namespace FujinTerm.Game;

/// <summary>
/// Parses the in-game <c>stat</c> screen and writes every field onto
/// <see cref="PlayerStats"/>. Drives
/// <see cref="Remote.RemoteCommandManager.LivesProvider"/> so the
/// <c>@suicide</c> hard-block has a real value to gate against.
/// </summary>
/// <remarks>
/// <para>
/// State machine mirrors <see cref="TrainerMenuTracker"/>'s shape:
/// </para>
/// <list type="number">
///   <item>User sends <c>stat</c> outbound —
///         <see cref="ObserveOutbound"/> arms
///         <see cref="ExpectingScreenWindow"/> seconds of "expecting
///         stat output" time.</item>
///   <item>Within that window, every emitted line is scanned for
///         label / value pairs (<c>Name:</c>, <c>Lives/CP:</c>,
///         <c>Strength:</c>, etc.) — each match commits the value
///         directly to <see cref="PlayerStats"/>. Lines unrelated
///         to the stat screen pass through unchanged.</item>
///   <item>Once the window expires, scanning stops. The outbound
///         gate prevents chat-noise (e.g., a gossip line with
///         <c>"Strength: 60"</c>) from updating our state outside
///         the user-initiated stat poll.</item>
/// </list>
/// <para>
/// Once any field has been parsed at least once,
/// <see cref="HasParsed"/> flips true permanently for the session.
/// <c>RemoteCommandManager.LivesProvider</c> uses that flag to decide
/// whether to return <see cref="PlayerStats.Lives"/> or <c>null</c>
/// (which the suicide hard-block treats as "unknown → blocked").
/// </para>
/// </remarks>
public sealed partial class StatParser : IDisposable
{
    private LineExtractor? _lines;
    private readonly LogService? _log;
    private bool _disposed;

    public PlayerStats Stats { get; }

    /// <summary>Window after observing outbound <c>stat</c> during which incoming lines are scanned.</summary>
    public TimeSpan ExpectingScreenWindow { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Test seam.</summary>
    public Func<DateTime> NowProvider { get; set; } = () => DateTime.UtcNow;

    private DateTime? _windowOpenedAt;
    /// <summary>
    /// Per-arm flag — flipped true the first time a field commits
    /// within the current scan window, reset when the gate closes.
    /// Lets us close the gate as soon as the in-game prompt returns
    /// AFTER capture, instead of waiting for the full window timeout.
    /// </summary>
    private bool _capturedThisArm;

    /// <summary>True once any stat-screen line has been parsed this session.</summary>
    public bool HasParsed { get; private set; }

    public StatParser(PlayerStats stats, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(stats);
        _log   = log;
        Stats  = stats;
    }

    /// <summary>
    /// Bind the per-session <see cref="LineExtractor"/>. Same shape as
    /// <see cref="PartyManager.AttachLineExtractor"/> — the extractor
    /// is owned by the main-window VM (one per terminal session)
    /// while this parser is app-level. Calling again with a new
    /// extractor unhooks the previous one first.
    /// </summary>
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
    }

    /// <summary>
    /// Called by the wire-send path so we can spot the user's
    /// outbound <c>stat</c> command. Arming gate is the same shape
    /// <see cref="TrainerMenuTracker"/> uses — without an outbound
    /// <c>stat</c> within the window, every <c>OnLine</c> call is a
    /// no-op (protects against chat lines containing "Strength:" or
    /// similar from updating our stats).
    /// </summary>
    public void ObserveOutbound(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > 16) return;
        string raw = Encoding.Latin1.GetString(bytes);
        string cmd = raw.TrimEnd('\r', '\n', '\0').Trim().ToLowerInvariant();
        if (cmd != "stat") return;
        _windowOpenedAt = NowProvider();
        _capturedThisArm = false;
        _log?.Log(LogSeverity.Info, "StatParser",
            $"Observed outbound `stat` — armed {ExpectingScreenWindow.TotalSeconds:0}s scan window.");
    }

    // ----- Test seam -----------------------------------------------------
    /// <summary>Test seam — arm the scanner without going through the wire-observation path.</summary>
    internal void TestArm() => _windowOpenedAt = NowProvider();

    /// <summary>
    /// Test seam — pump a line through the full handler path without
    /// a real <see cref="LineExtractor"/>. Mirrors <see cref="OnLine"/>
    /// so tests exercise the always-on lives handler + the gated
    /// scan together. <paramref name="isPromptLine"/> defaults to
    /// false; set true for tests that want to exercise the
    /// close-on-prompt-after-capture path.
    /// </summary>
    internal void FeedTestLine(string text, bool isPromptLine = false)
    {
        OnLivesRemainingLine(text);
        if (_windowOpenedAt is null) return;
        if (isPromptLine && _capturedThisArm)
        {
            _windowOpenedAt = null;
            _capturedThisArm = false;
            return;
        }
        if (NowProvider() - _windowOpenedAt.Value > ExpectingScreenWindow)
        {
            _windowOpenedAt = null;
            _capturedThisArm = false;
            return;
        }
        bool hadParsed = HasParsed;
        ScanLine(text);
        if (HasParsed && !hadParsed) _capturedThisArm = true;
    }

    private void OnLine(LineExtractor.EmittedLine line)
    {
        // Lives-remaining (miracle save) — always-on, independent of
        // the stat-screen scan window.
        OnLivesRemainingLine(line.Text);

        if (_windowOpenedAt is null) return;

        // Close the gate as soon as the in-game prompt fires AFTER we
        // captured at least one field this arm. The stat screen
        // terminates with the next `[HP=...]:` prompt, which arrives
        // milliseconds after the burst — long before any human
        // keystroke could land. Without this, the gate stays open
        // for the full 5 s and a fast typist could land a command
        // whose echo contains "Strength: N" and corrupt the field.
        if (line.IsPromptLine && _capturedThisArm)
        {
            _windowOpenedAt = null;
            _capturedThisArm = false;
            return;
        }

        if (NowProvider() - _windowOpenedAt.Value > ExpectingScreenWindow)
        {
            _windowOpenedAt = null;
            _capturedThisArm = false;
            return;
        }

        bool hadParsed = HasParsed;
        ScanLine(line.Text);
        if (HasParsed && !hadParsed) _capturedThisArm = true;
    }

    /// <summary>
    /// Single-line scan — apply every field regex and update the
    /// corresponding <see cref="PlayerStats"/> property on a match.
    /// Multiple labels can appear on one stat-screen row
    /// (e.g., <c>"Race: Dark-Elf       Exp: 0          Perception: 50"</c>),
    /// so we run every pattern against every line — at most one field
    /// per pattern is updated per call.
    /// </summary>
    private void ScanLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // Chat-line shape guard — any line that opens with
        // `<player> <verb>:` is chat or a self-echo of chat the user
        // typed. Skipped wholesale so a message like
        // "Foo gossips: my Strength: 60 sucks" can't write to the
        // Strength field even if the gate happens to be open. Pairs
        // with the close-on-prompt-after-capture path: between the
        // two, the only lines that ever reach the field regexes
        // during a scan window are non-chat, non-prompt server lines
        // — i.e. the actual stat-screen output.
        if (ChatLineRx().IsMatch(text)) return;

        // String-valued fields are caught first — they have the
        // non-greedy "up to two spaces" cutoff so multi-word values
        // like "Dark-Elf" / "Fujin WuzHere" capture fully without
        // bleeding into the next label.
        TryString(text, NameRx(),  v => Stats.Name  = v);
        TryString(text, RaceRx(),  v => Stats.Race  = v);
        TryString(text, ClassRx(), v => Stats.Class = v);

        // Paired N/M fields.
        TryPair(text, LivesCpRx(),     (a, b) => { Stats.Lives = a; Stats.Cp = b; });
        TryPair(text, HitsRx(),        (a, b) => { Stats.Hits = a; Stats.MaxHits = b; });
        TryPair(text, KaiRx(),         (a, b) => { Stats.Kai  = a; Stats.MaxKai  = b; });
        TryPair(text, ManaRx(),        (a, b) => { Stats.Mana = a; Stats.MaxMana = b; });
        TryPair(text, ArmourClassRx(), (a, b) => { Stats.ArmourClass = a; Stats.MaxArmourClass = b; });

        // Plain N fields. The `\*?` in every numeric regex tolerates
        // the asterisk that altered (buffed / cursed) stats prefix
        // their value with — e.g. `Strength: *80`. We strip the
        // asterisk and capture the raw post-modifier value (the
        // altered-or-not distinction isn't surfaced anywhere yet;
        // can be added as parallel bool fields if a future consumer
        // wants it).
        TryInt(text, LevelRx(),        v => Stats.Level        = v);
        TryInt(text, ExpRx(),          v => Stats.Exp          = v);
        TryInt(text, PerceptionRx(),   v => Stats.Perception   = v);
        TryInt(text, StealthRx(),      v => Stats.Stealth      = v);
        TryInt(text, ThieveryRx(),     v => Stats.Thievery     = v);
        TryInt(text, TrapsRx(),        v => Stats.Traps        = v);
        TryInt(text, PicklocksRx(),    v => Stats.Picklocks    = v);
        TryInt(text, TrackingRx(),     v => Stats.Tracking     = v);
        TryInt(text, StrengthRx(),     v => Stats.Strength     = v);
        TryInt(text, IntellectRx(),    v => Stats.Intellect    = v);
        TryInt(text, WillpowerRx(),    v => Stats.Willpower    = v);
        TryInt(text, AgilityRx(),      v => Stats.Agility      = v);
        TryInt(text, HealthRx(),       v => Stats.Health       = v);
        TryInt(text, CharmRx(),        v => Stats.Charm        = v);
        TryInt(text, MartialArtsRx(),  v => Stats.MartialArts  = v);
        TryInt(text, MagicResRx(),     v => Stats.MagicRes     = v);
        TryInt(text, SpellcastingRx(), v => Stats.Spellcasting = v);
    }

    /// <summary>
    /// Always-on handler for the post-miracle-save line
    /// <c>"You have N lives left."</c>. Updates
    /// <see cref="PlayerStats.Lives"/> immediately so the
    /// <c>@suicide</c> hard-block reflects the new count without
    /// waiting for the next user-issued <c>stat</c>. Bypasses the
    /// outbound-`stat` gate because this line is server-emitted as a
    /// game event, not part of a stat block.
    /// </summary>
    private void OnLivesRemainingLine(string text)
    {
        Match m = LivesRemainingRx().Match(text);
        if (!m.Success) return;
        if (!int.TryParse(m.Groups[1].Value,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int lives)) return;
        Stats.Lives = lives;
        HasParsed = true;
        _log?.Log(LogSeverity.Info, "StatParser",
            $"Updated Lives → {lives} from miracle-save line.");
    }

    private void TryString(string text, Regex rx, Action<string> set)
    {
        Match m = rx.Match(text);
        if (!m.Success) return;
        set(m.Groups[1].Value.Trim());
        HasParsed = true;
    }

    private void TryInt(string text, Regex rx, Action<int> set)
    {
        Match m = rx.Match(text);
        if (!m.Success) return;
        if (!int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int v)) return;
        set(v);
        HasParsed = true;
    }

    private void TryPair(string text, Regex rx, Action<int, int> set)
    {
        Match m = rx.Match(text);
        if (!m.Success) return;
        System.Globalization.NumberStyles ns = System.Globalization.NumberStyles.Integer;
        System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
        if (!int.TryParse(m.Groups[1].Value, ns, inv, out int a)) return;
        if (!int.TryParse(m.Groups[2].Value, ns, inv, out int b)) return;
        set(a, b);
        HasParsed = true;
    }

    // ----- Regexes -------------------------------------------------------
    // Source-generated for hot-path efficiency. String labels use the
    // `(?=\s{2,}|$)` lookahead to stop the value at two-space gutters
    // between row columns (so "Name: Fujin WuzHere    Lives/CP: ..." captures
    // just "Fujin WuzHere"). Numeric labels capture digits only.

    [GeneratedRegex(@"\bName:\s+(\S[\w '\-]*?)(?=\s{2,}|$)",      RegexOptions.CultureInvariant)] private static partial Regex NameRx();
    [GeneratedRegex(@"\bRace:\s+(\S[\w '\-]*?)(?=\s{2,}|$)",      RegexOptions.CultureInvariant)] private static partial Regex RaceRx();
    [GeneratedRegex(@"\bClass:\s+(\S[\w '\-]*?)(?=\s{2,}|$)",     RegexOptions.CultureInvariant)] private static partial Regex ClassRx();

    // Paired N/M fields — Hits / Kai / Mana / Armour Class allow `*`
    // between the colon and digits to capture altered values
    // ("Hits: *22/22"). Lives/CP is never altered in-game so doesn't
    // need the tolerance.
    [GeneratedRegex(@"\bLives/CP:\s+(\d+)/(\d+)",                       RegexOptions.CultureInvariant)] private static partial Regex LivesCpRx();
    [GeneratedRegex(@"\bHits:\s+\*?\s*(\d+)/(\d+)",                     RegexOptions.CultureInvariant)] private static partial Regex HitsRx();
    [GeneratedRegex(@"\bKai:\s+\*?\s*(\d+)/(\d+)",                      RegexOptions.CultureInvariant)] private static partial Regex KaiRx();
    [GeneratedRegex(@"\bMana:\s+\*?\s*(\d+)/(\d+)",                     RegexOptions.CultureInvariant)] private static partial Regex ManaRx();
    [GeneratedRegex(@"\bArmour Class:\s+\*?\s*(\d+)/(\d+)",             RegexOptions.CultureInvariant)] private static partial Regex ArmourClassRx();

    // Plain N fields — `\*?` between the colon and the digits
    // tolerates the altered-stat marker.
    [GeneratedRegex(@"\bLevel:\s+(\d+)",                                RegexOptions.CultureInvariant)] private static partial Regex LevelRx();
    [GeneratedRegex(@"\bExp:\s+(\d+)",                                  RegexOptions.CultureInvariant)] private static partial Regex ExpRx();
    [GeneratedRegex(@"\bPerception:\s+\*?\s*(\d+)",                     RegexOptions.CultureInvariant)] private static partial Regex PerceptionRx();
    [GeneratedRegex(@"\bStealth:\s+\*?\s*(\d+)",                        RegexOptions.CultureInvariant)] private static partial Regex StealthRx();
    [GeneratedRegex(@"\bThievery:\s+\*?\s*(\d+)",                       RegexOptions.CultureInvariant)] private static partial Regex ThieveryRx();
    [GeneratedRegex(@"\bTraps:\s+\*?\s*(\d+)",                          RegexOptions.CultureInvariant)] private static partial Regex TrapsRx();
    [GeneratedRegex(@"\bPicklocks:\s+\*?\s*(\d+)",                      RegexOptions.CultureInvariant)] private static partial Regex PicklocksRx();
    [GeneratedRegex(@"\bTracking:\s+\*?\s*(\d+)",                       RegexOptions.CultureInvariant)] private static partial Regex TrackingRx();
    [GeneratedRegex(@"\bStrength:\s+\*?\s*(\d+)",                       RegexOptions.CultureInvariant)] private static partial Regex StrengthRx();
    [GeneratedRegex(@"\bIntellect:\s+\*?\s*(\d+)",                      RegexOptions.CultureInvariant)] private static partial Regex IntellectRx();
    [GeneratedRegex(@"\bWillpower:\s+\*?\s*(\d+)",                      RegexOptions.CultureInvariant)] private static partial Regex WillpowerRx();
    [GeneratedRegex(@"\bAgility:\s+\*?\s*(\d+)",                        RegexOptions.CultureInvariant)] private static partial Regex AgilityRx();
    [GeneratedRegex(@"\bHealth:\s+\*?\s*(\d+)",                         RegexOptions.CultureInvariant)] private static partial Regex HealthRx();
    [GeneratedRegex(@"\bCharm:\s+\*?\s*(\d+)",                          RegexOptions.CultureInvariant)] private static partial Regex CharmRx();
    [GeneratedRegex(@"\bMartial Arts:\s+\*?\s*(\d+)",                   RegexOptions.CultureInvariant)] private static partial Regex MartialArtsRx();
    [GeneratedRegex(@"\bMagicRes:\s+\*?\s*(\d+)",                       RegexOptions.CultureInvariant)] private static partial Regex MagicResRx();
    [GeneratedRegex(@"\bSpellcasting:\s+\*?\s*(\d+)",                   RegexOptions.CultureInvariant)] private static partial Regex SpellcastingRx();

    // Always-on miracle-save line — fires outside the stat-screen
    // window. "You have N lives left." / "You have 1 life left."
    [GeneratedRegex(@"^You have (\d+) (?:lives?|life) left\.",          RegexOptions.CultureInvariant)] private static partial Regex LivesRemainingRx();

    // Chat-line shape — matched at line start. Any of the standard
    // MajorMUD chat verbs after a single-word speaker means the
    // entire line is chat noise (including the user's own outgoing
    // gossip / say echoes). Used as a skip-guard in ScanLine so a
    // chat line embedding a stat label can't write to PlayerStats
    // even if it lands inside the scan window.
    [GeneratedRegex(@"^\w+\s+(?:gossips|telepaths|yells|says|auctions|gangpaths|broadcasts):",
        RegexOptions.CultureInvariant)] private static partial Regex ChatLineRx();
}
