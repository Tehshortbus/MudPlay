using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using FujinTerm.Game.GameData;
using FujinTerm.Game.Map;
using FujinTerm.Game.Quests;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// Presentation formatting shared by the Quest Status tab and the Quest editor window —
// turns crawled quest mechanics (CrawledQuest / QuestStep) into the human-readable
// labels both surfaces render. Pure functions over the active GameDataCache; no state.
internal static partial class QuestTextFormatter
{
    // Auto-draft title for a quest when the user hasn't named it: the flag's ability
    // name for a single-part quest; for a multi-part band, the flag's base name (its
    // trailing "Quest" dropped) plus the 1-based band number — e.g. "Good 1" from the
    // GoodQuest flag's first band.
    public static string FallbackTitle(CrawledQuest q)
    {
        string flagName = AbilityNames.FormatId(q.Flag);
        return q.BandOrdinal > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{StripQuestSuffix(flagName)} {q.BandOrdinal}")
            : flagName;
    }

    // Drop a trailing "Quest" so the alignment band names read "Good 1" not
    // "GoodQuest 1"; leave a name that is only "Quest" (nothing else) intact.
    private static string StripQuestSuffix(string name) =>
        name.Length > 5 && name.EndsWith("Quest", StringComparison.Ordinal)
            ? name[..^5]
            : name;

    // Level-gate label ("Level N"), or empty when ungated.
    public static string Level(int level) =>
        level > 0 ? string.Create(CultureInfo.InvariantCulture, $"Level {level}") : string.Empty;

    // Class-resolved permanent stat-bonus summary, or empty when the quest grants none.
    public static string Bonuses(IReadOnlyList<QuestBonus> bonuses) =>
        bonuses.Count == 0 ? string.Empty
            : AbilityNames.SummarizeAbilities(bonuses.Select(b => (b.AbilityId, b.Value)));

    // The quest's reward label: comma-joined keeper-item award names, or — when the
    // quest awards no item or stat but the ability it grants is the prize (Smash,
    // Meditate, SeeHidden) — the flag's ability name. Empty when neither.
    public static string Awards(GameDataCache gameData, CrawledQuest q) =>
        q.AwardItems.Count > 0
            ? string.Join(", ", q.AwardItems.Select(id => ItemName(gameData, id)))
            : q.AwardsAbility ? AbilityNames.FormatId(q.Flag) : string.Empty;

    // The quest's completion experience, thousands-separated with an "exp" suffix
    // ("1,500,000 exp"); empty when the quest (or band) hands none. A distinct reward
    // line from the keeper-item award — this is the raw exp the give-chain grants.
    public static string Experience(CrawledQuest q) =>
        q.ExpAward > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{q.ExpAward:N0} exp")
            : string.Empty;

    // The class / race the crawl found this quest restricted to, as
    // "Classes: Warrior, Cleric  ·  Races: Gaunt One"; empty when the quest is open to
    // all (no restriction surfaced). Informational — the crawl reads guards off the
    // grant chains and can't see gating that lives upstream in the textblock flow, so
    // this is "what the crawl grabbed", not a hard eligibility verdict.
    public static string Requirements(GameDataCache gameData, CrawledQuest q)
    {
        var parts = new List<string>();
        if (q.ClassIds is { Count: > 0 } cls)
            parts.Add("Classes: " + string.Join(", ", cls.Select(id => ClassRequirement(gameData, id, q.ClassLevels))));
        if (q.RaceIds is { Count: > 0 } rcs)
            parts.Add("Races: " + string.Join(", ", rcs.Select(id => RestrictionName(gameData, "Races", id))));
        return string.Join("  ·  ", parts);
    }

    // A restricted class with its own level gate appended ("Priest-20"), or the bare
    // class name when the quest carries no per-class level for it. Lets a multi-class
    // ability quest (Smash, Meditate) show each class's distinct unlock level.
    private static string ClassRequirement(GameDataCache gameData, int id, IReadOnlyDictionary<int, int>? levels)
    {
        string name = RestrictionName(gameData, "Classes", id);
        return levels is not null && levels.TryGetValue(id, out int lvl) && lvl > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{name}-{lvl}")
            : name;
    }

    private static string RestrictionName(GameDataCache gameData, string table, int number) =>
        gameData.FindNameByNumber(table, number)
        ?? string.Create(CultureInfo.InvariantCulture, $"#{number}");

    // One followable step drafted in the hand-written guide's own shape:
    //   (map/room) `command` (item note)
    // The Called-From location's rooms become clickable (map/room) links (all of
    // them, for a multi-room list); a player command is backtick-wrapped as the
    // literal to type; a command-less step sourced from a monster's textblock is
    // narrated "kill <monster> (<drop>)" and a bare item grant "obtain <item>",
    // matching how the seed guides read. Items the step needs / turns in trail as
    // a parenthetical note.
    public static string Step(GameDataCache gameData, QuestStep s,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms = null)
    {
        var segments = new List<string>();

        string granted = string.Join(", ", s.GrantedItems.Select(id => ItemName(gameData, id)));

        int monster = 0;
        bool isKill = string.IsNullOrWhiteSpace(s.Command) && TryMonsterRef(s.Location, out monster);

        // Room link(s): a command / obtain step names its room in the Called-From
        // location; a kill step's location is the monster itself, so its room comes
        // from where the quest places (or summons) that monster.
        string rooms = isKill ? MonsterRoomLinks(monster, monsterRooms) : RoomLinks(s.Location);
        if (rooms.Length > 0) segments.Add(rooms);

        if (!string.IsNullOrWhiteSpace(s.Command))
        {
            segments.Add($"`{s.Command!.Trim()}`");
            if (granted.Length > 0) segments.Add($"(get {granted})");
        }
        else if (isKill)
        {
            string name = MonsterName(gameData, monster);
            segments.Add(granted.Length > 0 ? $"kill {name} ({granted})" : $"kill {name}");
        }
        else if (granted.Length > 0)
        {
            segments.Add($"obtain {granted}");
        }

        if (s.TurnInItems.Count > 0)
            segments.Add("(turn in " + string.Join(", ", s.TurnInItems.Select(id => ItemName(gameData, id))) + ")");
        if (s.RequiredItems.Count > 0)
            segments.Add("(" + string.Join(", ", s.RequiredItems.Select(id => ItemName(gameData, id))) + " required)");

        return segments.Count > 0
            ? string.Join(" ", segments)
            : string.Create(CultureInfo.InvariantCulture, $"Step {s.Order}");
    }

    // The Called-From location's room coordinates as space-joined (map/room) link
    // tokens — every room in a multi-room list, so each renders as its own walk-to
    // link. Empty when the location names no room (a Monster / Spell / Textblock ref).
    private static string RoomLinks(string? location) =>
        string.IsNullOrWhiteSpace(location)
            ? string.Empty
            : string.Join(" ", RoomRef().Matches(location)
                .Select(m => string.Create(CultureInfo.InvariantCulture, $"({m.Groups[1].Value}/{m.Groups[2].Value})")));

    // A kill step's room link(s): every room the quest places the target monster in,
    // as space-joined (map/room) tokens, drawn from the pre-built placement map so no
    // per-step room scan happens. Empty when the monster has no resolved placement —
    // the kill step then renders room-less rather than offering a dead link.
    private static string MonsterRoomLinks(int monster,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms) =>
        monsterRooms is not null && monsterRooms.TryGetValue(monster, out IReadOnlyList<RoomKey>? keys)
            ? string.Join(" ", keys.Select(k =>
                string.Create(CultureInfo.InvariantCulture, $"({k.Map}/{k.Room})")))
            : string.Empty;

    // A command-less step whose chain is Called-From a monster is a kill step —
    // the monster number to narrate. Matches the hand-written guides' convention
    // of writing monster-granted quest items as "kill <monster> (<drop>)".
    private static bool TryMonsterRef(string? location, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(location)) return false;
        Match m = MonsterRef().Match(location);
        return m.Success && int.TryParse(m.Groups[1].Value, out number);
    }

    private static string MonsterName(GameDataCache gameData, int number) =>
        gameData.FindNameByNumber("Monsters", number)
        ?? string.Create(CultureInfo.InvariantCulture, $"monster #{number}");

    // Item display name by id, falling back to #id when the active set has no such row.
    public static string ItemName(GameDataCache gameData, int id) =>
        gameData.FindNameByNumber("Items", id)
        ?? string.Create(CultureInfo.InvariantCulture, $"#{id}");

    // The crawler's auto-draft followable steps for a quest, one markdown line per
    // give-step in order — each a checkbox line "[] {step}" (the seed guides carry no
    // flag/order prefix, so the draft matches them) so the Quest Status tab renders it
    // as a tickable item and the editor pre-fills it verbatim. For a multi-part band
    // only the give-steps inside the band's StepRangeStart..StepRangeEnd span are
    // emitted; a single-part quest (range 0/0) emits every step. Empty when the flag
    // drafts no steps.
    public static IReadOnlyList<string> StepLines(GameDataCache gameData, CrawledQuest q,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms = null)
    {
        var lines = new List<string>();
        var seenOrders = new HashSet<int>();
        foreach (QuestStep s in QuestStepGraph.Build(gameData, q.Flag, q.ProgressByValue))
        {
            // Value-laddered bands legitimately carry several distinct steps that all
            // land on the same ability value, so the give-step-order dedup (which folds
            // one give-step echoed from many rooms) only applies on the give-step axis.
            if (!q.ProgressByValue && !seenOrders.Add(s.Order)) continue;
            if (q.StepRangeEnd > 0 && (s.Order < q.StepRangeStart || s.Order > q.StepRangeEnd)) continue;
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"[] {Step(gameData, s, monsterRooms)}"));
        }
        return lines;
    }

    // Parse user-or-crawler step markdown into render rows. Each non-blank line is one
    // row: a leading [] / [ ] / [x] marker makes it a tickable checkbox whose label is
    // the text after the marker; a line with no marker is a plain, non-tickable label.
    // Blank lines are skipped.
    public static IEnumerable<(bool Checkable, string Text)> ParseStepLines(string steps)
    {
        if (string.IsNullOrEmpty(steps)) yield break;
        foreach (string raw in steps.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            Match m = CheckboxMarker().Match(line);
            if (m.Success)
                yield return (true, line[m.Length..].TrimStart());
            else
                yield return (false, line);
        }
    }

    // Leading checkbox marker: "[", optional whitespace, optional x/X, optional
    // whitespace, "]". The text after it is the row label.
    [GeneratedRegex(@"^\[\s*[xX]?\s*\]")]
    private static partial Regex CheckboxMarker();

    // Split a step label into render segments, isolating any (map/room) coordinate token
    // (e.g. (5/297)) into its own segment carrying the parsed RoomKey so the view can
    // render it as a clickable walk-to link; the surrounding prose stays as plain
    // segments (null room). A coordinate whose numbers don't fit a positive int is left
    // folded into the prose. Returns a single plain segment when the label holds no
    // coordinate, and an empty list for empty input.
    public static IReadOnlyList<(string Text, RoomKey? Room)> SplitRoomLinks(string text)
    {
        var segments = new List<(string Text, RoomKey? Room)>();
        if (string.IsNullOrEmpty(text)) return segments;

        int pos = 0;
        foreach (Match m in RoomLink().Matches(text))
        {
            // Non-positive or over-range coordinate: not a real room — leave the
            // token in the prose run rather than offering a dead link.
            if (!int.TryParse(m.Groups[1].Value, out int map)
                || !int.TryParse(m.Groups[2].Value, out int room)
                || map <= 0 || room <= 0)
                continue;

            if (m.Index > pos) segments.Add((text[pos..m.Index], null));
            segments.Add((m.Value, new RoomKey(map, room)));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) segments.Add((text[pos..], null));
        return segments;
    }

    // Map/room coordinate token: "(", digits, "/", digits, ")" — the link target.
    [GeneratedRegex(@"\((\d+)/(\d+)\)")]
    private static partial Regex RoomLink();

    // "Room 9/1259" inside a Called-From string — a location's room reference.
    [GeneratedRegex(@"Room\s+(\d+)/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RoomRef();

    // "Monster #39" inside a Called-From string — a monster-sourced chain.
    [GeneratedRegex(@"Monster\s+#(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MonsterRef();
}
