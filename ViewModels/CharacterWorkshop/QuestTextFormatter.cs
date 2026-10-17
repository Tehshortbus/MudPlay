using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Game.Quests;
using MudPlay.Services;

namespace MudPlay.ViewModels.CharacterWorkshop;

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

    // Room links named on one kill line for a monster that spawns in many rooms.
    private const int MaxDropperRooms = 3;

    // One followable step drafted in the hand-written guide's own shape:
    //   (map/room) (items required) `command` (get items)
    // The Called-From location's rooms become clickable (map/room) links (all of
    // them, for a multi-room list); a player command is backtick-wrapped as the
    // literal to type; a command-less step whose script a monster's death runs is
    // narrated "kill <monster> (<drop>)" and a bare item grant "obtain <item>",
    // matching how the seed guides read. Items the step needs or turns in come first,
    // "(… required)", the way the seed guides write them.
    //
    // The step's followable body — room links, command, kill/obtain narration and
    // item notes joined into one line — or null when the crawl captured no action
    // the player can take. Many quest steps are pure flag-advances the crawl can't
    // render: an alignment ladder's automatic value ticks, a story textblock the
    // player never directly triggers (Called-From another textblock/spell, no room,
    // no command, no item). Those carry nothing to do, so the auto-draft omits them
    // rather than listing an opaque "Step 31" the player can't act on.
    //
    // sameStep is every drafted step that lands on the same give-step: when several
    // monsters' deaths run it (two records of one boss), the kill names each and
    // links every room one of them stands in, so an unplaced record listed first
    // doesn't leave the step with nowhere to go.
    public static string? StepOrNull(GameDataCache gameData, QuestStep s,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms = null,
        IEnumerable<QuestStep>? sameStep = null)
    {
        var segments = new List<string>();

        string granted = string.Join(", ", s.GrantedItems.Select(id => ItemName(gameData, id)));

        // A gather step for a dropped item: the rooms its droppers stand in, any room
        // command that brings the dropper, then the kill.
        if (s.Droppers is { Count: > 0 } droppers)
        {
            string where = string.Join(" ", droppers
                .SelectMany(m => monsterRooms is not null && monsterRooms.TryGetValue(m, out IReadOnlyList<RoomKey>? keys)
                    ? keys : Array.Empty<RoomKey>())
                .Distinct()
                .Take(MaxDropperRooms)
                .Select(k => string.Create(CultureInfo.InvariantCulture, $"({k.Map}/{k.Room})")));
            if (where.Length == 0) where = RoomLinks(s.Location);
            if (where.Length > 0) segments.Add(where);
            if (!string.IsNullOrWhiteSpace(s.Command)) segments.Add($"`{s.Command.Trim()}`");
            string names = string.Join(" or ", droppers.Select(m => MonsterName(gameData, m)).Distinct());
            segments.Add(granted.Length > 0 ? $"kill {names} ({granted})" : $"kill {names}");
            return string.Join(" ", segments);
        }

        int monster = 0;
        bool monsterLoc = TryMonsterRef(s.Location, out monster);
        bool hasCommand = !string.IsNullOrWhiteSpace(s.Command);
        List<int> killTargets = hasCommand ? new List<int>() : QuestDeathSpells.KilledAt(gameData, s.Location);
        if (killTargets.Count > 0 && sameStep is not null)
            foreach (QuestStep other in sameStep)
                if (string.IsNullOrWhiteSpace(other.Command))
                    foreach (int id in QuestDeathSpells.KilledAt(gameData, other.Location))
                        if (!killTargets.Contains(id)) killTargets.Add(id);

        // Room link(s): a kill step links where its target stands, an ask step the NPC
        // it asks, and a room-anchored step its own Called-From room. A step that hangs
        // off an NPC with nothing to ask (a keyword the NPC shows by itself) has no room
        // to send the player to.
        string rooms = killTargets.Count > 0
                ? string.Join(" ", killTargets
                    .SelectMany(m => monsterRooms is not null && monsterRooms.TryGetValue(m, out IReadOnlyList<RoomKey>? keys)
                        ? keys : Array.Empty<RoomKey>())
                    .Distinct()
                    .Select(k => string.Create(CultureInfo.InvariantCulture, $"({k.Map}/{k.Room})")))
            : monsterLoc ? (hasCommand ? MonsterRoomLinks(monster, monsterRooms) : string.Empty)
            : RoomLinks(s.Location);
        if (rooms.Length > 0) segments.Add(rooms);

        // What the step needs on hand, taken or only checked, goes ahead of the action as
        // the guides write it: "(6/1357) (Gleaming Shard required) `ask martok forge`".
        // A step with nothing else to do (no command, kill or give) is the hand-in itself,
        // so what it takes is its action: "turn in Ice Tear, Eternal Fire".
        bool handInOnly = !hasCommand && killTargets.Count == 0 && granted.Length == 0 && s.TurnInItems.Count > 0;
        List<int> needs = (handInOnly ? s.RequiredItems.Except(s.TurnInItems) : s.TurnInItems.Concat(s.RequiredItems))
            .Distinct().ToList();
        if (needs.Count > 0)
            segments.Add("(" + string.Join(", ", needs.Select(id => ItemName(gameData, id))) + " required)");

        if (handInOnly)
        {
            segments.Add("turn in " + string.Join(", ", s.TurnInItems.Select(id => ItemName(gameData, id))));
        }
        else if (hasCommand)
        {
            segments.Add($"`{s.Command!.Trim()}`");
            if (granted.Length > 0) segments.Add($"(get {granted})");
        }
        else if (killTargets.Count > 0)
        {
            string name = string.Join(" or ", killTargets.Select(m => MonsterName(gameData, m)).Distinct());
            segments.Add(granted.Length > 0 ? $"kill {name} ({granted})" : $"kill {name}");
        }
        else if (granted.Length > 0)
        {
            segments.Add($"obtain {granted}");
        }

        if (!string.IsNullOrWhiteSpace(s.Note)) segments.Add($"({s.Note})");

        return segments.Count > 0 ? string.Join(" ", segments) : null;
    }

    // The Called-From location's room coordinates as space-joined (map/room) link
    // tokens — every room in a multi-room list, so each renders as its own walk-to
    // link. Empty when the location names no room (a Monster / Spell / Textblock ref).
    private static string RoomLinks(string? location) =>
        string.IsNullOrWhiteSpace(location)
            ? string.Empty
            : string.Join(" ", RoomRef().Matches(location)
                .Select(m => string.Create(CultureInfo.InvariantCulture, $"({m.Groups[1].Value}/{m.Groups[2].Value})")));

    // A kill or ask step's room link(s): every room the quest places the monster in,
    // as space-joined (map/room) tokens, drawn from the pre-built placement map so no
    // per-step room scan happens. Empty when the monster has no resolved placement —
    // the kill step then renders room-less rather than offering a dead link.
    private static string MonsterRoomLinks(int monster,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms) =>
        monsterRooms is not null && monsterRooms.TryGetValue(monster, out IReadOnlyList<RoomKey>? keys)
            ? string.Join(" ", keys.Select(k =>
                string.Create(CultureInfo.InvariantCulture, $"({k.Map}/{k.Room})")))
            : string.Empty;

    // The step's monster anchor, if any: a "Monster #N" location. A monster step
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
    // only the give-steps of the band's tier are emitted (DraftRange); a single-part
    // quest (range 0/0) emits every step. Empty when the flag drafts no steps.
    //
    // itemSources / itemDrops let the draft add a line for fetching an item a step needs
    // that no earlier step hands over (QuestItemGathering); without them those items are
    // only named on the step.
    public static IReadOnlyList<string> StepLines(GameDataCache gameData, CrawledQuest q,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms = null,
        ItemSourceIndex? itemSources = null, MonsterDropIndex? itemDrops = null)
        => StepEntries(gameData, q, monsterRooms, itemSources, itemDrops).Select(e => e.Line).ToList();

    // The note a later tier of a multi-part quest opens with, as the guides head one
    // ("-MUST COMPLETE MAGEBANE PORTION FIRST-"): its steps check the flag at the values
    // the tier before it leaves. Null for a single-part quest or a first tier. titleOf
    // names a tier the way the journal does. A plain line, not a checkbox, so the draft's
    // checkbox numbering is untouched.
    public static string? PriorTierLine(GameDataCache gameData, CrawledQuest q, Func<CrawledQuest, string> titleOf)
    {
        ArgumentNullException.ThrowIfNull(titleOf);
        if (q.BandOrdinal <= 1) return null;
        CrawledQuest? prior = QuestCrawler.Crawl(gameData, null)
            .FirstOrDefault(c => c.Flag == q.Flag && c.BandOrdinal == q.BandOrdinal - 1);
        return prior is null ? null : $"-Complete {titleOf(prior)} first-";
    }

    // StepLines with each line's give-step Order alongside — the flag value that step
    // sets, so a live flag read can say which checklist lines it proves done. A gather
    // line carries the Order of the step it fetches for: that step done, it was too.
    public static IReadOnlyList<(int Order, string Line)> StepEntries(GameDataCache gameData, CrawledQuest q,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms = null,
        ItemSourceIndex? itemSources = null, MonsterDropIndex? itemDrops = null)
    {
        var lines = new List<(int Order, string Line)>();
        foreach ((_, int order, string? body, _, _) in DraftSteps(gameData, q, monsterRooms, itemSources, itemDrops))
        {
            // A pure flag-advance (no room, command, kill or item) carries nothing the
            // player can act on, so it's dropped from the draft rather than listed as an
            // opaque "Step N" — the seed guides list actions, not narrative ticks.
            if (body is null) continue;
            lines.Add((order, string.Create(CultureInfo.InvariantCulture, $"[] {body}")));
        }
        return lines;
    }

    // The give-steps a tier's draft lists, inclusive; null for a single-part quest. A
    // give-step ladder's tier gate (StepRangeStart of the next tier) is the reward
    // hand-in of the tier before it — the Good ladder's `ask annora return` at 6 — so the
    // draft ends each tier with it, as the hand-written guides do (user, 2026-10-17), and
    // the next tier opens after it. StepRangeStart / StepRangeEnd themselves are left
    // alone: the tier's complete value and the flag sync read them. A value ladder keeps
    // its own span.
    private static (int Start, int End)? DraftRange(CrawledQuest q)
    {
        if (q.StepRangeEnd <= 0) return null;
        if (q.ProgressByValue || q.BandOrdinal <= 0) return (q.StepRangeStart, q.StepRangeEnd);
        int start = q.BandOrdinal == 1 ? q.StepRangeStart : q.StepRangeStart + 1;
        int end = q.StepRangeEnd == int.MaxValue ? int.MaxValue : q.StepRangeEnd + 1;
        return (start, end);
    }

    // Which of a flag's tiers a give-step is drafted in: the tier ordinal whose
    // DraftRange holds it, 0 for a single-part quest, -1 past every tier.
    private static Func<int, int> TierOf(GameDataCache gameData, int flag)
    {
        List<(int Ordinal, (int Start, int End) Range)> tiers = QuestCrawler.Crawl(gameData, null)
            .Where(c => c.Flag == flag && DraftRange(c) is not null)
            .Select(c => (c.BandOrdinal, DraftRange(c)!.Value))
            .ToList();
        if (tiers.Count == 0) return _ => 0;
        return order =>
        {
            foreach ((int ordinal, (int start, int end)) in tiers)
                if (order >= start && order <= end) return ordinal;
            return -1;
        };
    }

    // Every step the draft considers for a quest's flag, in checklist order: its place in
    // the flag's whole draft, its body (null when it has nothing followable), whether
    // the draft had a line for it before kill steps were read off the death-spell chain,
    // and whether it is a gather line (the draft had none of those before them either).
    // range filters to one tier's span; null takes every step.
    private static IEnumerable<(int Position, int Order, string? Body, bool HadLineBefore, bool Gathers)> DraftSteps(
        GameDataCache gameData, int flag, bool byAbilityValue, (int Start, int End)? range,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms,
        ItemSourceIndex? itemSources, MonsterDropIndex? itemDrops)
    {
        IReadOnlyList<QuestStep> steps = QuestStepGraph.Build(gameData, flag, byAbilityValue);
        IReadOnlyList<QuestStep> drafted = QuestItemGathering.Draft(gameData, steps, byAbilityValue, itemSources, itemDrops,
            TierOf(gameData, flag));
        for (int position = 0; position < drafted.Count; position++)
        {
            QuestStep s = drafted[position];
            if (range is { } r && (s.Order < r.Start || s.Order > r.End)) continue;
            IEnumerable<QuestStep>? sameStep = byAbilityValue || s.Gathers
                ? null : steps.Where(o => !o.Gathers && o.Order == s.Order);
            yield return (position, s.Order, StepOrNull(gameData, s, monsterRooms, sameStep),
                !s.Gathers && HadLineBeforeKillSteps(s), s.Gathers);
        }
    }

    private static IEnumerable<(int Position, int Order, string? Body, bool HadLineBefore, bool Gathers)> DraftSteps(
        GameDataCache gameData, CrawledQuest q, IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms,
        ItemSourceIndex? itemSources, MonsterDropIndex? itemDrops)
        => DraftSteps(gameData, q.Flag, q.ProgressByValue, DraftRange(q), monsterRooms, itemSources, itemDrops);

    // Whether the draft listed this step when a kill was only told from a "Monster #N"
    // location: anything with a command, a room or monster to link, or an item changing
    // hands. A step off a death spell with none of those was left out then.
    private static bool HadLineBeforeKillSteps(QuestStep s) =>
        !string.IsNullOrWhiteSpace(s.Command)
        || TryMonsterRef(s.Location, out _)
        || RoomLinks(s.Location).Length > 0
        || s.GrantedItems.Count > 0 || s.TurnInItems.Count > 0 || s.RequiredItems.Count > 0;

    // For step ticks saved against an older draft of one quest flag: where each of that
    // draft's checkboxes, (tier, number), sits in today's, keyed by the old (tier,
    // number). A step whose line is gone is absent. The older drafts are the one from
    // before the kill lines (sinceKillSteps) and the one before the gather lines, both of
    // which ran every tier over StepRangeStart..StepRangeEnd; today's adds lines and ends
    // each tier with its reward hand-in, which moves that line onto the tier before
    // (DraftRange). The gather lines count in today's numbering whatever indexes were at
    // hand, so the caller passes the ones the Quest tab drafts with.
    public static IReadOnlyDictionary<(int Step, int Index), (int Step, int Index)> CheckboxMoves(
        GameDataCache gameData, int flag, bool sinceKillSteps,
        ItemSourceIndex? itemSources = null, MonsterDropIndex? itemDrops = null)
    {
        List<CrawledQuest> tiers = QuestCrawler.Crawl(gameData, null).Where(c => c.Flag == flag).ToList();
        var now = new Dictionary<int, (int Step, int Index)>();
        foreach (CrawledQuest tier in tiers)
        {
            int index = 0;
            foreach (var e in DraftSteps(gameData, tier, null, itemSources, itemDrops))
                if (e.Body is not null) now[e.Position] = (tier.Step, index++);
        }

        var moves = new Dictionary<(int Step, int Index), (int Step, int Index)>();
        foreach (CrawledQuest tier in tiers)
        {
            (int Start, int End)? oldRange = tier.StepRangeEnd > 0 ? (tier.StepRangeStart, tier.StepRangeEnd) : null;
            int index = 0;
            foreach (var e in DraftSteps(gameData, tier.Flag, tier.ProgressByValue, oldRange, null, itemSources, itemDrops))
            {
                bool wasLine = sinceKillSteps ? e.HadLineBefore : !e.Gathers && e.Body is not null;
                if (!wasLine) continue;
                if (now.TryGetValue(e.Position, out (int Step, int Index) target)) moves[(tier.Step, index)] = target;
                index++;
            }
        }
        return moves;
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

    // Split a step label into render segments, isolating the two clickable token
    // kinds so the view can wrap each in a link: a (map/room) coordinate (e.g.
    // (5/297)) carries the parsed RoomKey for a walk-to link; a quoted command
    // carries the unquoted command text for a send-to-game link. Backticks
    // (`ask jorah transport`) are the guides' and the draft's own mark; a
    // single-quoted 'ask jorah transport' is the older one, still in many saved
    // guides; and a write-up pasted from elsewhere quotes with "…" or “…”, which
    // count only for two words or more that don't end a sentence, so a quoted word
    // or a quoted reply stays prose. Surrounding prose stays as plain segments (both
    // null). A coordinate whose numbers don't fit a positive int, or an empty quote,
    // is left folded into the prose. Returns a single plain segment when the label
    // holds no token, and an empty list for empty input.
    public static IReadOnlyList<(string Text, RoomKey? Room, string? Command)> SplitStepLinks(string text)
    {
        var segments = new List<(string Text, RoomKey? Room, string? Command)>();
        if (string.IsNullOrEmpty(text)) return segments;

        int pos = 0;
        foreach (Match m in StepLink().Matches(text))
        {
            if (m.Groups["map"].Success)
            {
                // Non-positive or over-range coordinate: not a real room — leave the
                // token in the prose run rather than offering a dead link.
                if (!int.TryParse(m.Groups["map"].Value, out int map)
                    || !int.TryParse(m.Groups["room"].Value, out int room)
                    || map <= 0 || room <= 0)
                    continue;

                if (m.Index > pos) segments.Add((text[pos..m.Index], null, null));
                segments.Add((m.Value, new RoomKey(map, room), null));
                pos = m.Index + m.Length;
            }
            else // a quoted command
            {
                string command = m.Groups["cmd"].Value.Trim();
                if (command.Length == 0) continue;   // empty quote — leave in prose

                if (m.Index > pos) segments.Add((text[pos..m.Index], null, null));
                segments.Add((m.Value, null, command));
                pos = m.Index + m.Length;
            }
        }
        if (pos < text.Length) segments.Add((text[pos..], null, null));
        return segments;
    }

    // A clickable quest-step token: a (map/room) coordinate (walk link) or a quoted
    // command (send-to-game link). A backtick run is taken whole, apostrophes and all.
    // The single-quote arm requires the quotes to sit on non-letter boundaries so a
    // prose apostrophe (don't, they're, Jorah's) never opens a bogus quoted run, and
    // an internal apostrophe ends the token. The double-quote arms want a space inside
    // and no trailing . ! ? or comma (see SplitStepLinks).
    [GeneratedRegex(@"\((?<map>\d+)/(?<room>\d+)\)"
        + @"|`(?<cmd>[^`]+)`"
        + @"|(?<![A-Za-z])'(?<cmd>[^']+?)'(?![A-Za-z])"
        + "|\"(?<cmd>[^\"\\s][^\"]*\\s[^\"]*[^\".!?,\\s])\""
        + "|\u201C(?<cmd>[^\u201D\\s][^\u201D]*\\s[^\u201D]*[^\u201D.!?,\\s])\u201D")]
    private static partial Regex StepLink();

    // "Room 9/1259" inside a Called-From string — a location's room reference.
    [GeneratedRegex(@"Room\s+(\d+)/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RoomRef();

    // "Monster #39" inside a Called-From string — a monster-sourced chain.
    [GeneratedRegex(@"Monster\s+#(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MonsterRef();
}
