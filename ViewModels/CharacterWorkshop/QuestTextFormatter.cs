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
    // a parenthetical note. Falls back to a bare "Step N" label when the crawl
    // captured nothing followable (see StepOrNull) — kept for any caller that wants
    // a label for every step; the auto-draft (StepLines) drops those steps instead.
    public static string Step(GameDataCache gameData, QuestStep s,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms = null,
        ItemSourceIndex? itemSources = null)
        => StepOrNull(gameData, s, monsterRooms, itemSources)
           ?? string.Create(CultureInfo.InvariantCulture, $"Step {s.Order}");

    // The step's followable body — room links, command, kill/obtain narration and
    // item notes joined into one line — or null when the crawl captured no action
    // the player can take. Many quest steps are pure flag-advances the crawl can't
    // render: an alignment ladder's automatic value ticks, a story textblock the
    // player never directly triggers (Called-From another textblock/spell, no room,
    // no command, no item). Those carry nothing to do, so the auto-draft omits them
    // rather than listing an opaque "Step 31" the player can't act on.
    public static string? StepOrNull(GameDataCache gameData, QuestStep s,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms = null,
        ItemSourceIndex? itemSources = null)
    {
        var segments = new List<string>();

        string granted = string.Join(", ", s.GrantedItems.Select(id => ItemName(gameData, id)));

        int monster = 0;
        bool monsterLoc = TryMonsterRef(s.Location, out monster);
        bool isKill = monsterLoc && string.IsNullOrWhiteSpace(s.Command);

        // Room link(s): a monster-anchored step (a kill target, or an NPC the step
        // asks) links that monster's placement room; a room-anchored step links its
        // own Called-From room.
        string rooms = monsterLoc ? MonsterRoomLinks(monster, monsterRooms) : RoomLinks(s.Location);
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
            segments.Add("(turn in " + string.Join(", ",
                s.TurnInItems.Select(id => ItemName(gameData, id) + SourceSuffix(itemSources, monsterRooms, id))) + ")");

        // A required item the step also turns in is already named by the turn-in note
        // (a checkitem + takeitem of the same id), so it isn't repeated as "required".
        var required = s.RequiredItems.Where(id => !s.TurnInItems.Contains(id)).ToList();
        if (required.Count > 0)
            segments.Add("(" + string.Join(", ",
                required.Select(id => ItemName(gameData, id) + SourceSuffix(itemSources, monsterRooms, id))) + " required)");

        return segments.Count > 0 ? string.Join(" ", segments) : null;
    }

    // Where the player acquires an item the step needs, appended as ", from <source>"
    // so a turn-in / prerequisite step points at the chest, NPC, or room that yields
    // the item — the reverse acquisition index the item-detail pane uses. Empty when
    // no index is threaded in or nothing in the set yields the item. A monster giver
    // trails its placement room link (resolved through the same map the kill steps
    // use); a room-CMD reward trails the room; a chest names the container.
    private static string SourceSuffix(ItemSourceIndex? sources,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms, int itemId)
    {
        if (sources is null) return string.Empty;

        foreach (ItemGiver g in sources.GiversOf(itemId))
        {
            if (g.Kind == ItemGiverKind.Room && g.Map > 0 && g.Room > 0)
                return string.Create(CultureInfo.InvariantCulture, $", from ({g.Map}/{g.Room})");
            if (g.Kind == ItemGiverKind.Monster)
            {
                string where = MonsterRoomLinks(g.Number, monsterRooms);
                return where.Length > 0 ? $", from {g.Name} {where}" : $", from {g.Name}";
            }
        }

        IReadOnlyList<ItemSource> chests = sources.ContainersOf(itemId);
        return chests.Count > 0 ? $", from {chests[0].ContainerName}" : string.Empty;
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

    // The step's monster anchor, if any: a "Monster #N" location. A command-less
    // monster step is a kill (narrated "kill <monster> (<drop>)"); a monster step
    // carrying an "ask <npc> ..." command is an NPC dialogue step re-anchored on that
    // NPC by QuestStepGraph so the guide can link its room.
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
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms = null,
        ItemSourceIndex? itemSources = null)
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
            // A pure flag-advance (no room, command, kill or item) carries nothing the
            // player can act on, so it's dropped from the draft rather than listed as an
            // opaque "Step N" — the seed guides list actions, not narrative ticks.
            if (StepOrNull(gameData, s, monsterRooms, itemSources) is not { } body) continue;
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"[] {body}"));
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
