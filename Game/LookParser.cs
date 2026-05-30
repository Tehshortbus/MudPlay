using System.Text.RegularExpressions;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Terminal;

namespace FujinTerm.Game;

/// <summary>
/// Stateful parser that pulls race + class + equipment loadout out of
/// the <c>look &lt;player&gt;</c> response and feeds them to
/// <see cref="PlayerDatabase.RecordLook"/>. Sibling to
/// <see cref="WhoListParser"/>; subscribes to
/// <see cref="LineExtractor.LineEmitted"/> on its own because the look
/// block spans multiple lines and needs state.
/// </summary>
/// <remarks>
/// <para>
/// Expected shape (verbatim from the user's session against Playpen BBS):
/// </para>
/// <code>
/// [ Fujin WuzHere ]
/// Fujin is a healthy, well built Dark-Elf Mystic with short black
/// hair and black eyes.  He moves very swiftly, and is quite
/// unfriendly and aloof.  Fujin appears to be bright and seems sullen
/// and impulsive.  He is unwounded.
///
/// He is equipped with:
/// padded vest                     (Torso)
/// padded pants                    (Legs)
/// quarterstaff                    (Weapon Hand)
/// </code>
/// <para>
/// Or, when no equipment is worn:
/// </para>
/// <code>
/// He is equipped with:
///
/// Nothing
/// </code>
/// <para>
/// Race + class are detected by substring-matching the joined
/// description sentence against the static <see cref="RaceTokens"/>
/// and <see cref="ClassTokens"/> lists — longest match wins so
/// <c>"Dark-Elf"</c> beats <c>"Elf"</c>. We deliberately don't write
/// a positional regex over the description sentence because the
/// adjective sequence (healthy / well built / etc.) varies with the
/// character's stats and the gender pronoun.
/// </para>
/// </remarks>
public sealed partial class LookParser : IDisposable
{
    private readonly LineExtractor _lines;
    private readonly PlayerDatabase _db;
    private readonly LogService? _log;

    private State _state = State.Idle;
    private string? _currentName;
    private readonly List<string> _descriptionLines = new();
    private readonly List<EquipmentItem> _equipment = new();
    private bool _sawEquipmentMarker;

    /// <summary>
    /// Standard MajorMUD race names — adapted from
    /// megamind-mud/megamind-client's realmData.js default race list.
    /// Order: longer-first compounds before their bare base forms so
    /// the simple "first substring hit" detection still produces the
    /// canonical name (we also longest-match in <see cref="InferToken"/>).
    /// </summary>
    public static readonly string[] RaceTokens =
    {
        "Dark-Elf", "Half-Elf", "Half-Orc", "Half-Ogre", "Gaunt One",
        "Human", "Dwarf", "Gnome", "Halfling", "Elf",
        "Goblin", "Kang", "Nekojin",
    };

    /// <summary>Standard MajorMUD class names — same source as <see cref="RaceTokens"/>.</summary>
    public static readonly string[] ClassTokens =
    {
        "Witchunter", "Missionary", "Warrior", "Paladin", "Cleric",
        "Priest", "Ninja", "Thief", "Bard", "Gypsy",
        "Warlock", "Mage", "Druid", "Ranger", "Mystic",
    };

    public LookParser(LineExtractor lines, PlayerDatabase db, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(db);
        _lines = lines;
        _db = db;
        _log = log;
        _lines.LineEmitted += OnLineEmitted;
    }

    public void Dispose() => _lines.LineEmitted -= OnLineEmitted;

    /// <summary>
    /// Test hook — drive plain text lines without spinning up a real
    /// <see cref="LineExtractor"/>. Each line is fed as a non-prompt
    /// EmittedLine; pass <c>isPromptLine: true</c> via
    /// <see cref="FeedPromptLine"/> to end the block.
    /// </summary>
    internal void FeedTestLines(IEnumerable<string> lines, DateTime? nowUtc = null)
    {
        DateTime when = nowUtc ?? DateTime.UtcNow;
        foreach (string text in lines)
            HandleLine(text, isPromptLine: false, when);
    }

    internal void FeedPromptLine(string text, DateTime? nowUtc = null)
        => HandleLine(text, isPromptLine: true, nowUtc ?? DateTime.UtcNow);

    private void OnLineEmitted(LineExtractor.EmittedLine line)
    {
        HandleLine(line.Text, line.IsPromptLine, line.Timestamp.UtcDateTime);
    }

    private void HandleLine(string text, bool isPromptLine, DateTime nowUtc)
    {
        // Prompt = server done responding. Same terminator semantics
        // WhoListParser uses — emit whatever we've collected, return
        // to Idle. (The prompt row itself doesn't always emit promptly
        // — see WhoListParser comments — so we also end the equipment
        // block on the first blank line after the equipment marker.)
        if (isPromptLine)
        {
            if (_state != State.Idle) EndBlock(nowUtc);
            return;
        }

        switch (_state)
        {
            case State.Idle:
                Match nameMatch = NameHeaderPattern().Match(text);
                if (nameMatch.Success)
                {
                    _currentName = nameMatch.Groups["name"].Value.Trim();
                    _descriptionLines.Clear();
                    _equipment.Clear();
                    _sawEquipmentMarker = false;
                    _state = State.Description;
                    _log?.Info("LookParser", $"look response started for '{_currentName}'");
                }
                break;

            case State.Description:
                // Skip blank padding between header and description.
                if (string.IsNullOrWhiteSpace(text)) break;
                if (EquipmentHeaderPattern().IsMatch(text))
                {
                    _sawEquipmentMarker = true;
                    _state = State.Equipment;
                    break;
                }
                _descriptionLines.Add(text);
                break;

            case State.Equipment:
                if (string.IsNullOrWhiteSpace(text))
                {
                    // Blank after the equipment marker is common padding
                    // before the items begin (the "Nothing" case has one
                    // right before the word). Once we've parsed at least
                    // one item, a blank ends the block.
                    if (_equipment.Count > 0) EndBlock(nowUtc);
                    break;
                }

                // Explicit "Nothing" = empty loadout. Could be padded
                // with leading whitespace; trim first.
                if (text.Trim().Equals("Nothing", StringComparison.OrdinalIgnoreCase))
                {
                    EndBlock(nowUtc);
                    break;
                }

                Match equipMatch = EquipmentLinePattern().Match(text);
                if (equipMatch.Success)
                {
                    string item = equipMatch.Groups["item"].Value.Trim();
                    string slot = equipMatch.Groups["slot"].Value.Trim();
                    _equipment.Add(new EquipmentItem(slot, item));
                }
                else
                {
                    // Non-blank, non-equipment line ends the block —
                    // typically the trailing prompt or unrelated text.
                    // Re-feed through Idle in case it's the next look's
                    // header (back-to-back look calls).
                    EndBlock(nowUtc);
                    HandleLine(text, isPromptLine, nowUtc);
                }
                break;
        }
    }

    private void EndBlock(DateTime nowUtc)
    {
        if (_currentName is null)
        {
            _state = State.Idle;
            return;
        }

        string description = string.Join(' ', _descriptionLines);
        string? race  = InferToken(description, RaceTokens);
        string? cls   = InferToken(description, ClassTokens);

        // Equipment list semantics:
        //   - parsed at least one item → record the snapshot.
        //   - saw the marker but parsed zero items (the "Nothing"
        //     branch above, or just the marker + no items observed) →
        //     empty list = explicit "naked".
        //   - never saw the marker (the look block was truncated /
        //     interrupted) → null = leave existing equipment intact.
        IReadOnlyList<EquipmentItem>? equipment =
            _sawEquipmentMarker ? _equipment.ToArray() : null;

        _db.RecordLook(_currentName, race, cls, equipment, nowUtc);

        _log?.Info("LookParser",
            $"look response complete for '{_currentName}' — race: {race ?? "?"}, " +
            $"class: {cls ?? "?"}, equipment: " +
            (equipment is null ? "unchanged" : $"{equipment.Count} item(s)"));

        _state = State.Idle;
        _currentName = null;
        _descriptionLines.Clear();
        _equipment.Clear();
        _sawEquipmentMarker = false;
    }

    /// <summary>
    /// Longest-match substring search — picks the longest token from
    /// <paramref name="tokens"/> that appears in <paramref name="haystack"/>
    /// (case-insensitive). Longest-match ensures <c>"Dark-Elf"</c> wins
    /// over <c>"Elf"</c> and <c>"Half-Elf"</c> wins over <c>"Elf"</c> or
    /// <c>"Half"</c>. Returns <c>null</c> when nothing matches.
    /// </summary>
    private static string? InferToken(string haystack, IReadOnlyList<string> tokens)
    {
        if (string.IsNullOrEmpty(haystack)) return null;
        string? best = null;
        foreach (string t in tokens)
        {
            if (haystack.Contains(t, StringComparison.OrdinalIgnoreCase) &&
                (best is null || t.Length > best.Length))
            {
                best = t;
            }
        }
        return best;
    }

    /// <summary>
    /// Bracketed player name header — e.g. <c>"[ Fujin WuzHere ]"</c>.
    /// Restricted to alphabetic content + spaces so unrelated bracketed
    /// lines (chat tags, status codes) don't false-trigger.
    /// </summary>
    [GeneratedRegex(@"^\s*\[\s+(?<name>[A-Za-z][A-Za-z' -]*?)\s+\]\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex NameHeaderPattern();

    /// <summary>"He / She / It / They is/are equipped with:" — pronoun varies.</summary>
    [GeneratedRegex(@"^(He|She|It|They)\s+(is|are)\s+equipped\s+with:\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EquipmentHeaderPattern();

    /// <summary>
    /// One equipment line — item name (greedy up to the last space
    /// before the slot parens) then <c>(SlotLabel)</c>. Greedy on the
    /// item lets names that themselves contain a single internal space
    /// land cleanly; the lazy approach would split on the first run
    /// of spaces inside the name.
    /// </summary>
    [GeneratedRegex(@"^\s*(?<item>\S.*\S)\s+\((?<slot>[^)]+)\)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex EquipmentLinePattern();

    private enum State { Idle, Description, Equipment }
}
