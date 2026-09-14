using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MudPlay.Game.Calculators;
using MudPlay.Game.Light;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Builds the plain-text hover tooltip for a room on the Navigation map. Lair
// Exp/HP/Dmg-per-clear is intentionally omitted — those numbers need
// character-side calculations we don't track yet.
//
// Field order (blank-line separated where indicated):
//   1. Name (Map/Room)
//   2. blank
//   3. Room contents — monster groups (Placed / Assigned / Lair), then the lair
//      "Max Regen: N @ (Delay-1)m 30s" line directly beneath the Lair line, then
//      "Floor items: …" (the room's Placed / roomitem items).
//   4. blank
//   5. Shop: …
//   6. Room Spell: …
//   7. blank
//   8. Obvious exits: per-direction list with destination room name +
//      (map/room) + Door / Trap / gated annotation.
//   9. blank
//   10. Room Light: ±N + light description ("pitch black" / "very dark" /
//       "barely visible" / "dimly lit") beneath it.
//
// Lair string format expected (per the MDB): "(Max N): id,id,...,[group-index]".
// Older NMR < 1.83 imports may omit the trailing bracket; the parser tolerates
// both.
public static class RoomTooltipBuilder
{
    public static string Build(Room room, RoomGraphManager graph, GameDataCache? data,
        TBInfoStore? tbinfo = null, MonsterSpawnIndex? spawnIndex = null,
        Game.Spells.KnownSpellCatalog? spellCatalog = null, int charIllu = 0,
        RoomFloorItemIndex? floorItems = null)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(graph);

        StringBuilder sb = new();

        // 1. Name (Map/Room)
        sb.Append(room.DisplayName).Append(" (").Append(room.Key).Append(')');

        // 2. Room contents — the monster groups (Placed / Assigned / Lair + lair
        // regen) followed by any floor items, set off from the name by a blank
        // line so the header stands alone.
        var contents = new List<string>();
        string alsoHere = BuildAlsoHere(room, data, spawnIndex);
        if (alsoHere.Length > 0) contents.Add(alsoHere);
        string floorItemsLine = BuildFloorItems(room, data, floorItems);
        if (floorItemsLine.Length > 0) contents.Add(floorItemsLine);
        if (contents.Count > 0)
            sb.Append('\n').Append('\n').Append(string.Join("\n", contents));

        // 4-7. Shop / Room Spell (blank line separator above when any).
        string shopLine = room.Shop > 0
            ? "Shop: " + (LookupName(data, "Shops", room.Shop) ?? $"#{room.Shop}")
            : string.Empty;
        string spellLine = room.Spell > 0
            ? "Room Spell: " + (LookupName(data, "Spells", room.Spell) ?? $"#{room.Spell}")
            : string.Empty;
        if (shopLine.Length > 0 || spellLine.Length > 0)
        {
            sb.Append('\n');                          // blank line
            if (shopLine.Length > 0)  sb.Append('\n').Append(shopLine);
            if (spellLine.Length > 0) sb.Append('\n').Append(spellLine);
        }

        // 8. Exits — blank line above, per-direction with destination.
        string exitsBlock = BuildExitsBlock(room, graph, data, tbinfo);
        if (exitsBlock.Length > 0)
        {
            sb.Append('\n').Append('\n').Append(exitsBlock);
        }

        // 8b. Levers here — remote switches physically in THIS room that
        // control a gated exit elsewhere (e.g. a guardroom lever that lifts a
        // portcullis in the adjacent gate room). The gate's MultiAction data
        // attaches to the gate room's exit, so without this reverse lookup the
        // lever room's own tooltip would give no hint that acting here matters.
        string leversBlock = BuildLeversHereBlock(room, graph);
        if (leversBlock.Length > 0)
        {
            sb.Append('\n').Append('\n').Append(leversBlock);
        }

        // 9. Room commands — TBInfo CMD chains for the room (use chime,
        // ring chime, etc. — keyword-triggered teleports that bypass
        // normal exits). Grouped per-destination so identical-target
        // synonyms collapse to one line. Includes cast-delivered
        // teleports ("jump west" → bridge-jump spell) whose random range
        // surfaces every landing room.
        string commandsBlock = BuildRoomCommandsBlock(room, graph, data, tbinfo, spellCatalog);
        if (commandsBlock.Length > 0)
        {
            sb.Append('\n').Append('\n').Append(commandsBlock);
        }

        // 10. Room Light line + the descriptive phrase immediately
        // beneath it ("pitch black" / "very dark" / "barely visible"
        // / "dimly lit"). Description renders even when the numeric
        // line is suppressed (Light == 0 but still a dark room is
        // impossible by the encoding, so the description follows the
        // numeric line unconditionally).
        bool needBottomBlank = exitsBlock.Length > 0;
        if (room.Light != 0)
        {
            if (needBottomBlank) { sb.Append('\n'); needBottomBlank = false; }
            sb.Append('\n').Append("Room Light: ").Append(room.Light > 0 ? "+" : "")
              .Append(room.Light);
            string lightDesc = BuildLightDescription(room.Light, charIllu);
            if (lightDesc.Length > 0) sb.Append('\n').Append(lightDesc);
        }

        // Max Regen now renders beneath the Lair line (section 2) instead of here.

        return sb.ToString();
    }

    // ----- Also Here -------------------------------------------------

    // A monster present in a room, resolved to its record Number + display name.
    public readonly record struct RoomMonsterRef(int Id, string Name);

    // The three distinct ways a room hosts monsters, kept apart so the tooltip
    // and panels can label them (a monster can appear in more than one group —
    // e.g. a placed boss also assigned to roam — which is intentional):
    //   Placed   — the room's NPC fixture + "Room m/r" Summoned-By tokens (bosses).
    //   Assigned — non-lair "Group:" tokens (roam / rare-random spawns).
    //   Lair     — the room's own Lair tag members (consistent spawners); LairMax
    //              is that tag's "(Max N)" simultaneous cap.
    public readonly record struct RoomMonsters(
        IReadOnlyList<RoomMonsterRef> Placed,
        IReadOnlyList<RoomMonsterRef> Assigned,
        IReadOnlyList<RoomMonsterRef> Lair,
        int? LairMax);

    // Resolves the "Also Here" set — lair-tag members plus boss / script-spawn
    // monsters whose presence lives on the monster's "Summoned By" field — into
    // ordered, name-deduped refs. `max` carries the lair tag's Max-N (null when
    // the room has no lair). Shared by the map tooltip text and the interactive
    // room-detail popup so the two never drift.
    public static IReadOnlyList<RoomMonsterRef> ResolveAlsoHere(
        Room room, GameDataCache? data, MonsterSpawnIndex? spawnIndex, out int? max)
    {
        ArgumentNullException.ThrowIfNull(room);
        max = null;

        var refs = new List<RoomMonsterRef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(int id)
        {
            string? name = LookupName(data, "Monsters", id);
            if (string.IsNullOrEmpty(name) || !seen.Add(name)) return;
            refs.Add(new RoomMonsterRef(id, name));
        }

        if (!string.IsNullOrEmpty(room.RawLairTag))
        {
            ParseLairTag(room.RawLairTag, out max, out IReadOnlyList<int> monsterIds);
            foreach (int id in monsterIds) Add(id);
        }

        // Boss / script-spawn monsters don't count against the lair tag's
        // Max-N — separate respawn mechanic — so the count prefix stays driven
        // by the lair tag alone.
        if (spawnIndex is not null)
            foreach (int id in spawnIndex.MonsterIdsSummonedAt(room.Key))
                Add(id);

        return refs;
    }

    // Split the room's monsters into Placed / Assigned / Lair groups (see
    // RoomMonsters). Placed = the NPC fixture + "Room m/r" Summoned-By tokens;
    // Assigned = non-lair "Group:" tokens; Lair = the room's Lair tag. Each group
    // is name-deduped internally, but a monster may legitimately appear in more
    // than one group. Shared by the map tooltip and the interactive panels so the
    // labelling never drifts.
    public static RoomMonsters ResolveRoomMonsters(
        Room room, GameDataCache? data, MonsterSpawnIndex? spawnIndex)
    {
        ArgumentNullException.ThrowIfNull(room);

        List<RoomMonsterRef> Group(IEnumerable<int> ids)
        {
            var refs = new List<RoomMonsterRef>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (int id in ids)
            {
                string? name = LookupName(data, "Monsters", id);
                if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
                refs.Add(new RoomMonsterRef(id, name));
            }
            return refs;
        }

        // Placed — the room's NPC fixture (a boss / unique lives on Room.Npc)
        // plus any "Room m/r" Summoned-By tokens (usually the same monster).
        var placedIds = new List<int>();
        if (room.Npc > 0) placedIds.Add(room.Npc);
        if (spawnIndex is not null) placedIds.AddRange(spawnIndex.PlacedMonsterIdsAt(room.Key));

        IEnumerable<int> assignedIds = spawnIndex?.AssignedMonsterIdsAt(room.Key) ?? Array.Empty<int>();

        int? max = null;
        var lairIds = new List<int>();
        if (!string.IsNullOrEmpty(room.RawLairTag))
        {
            ParseLairTag(room.RawLairTag, out max, out IReadOnlyList<int> ids);
            lairIds.AddRange(ids);
        }

        return new RoomMonsters(Group(placedIds), Group(assignedIds), Group(lairIds), max);
    }

    private static string BuildAlsoHere(Room room, GameDataCache? data, MonsterSpawnIndex? spawnIndex)
    {
        RoomMonsters rm = ResolveRoomMonsters(room, data, spawnIndex);

        StringBuilder sb = new();
        // Append each monster's Monsters-table record number so the tooltip
        // doubles as a quick lookup key — "Dark Goblin Archer(#48)".
        void Line(string label, IReadOnlyList<RoomMonsterRef> refs)
        {
            if (refs.Count == 0) return;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(label).Append(": ")
              .Append(string.Join(", ", refs.Select(r => $"{r.Name}(#{r.Id})")));
        }
        Line("Placed", rm.Placed);
        Line("Assigned", rm.Assigned);
        Line("Lair", rm.Lair);

        // Lair regen sits directly beneath the Lair line (its simultaneous cap +
        // per-mob respawn time), where it annotates the mobs it describes, rather
        // than at the bottom of the tooltip.
        string regen = FormatLairRegen(rm.LairMax, room.Delay);
        if (regen.Length > 0)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(regen);
        }
        return sb.ToString();
    }

    // The "Max Regen: N @ (Delay-1)m 30s" line for a room's lair (N = the lair
    // tag's simultaneous cap, the time = its respawn cadence), or empty when the
    // room has no lair. Shared by the map tooltip and the Room Info panel so the
    // two never drift.
    public static string FormatLairRegen(int? lairMax, int delay)
    {
        if (lairMax is not { } max) return string.Empty;
        string line = "Max Regen: " + max.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string time = BuildRegenTime(delay);
        return time.Length > 0 ? line + " @ " + time : line;
    }

    // Floor items the room drops on the ground — its static `Placed` list plus any
    // `roomitem` scatter, from RoomFloorItemIndex. One "Floor items: ..." line,
    // name-deduped, each with its record number. Empty when nothing is on the
    // floor (or no index was supplied). Fixes the case where an item's own record
    // named its room (e.g. the bogwood box → 14/10415) but the room tooltip didn't
    // list the item.
    private static string BuildFloorItems(Room room, GameDataCache? data, RoomFloorItemIndex? floorItems)
    {
        if (floorItems is null) return string.Empty;
        IReadOnlyList<int> ids = floorItems.FloorItemsOf(room.Key);
        if (ids.Count == 0) return string.Empty;

        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (int id in ids)
        {
            string name = LookupName(data, "Items", id) ?? $"#{id}";
            string label = $"{name}(#{id})";
            if (seen.Add(label)) parts.Add(label);
        }
        return "Floor items: " + string.Join(", ", parts);
    }

    // ----- Light description ---------------------------------------

    private static string BuildLightDescription(int light, int charIllu)
        // Visibility is a function of V = charIllu + roomLight: a lit lantern or
        // worn +illu gear lifts a dark room out of the "can't see" bands, so the
        // phrase reflects what the player actually sees, not the room's raw
        // offset. Shares LightModel's band table so the tooltip and the route
        // predictor never drift.
        => LightModel.Describe(LightModel.Classify(charIllu, roomLight: light));

    // Room-illumination summary for the Navigation ROOM INFO panel — the room's
    // OWN light: "Room Illu: <signed value>", optionally with the room-alone
    // visibility phrase (" - <phrase>"). The phrase is dropped when the player
    // carries light, so it lands on the Your Illu line instead. A fully-lit room
    // (Light 0) still shows a line — "Room Illu: 0 - You can see." The panel trims
    // the line to its width.
    public static string BuildRoomLightSummary(Room room, bool includePhrase = true)
    {
        ArgumentNullException.ThrowIfNull(room);
        string offset = (room.Light > 0 ? "+" : "") + room.Light;
        return includePhrase
            ? $"Room Illu: {offset} - {PanelVisibilityPhrase(charIllu: 0, room.Light)}"
            : $"Room Illu: {offset}";
    }

    // Player-adjusted illumination for the ROOM INFO panel — the room's light plus
    // the player's carried illumination (playerIllu: worn +illu gear + readied
    // light + any configured light-spell illu) folded into one effective value,
    // with the visibility phrase: "Your Illu: <signed room.Light + playerIllu> -
    // <phrase>". Shown only when the player actually carries light (the caller
    // gates on playerIllu > 0).
    public static string BuildPlayerLightSummary(Room room, int playerIllu)
    {
        ArgumentNullException.ThrowIfNull(room);
        int v = room.Light + playerIllu;
        string value = (v > 0 ? "+" : "") + v;
        return $"Your Illu: {value} - {PanelVisibilityPhrase(playerIllu, room.Light)}";
    }

    // The ROOM INFO panel's visibility phrase for V = charIllu + roomLight. Keeps
    // every game darkness line (pitch black / very dark / barely visible / dimly
    // lit); only fully-lit (LightModel.Describe returns empty) reads "You can
    // see." — LightModel.Describe itself stays game-accurate for the map tooltip.
    private static string PanelVisibilityPhrase(int charIllu, int roomLight)
    {
        string desc = BuildLightDescription(light: roomLight, charIllu: charIllu);
        return desc.Length > 0 ? desc : "You can see.";
    }

    // Renders the non-interactive tail of the room-detail popup — shop, room
    // spell, room commands (teleports), room light + descriptive phrase, and
    // max regen. Name / Also-Here / exits are rendered as clickable controls in
    // the popup, so they're deliberately excluded here. Reuses the same private
    // helpers the map tooltip's Build() uses, so the two never drift.
    //
    // includeShop drops the plain "Shop: <name>" line when the popup renders the
    // shop richly instead — a merchant with stock (its own table) or a trainer
    // (its level band) owns that section, so the redundant line would double up.
    // Banks with no stock keep the plain line (includeShop stays true for them).
    public static string BuildDetailExtras(Room room, RoomGraphManager graph,
        GameDataCache? data = null, TBInfoStore? tbinfo = null,
        Game.Spells.KnownSpellCatalog? spellCatalog = null, int charIllu = 0,
        bool includeShop = true)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(graph);

        var parts = new List<string>();

        if (includeShop && room.Shop > 0)
            parts.Add("Shop: " + (LookupName(data, "Shops", room.Shop) ?? $"#{room.Shop}"));
        if (room.Spell > 0)
            parts.Add("Room Spell: " + (LookupName(data, "Spells", room.Spell) ?? $"#{room.Spell}"));

        string leversBlock = BuildLeversHereBlock(room, graph);
        if (leversBlock.Length > 0) parts.Add(leversBlock);

        string commandsBlock = BuildRoomCommandsBlock(room, graph, data, tbinfo, spellCatalog);
        if (commandsBlock.Length > 0) parts.Add(commandsBlock);

        if (room.Light != 0)
        {
            StringBuilder light = new();
            light.Append("Room Light: ").Append(room.Light > 0 ? "+" : "").Append(room.Light);
            string lightDesc = BuildLightDescription(room.Light, charIllu);
            if (lightDesc.Length > 0) light.Append('\n').Append(lightDesc);
            parts.Add(light.ToString());
        }

        if (TryParseLairMax(room.RawLairTag, out int maxRegen))
        {
            StringBuilder regen = new();
            regen.Append("Max Regen: ").Append(maxRegen);
            string regenTime = BuildRegenTime(room.Delay);
            if (regenTime.Length > 0) regen.Append(" @ ").Append(regenTime);
            parts.Add(regen.ToString());
        }

        return string.Join("\n\n", parts);
    }

    // ----- Exits block ---------------------------------------------

    // Room exits in the canonical compass order (N, NE, … U, D), skipping
    // directions the room doesn't have. Lets the room-detail popup render one
    // clickable row per exit using the same ordering as the map tooltip.
    public static IEnumerable<(Direction Dir, RoomExit Exit)> OrderedExits(Room room)
    {
        ArgumentNullException.ThrowIfNull(room);
        foreach (Direction dir in s_exitOrder)
            if (room.Exits.TryGetValue(dir, out RoomExit exit))
                yield return (dir, exit);
    }

    private static readonly Direction[] s_exitOrder =
    {
        Direction.N, Direction.NE, Direction.E, Direction.SE,
        Direction.S, Direction.SW, Direction.W, Direction.NW,
        Direction.U, Direction.D,
    };

    private static string BuildExitsBlock(Room room, RoomGraphManager graph, GameDataCache? data, TBInfoStore? tbinfo)
    {
        if (room.Exits.Count == 0) return string.Empty;

        StringBuilder sb = new();
        sb.Append("Obvious exits:");
        foreach (Direction dir in s_exitOrder)
        {
            if (!room.Exits.TryGetValue(dir, out RoomExit exit)) continue;

            Room? dest = graph.GetRoom(exit.Target);
            string destName = dest is not null ? dest.DisplayName : exit.Target.ToString();

            sb.Append('\n').Append("  ").Append(DirectionLabel(dir)).Append(" → ");
            sb.Append(destName).Append(' ').Append('(').Append(exit.Target).Append(')');

            string hintRender = FormatExitHint(exit, data);
            if (hintRender.Length > 0) sb.Append(" (").Append(hintRender).Append(')');

            // Multi-line per-step breakdown for action-required exits.
            // The inline hint above carries the summary ("Needs N
            // actions"); this block names the trigger room + commands
            // for each step so a glance at the tooltip is enough to
            // know where to go (e.g. "go to room 9/870 and pull lever"
            // for map 9 room 1012's east exit on v1.11p).
            if (exit.Hint == RoomExitHint.MultiActionHidden)
            {
                if (exit.MultiAction is { Actions.Count: > 0 } maDetail)
                {
                    AppendMultiActionDetail(sb, room.Key, maDetail, graph, data);
                }
                else if (room.Cmd > 0 && tbinfo is not null)
                {
                    // No Action#N exit cells were attached, but the
                    // room runs a TBInfo CMD chain. v1.11p encodes
                    // many lever-style unlocks this way (e.g. map
                    // 9 / room 1012 CMD 1422 — "clear rubble" /
                    // "push mound" / etc., all firing the same
                    // remoteaction). Surface those keywords as a
                    // fallback so the tooltip still tells the user
                    // what to type.
                    AppendTbInfoActionFallback(sb, room.Cmd, tbinfo);
                }
            }
        }
        return sb.ToString();
    }

    // Per-step breakdown rendered beneath a MultiActionHidden exit: one indented
    // line per ExitAction with the trigger room (when the action lives in
    // another room) plus its alternative commands. Mirrors the format the walker
    // actually executes — the user sees the same routing the path expander would
    // do.
    private static void AppendMultiActionDetail(
        StringBuilder sb, RoomKey hostRoom, MultiActionExitData ma, RoomGraphManager graph,
        GameDataCache? data)
    {
        for (int i = 0; i < ma.Actions.Count; i++)
        {
            ExitAction step = ma.Actions[i];
            sb.Append('\n').Append("    ");
            // Step number — match the parser's #N for the user, so it
            // lines up with the raw MDB cell if they ever look.
            sb.Append(step.StepNumber).Append(". ");

            // Trigger location: same room if RemoteSourceRoom is null
            // (action runs from the exit's host room), or the named
            // remote room otherwise. The remote-room name comes from
            // the graph when available; fall back to the bare RoomKey
            // when the room sits outside the active set.
            if (step.RemoteSourceRoom is { } remote)
            {
                Room? at = graph.GetRoom(remote);
                string name = at is not null ? at.DisplayName : remote.ToString();
                sb.Append("at ").Append(name).Append(' ').Append('(').Append(remote).Append("): ");
            }
            else
            {
                sb.Append("here: ");
            }
            sb.Append(string.Join(" / ", step.Commands));

            // Held-item requirement ("… (Item: 815)") — surface the item the
            // step needs so the user knows the exit is gated on carrying it.
            if (step.RequiredItemId > 0)
            {
                string? itemName = LookupName(data, "Items", step.RequiredItemId);
                string label = itemName is { Length: > 0 } ? itemName : $"#{step.RequiredItemId}";
                sb.Append(" (needs ").Append(label).Append(')');
            }
        }
    }

    // TBInfo fallback for MultiActionHidden exits whose unlock lives in a CMD
    // chain rather than Action#N exit cells. Walks the chain via
    // TBInfoActionResolver and renders the gathered keywords as a single
    // indented "Try: kw1 / kw2 / …" line. The keywords all run in the room being
    // hovered (TBInfo CMDs are local to their owning room), so no "here:" /
    // "at X:" prefix is needed.
    private static void AppendTbInfoActionFallback(
        StringBuilder sb, int roomCmd, TBInfoStore tbinfo)
    {
        List<string> keywords = new();
        foreach (string kw in TBInfoActionResolver.EnumerateRemoteActionKeywords(tbinfo, roomCmd))
        {
            // Preserve order but dedup — the same keyword appearing
            // twice in a CMD chain (rare but possible) shouldn't
            // bloat the tooltip.
            if (!keywords.Contains(kw, StringComparer.OrdinalIgnoreCase))
                keywords.Add(kw);
        }
        if (keywords.Count == 0) return;

        sb.Append('\n').Append("    Try: ").Append(string.Join(" / ", keywords));
    }

    // Render the parenthetical exit qualifier, looking up the underlying record
    // name when a hint carries a structured id. Item/Ticket → Items table.
    // KeyLocked → Items table (the key is itself an Item record per MDB
    // convention). Falls back to the raw hint string for unclassified modifiers
    // so diagnostic info still shows.
    public static string FormatExitHint(RoomExit exit, GameDataCache? data)
    {
        switch (exit.Hint)
        {
            case RoomExitHint.Item when exit.KeyItemId > 0:
            case RoomExitHint.Ticket when exit.KeyItemId > 0:
            case RoomExitHint.KeyLocked when exit.KeyItemId > 0:
            {
                string label = exit.Hint switch
                {
                    RoomExitHint.Item   => "Item",
                    RoomExitHint.Ticket => "Ticket",
                    _                   => "Key",
                };
                string? itemName = LookupName(data, "Items", exit.KeyItemId);

                // A key id that names no item in a READABLE Items table is a
                // game-data typo, not a key to go find: 8/462's north gate records
                // "Key: 1" where item ids start at 9, and its real openers are the
                // 31 picklocks/strength it also carries. Naming a key there sends
                // the reader hunting an item that doesn't exist, so the clause is
                // dropped and the door reads as what it is.
                //
                // The table itself has to be readable before a missing row can be
                // called a typo — LookupName returns null just as readily for an
                // absent or malformed Items.json, and silently restyling every keyed
                // door in the set as a plain door would be a much worse lie. Also
                // kept when the door has no stat alternative: that one really is
                // impassable, and the raw id is the only clue why.
                if (exit.Hint == RoomExitHint.KeyLocked
                    && itemName is not { Length: > 0 }
                    && exit.StatRequirement > 0
                    && data?.GetRawTable("Items") is not null)
                    return $"Door: {FormatDoorSkill(exit)}";

                string baseText = itemName is { Length: > 0 }
                    ? $"{label}: {itemName}"
                    : $"{label}: #{exit.KeyItemId}";
                // A key-locked door that can also be picked / bashed carries the
                // skill alternative ("or 50 picklocks/strength") — surface it so
                // the user knows they needn't have the key.
                if (exit.Hint == RoomExitHint.KeyLocked
                    && FormatDoorRequirement(exit) is { Length: > 0 } alt)
                    baseText += $", or {alt}";
                return baseText;
            }

            // A plain (keyless) door: surface the pick / bash skill requirement so
            // the user sees what it takes to open it. A zero requirement (a bare
            // "(Door)" or "(Door [any picklocks/strength])") reads "any" — anyone
            // can bash / pick it — rather than showing nothing.
            case RoomExitHint.Door:
                return $"Door: {FormatDoorSkill(exit)}";

            case RoomExitHint.Toll when exit.TollGold > 0:
                return $"Toll: {exit.TollGold} gold";

            case RoomExitHint.Trap when exit.TrapDamage > 0:
                return $"Trap: {exit.TrapDamage} dmg";

            case RoomExitHint.Text when exit.TextCommands is { Count: > 0 }:
                return "Text: " + string.Join(", ", exit.TextCommands);

            case RoomExitHint.MultiActionHidden when exit.MultiAction is { Actions.Count: > 0 } ma:
            {
                // "Needs N action(s) [specific order]: cmd1 / cmd1alt; cmd2 / cmd2alt"
                // — alternatives within one step are " / " joined; steps
                // are "; " joined. Concise enough for the tooltip while
                // still showing every parsed alternative.
                string countLabel = ma.RequiredActionCount == 1 ? "action" : "actions";
                string order      = ma.RequiresSpecificOrder ? " specific order" : "";
                string steps = string.Join("; ",
                    ma.Actions.Select(a => string.Join(" / ", a.Commands)));
                return $"Needs {ma.RequiredActionCount} {countLabel}{order}: {steps}";
            }

            case RoomExitHint.MultiActionHidden:
            {
                // MultiAction data didn't attach to this exit (no
                // Action#N exit cells — the unlock lives in a TBInfo
                // CMD chain instead, see TBInfoActionResolver). Still
                // synthesise the "Needs N actions" summary from the
                // raw modifier so the inline hint is informative
                // instead of just "(MultiActionHidden)". The per-step
                // breakdown beneath the exit line carries the actual
                // keyword candidates.
                (int count, bool specific) = MultiActionExitData.ParseModifier(exit.RawHint ?? string.Empty);
                string label = count == 1 ? "action" : "actions";
                string order = specific ? " specific order" : "";
                return $"Needs {count} {label}{order}";
            }

            case RoomExitHint.None:
                if (exit.HasLevelGate)
                    return RoomExit.FormatLevelGate(exit.MinLevel, exit.MaxLevel);
                if (exit.HasClassGate)
                {
                    // "(Class: 13 OK, 0 NO)" → "Druid only". Fall back to the
                    // raw class Number when the Classes table isn't loaded.
                    string? className = LookupName(data, "Classes", exit.ClassGate);
                    return className is { Length: > 0 }
                        ? $"{className} only"
                        : $"Class #{exit.ClassGate} only";
                }
                return string.IsNullOrEmpty(exit.RawHint) ? string.Empty : exit.RawHint!;

            default:
                return exit.Hint.ToString();
        }
    }

    // A door's pick / bash skill requirement, from the parsed StatRequirement +
    // CanBash flags. "50 picklocks/strength" when both verbs work (the MDB's own
    // "picklocks/strength" phrasing — one figure serves as both the picklock
    // skill and the bash strength), "50 picklocks" when the door is pick-only.
    // Empty when no figure was parsed (older exports omit the number).
    private static string FormatDoorRequirement(RoomExit exit)
    {
        if (exit.StatRequirement <= 0) return string.Empty;
        return exit.CanBash
            ? $"{exit.StatRequirement} picklocks/strength"
            : $"{exit.StatRequirement} picklocks";
    }

    // Like FormatDoorRequirement but never blank: a zero requirement renders "any"
    // (any bash / pick opens it) instead of an empty string. Used for a plain door,
    // where "any" is meaningful; the key-locked alternative keeps the blank-on-zero
    // FormatDoorRequirement (a key-only door has no stat alternative to append).
    private static string FormatDoorSkill(RoomExit exit)
    {
        string qty = exit.StatRequirement > 0
            ? exit.StatRequirement.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "any";
        return exit.CanBash ? $"{qty} picklocks/strength" : $"{qty} picklocks";
    }

    public static string DirectionLabel(Direction d) => d switch
    {
        Direction.N  => "north",
        Direction.S  => "south",
        Direction.E  => "east",
        Direction.W  => "west",
        Direction.NE => "northeast",
        Direction.NW => "northwest",
        Direction.SE => "southeast",
        Direction.SW => "southwest",
        Direction.U  => "up",
        Direction.D  => "down",
        _            => d.ToString(),
    };

    // ----- Levers here (remote switches this room controls) ---------

    // Lists any lever/switch physically in this room that governs an exit
    // elsewhere, naming the controlled room + direction and the verbs that work
    // it. One line per controlled exit, alternative verbs " / " joined.
    private static string BuildLeversHereBlock(Room room, RoomGraphManager graph)
    {
        IReadOnlyList<RoomGraphManager.RemoteLeverRef> levers =
            graph.LeversControlledFrom(room.Key);
        if (levers.Count == 0) return string.Empty;

        StringBuilder sb = new();
        sb.Append("Levers here:");
        foreach (RoomGraphManager.RemoteLeverRef lever in levers)
        {
            Room? controlled = graph.GetRoom(lever.ControlledRoom);
            string name = controlled is not null
                ? controlled.DisplayName
                : lever.ControlledRoom.ToString();
            sb.Append('\n').Append("  ")
              .Append(string.Join(" / ", lever.Commands))
              .Append(" → ").Append(name).Append(" (").Append(lever.ControlledRoom)
              .Append(") ").Append(DirectionLabel(lever.Direction)).Append(" exit");
        }
        return sb.ToString();
    }

    // ----- Room commands (TBInfo CMD chains) ------------------------

    private static string BuildRoomCommandsBlock(Room room, RoomGraphManager graph,
        GameDataCache? data, TBInfoStore? tbinfo, Game.Spells.KnownSpellCatalog? spellCatalog)
    {
        if (tbinfo is null || room.Cmd <= 0) return string.Empty;

        // Literal teleports (`teleport <room> <map>`): group destination →
        // list of keywords so multi-synonym CMDs ("use chime" / "ring
        // chime" both teleporting to 1/65) render as one line instead of
        // cluttering the tooltip.
        Dictionary<RoomKey, List<string>> byDest = new();
        Dictionary<RoomKey, int> minLevelByDest = new();
        foreach ((string keyword, RoomKey dest, int minLevel)
                 in TBInfoTeleportResolver.EnumerateTeleports(tbinfo, room.Cmd))
        {
            if (!byDest.TryGetValue(dest, out List<string>? words))
                byDest[dest] = words = new List<string>();
            if (!words.Contains(keyword)) words.Add(keyword);
            // A destination reachable by several keywords keeps the
            // highest level floor seen across them (conservative gate).
            if (minLevel > minLevelByDest.GetValueOrDefault(dest))
                minLevelByDest[dest] = minLevel;
        }

        // Cast-delivered teleports (`cast <spell>`): group by the full
        // destination set so two synonyms casting the same spell ("jump
        // west" / "jump east" → bridge jump) collapse to one entry.
        List<CastTeleportGroup> castGroups = ResolveCastGroups(room, tbinfo, spellCatalog);

        // Commands that demand something before they work — a `price` (gambling,
        // healer / summon buys, passage fares, the jail "bribe guard"), a
        // `minlevel` floor, or both. Keyed by keyword so a teleport / cast /
        // action line sharing the keyword can append what it demands, and any
        // command not otherwise surfaced still gets listed with its own line.
        List<TBInfoActionResolver.CommandRequirement> requirements =
            TBInfoActionResolver.EnumerateCommandRequirements(tbinfo, room.Cmd).ToList();
        Dictionary<string, TBInfoActionResolver.CommandRequirement> requirementByKeyword =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (TBInfoActionResolver.CommandRequirement pc in requirements)
            requirementByKeyword[pc.Keyword] = pc;
        HashSet<string> requirementShown = new(StringComparer.OrdinalIgnoreCase);

        // Room-action keywords (`remoteaction` CMD lines — "pull drawer",
        // "clear rubble", etc.) that change the world in place rather than
        // teleporting. These already surface beneath a MultiActionHidden exit
        // via AppendTbInfoActionFallback, so only list them here when no such
        // exit claimed them — otherwise a room with both would render the same
        // keyword twice. A room whose only special interaction is a standalone
        // room action (e.g. 1/381's "pull drawer", with just a normal door
        // exit) has no MultiActionHidden exit, so the fallback never fired and
        // the keyword would go unshown without this. Priced keywords are held
        // back — they render on their own line with the cost attached.
        List<string> actionKeywords = new();
        bool shownByExit = room.Exits.Values.Any(e =>
            e.Hint == RoomExitHint.MultiActionHidden
            && e.MultiAction is not { Actions.Count: > 0 });
        if (!shownByExit)
        {
            foreach (string kw in TBInfoActionResolver.EnumerateRemoteActionKeywords(tbinfo, room.Cmd))
                if (!requirementByKeyword.ContainsKey(kw)
                    && !actionKeywords.Contains(kw, StringComparer.OrdinalIgnoreCase))
                    actionKeywords.Add(kw);
        }

        // Item-yielding room actions — the Dwarven Mines "mine ore" / "mine vein"
        // gather commands (giveitem / random directives). These never unlock an
        // exit, so unlike the remoteaction keywords above they surface regardless
        // of the MultiActionHidden guard.
        foreach (string kw in TBInfoActionResolver.EnumerateRoomActionKeywords(tbinfo, room.Cmd))
            if (!requirementByKeyword.ContainsKey(kw)
                && !actionKeywords.Contains(kw, StringComparer.OrdinalIgnoreCase))
                actionKeywords.Add(kw);

        // Effect commands — the directives none of the resolvers above explain
        // (summon / learnspell / ability grant / room-item drop / plain turn-in).
        // Anything already rendered above is excluded: a command whose FIRST-time
        // line teleports and whose REPEAT line only awards an ability (7/142's
        // "touch gem") would otherwise appear both as a teleport and as an ability
        // grant, reading as two separate commands.
        var renderedKeywords = new List<string>();
        foreach (List<string> words in byDest.Values) renderedKeywords.AddRange(words);
        foreach (CastTeleportGroup g in castGroups) renderedKeywords.AddRange(g.Keywords);
        renderedKeywords.AddRange(actionKeywords);
        IReadOnlyList<RoomEffectRow> effectRows =
            ResolveRoomEffectRows(room, data, tbinfo, renderedKeywords);

        if (byDest.Count == 0 && castGroups.Count == 0 && actionKeywords.Count == 0
            && requirements.Count == 0 && effectRows.Count == 0)
            return string.Empty;

        // Append what a rendered group's keyword demands (marking it shown so it
        // isn't also listed standalone). Synonyms share the requirement.
        // levelShown says the caller has already printed the level floor beside
        // the destination — a teleport reads its level off the same directive, so
        // repeating it here would render "(Level 20+) — Level 20+".
        string CostSuffix(IReadOnlyList<string> keywords, bool levelShown)
        {
            TBInfoActionResolver.CommandRequirement? found = null;
            foreach (string kw in keywords)
                if (requirementByKeyword.TryGetValue(kw, out TBInfoActionResolver.CommandRequirement pc))
                {
                    found ??= pc;
                    requirementShown.Add(kw);
                }
            if (found is not { } f) return string.Empty;
            string text = FormatRequirement(f, includeLevel: !levelShown);
            return text.Length > 0 ? " — " + text : string.Empty;
        }

        StringBuilder sb = new();
        sb.Append("Room commands:");
        foreach (KeyValuePair<RoomKey, List<string>> entry in byDest)
        {
            sb.Append('\n').Append("  ")
              .Append(string.Join(" / ", entry.Value))
              .Append(" → ").Append(FormatDest(graph, entry.Key));
            int ml = minLevelByDest.GetValueOrDefault(entry.Key);
            if (ml > 0)
                sb.Append(" (").Append(RoomExit.FormatLevelGate(ml, 0)).Append(')');
            sb.Append(CostSuffix(entry.Value, levelShown: ml > 0));
        }
        foreach (CastTeleportGroup g in castGroups)
        {
            string castCost = CostSuffix(g.Keywords, levelShown: g.MinLevel > 0);
            sb.Append('\n').Append("  ")
              .Append(string.Join(" / ", g.Keywords)).Append(" → ");
            if (g.Destinations.Count == 1)
            {
                sb.Append(FormatDest(graph, g.Destinations[0]));
                if (g.MinLevel > 0)
                    sb.Append(" (").Append(RoomExit.FormatLevelGate(g.MinLevel, 0)).Append(')');
                sb.Append(castCost);
            }
            else
            {
                // A random multi-room landing is the walker's "tier 2
                // lost state" trigger — list every possibility so the map
                // can flag post-jump position uncertainty.
                sb.Append(g.Random
                    ? $"one of {g.Destinations.Count} rooms (random)"
                    : $"{g.Destinations.Count} rooms");
                if (g.MinLevel > 0)
                    sb.Append(" (").Append(RoomExit.FormatLevelGate(g.MinLevel, 0)).Append(')');
                sb.Append(castCost);
                sb.Append(':');
                foreach (RoomKey d in g.Destinations)
                    sb.Append('\n').Append("      ").Append(FormatDest(graph, d));
            }
        }
        if (actionKeywords.Count > 0)
            sb.Append('\n').Append("  ").Append(string.Join(" / ", actionKeywords))
              .Append(" (room action)");

        // An effect row that also charges (the healer's "summon healer" for gold)
        // carries its own cost, so mark those keywords shown — otherwise the same
        // command would render twice, once for what it does and once for the price.
        foreach (RoomEffectRow row in effectRows)
        {
            foreach (string kw in row.Keywords) requirementShown.Add(kw);
            sb.Append('\n').Append("  ").Append(string.Join(" / ", row.Keywords))
              .Append(" — ").Append(row.EffectText);
            if (row.CostText.Length > 0) sb.Append(" — ").Append(row.CostText);
        }

        // Paid commands not already surfaced above (a healer's buy list, a
        // summoner's services, the jail bribe) get one line each with the cost.
        foreach (TBInfoActionResolver.CommandRequirement pc in requirements)
        {
            if (requirementShown.Contains(pc.Keyword)) continue;
            requirementShown.Add(pc.Keyword);
            sb.Append('\n').Append("  ").Append(pc.Keyword)
              .Append(" — ").Append(FormatRequirement(pc));
        }
        return sb.ToString();
    }

    // One room command described by what it does: its synonyms grouped, the effect
    // phrased, and the charge folded in when the line carries a `price`. Shared by
    // the hover tooltip and the Navigation Room Info panel so both describe a
    // command identically. TargetId is the monster / spell / item the effect names
    // (0 for GrantAbility), kept alongside the text so a consumer can link the row
    // to that game-data record.
    public sealed record RoomEffectRow(
        IReadOnlyList<string> Keywords,
        TBInfoActionResolver.RoomEffectKind Kind,
        int TargetId,
        string EffectText,
        string CostText);

    // Every effect-explained command in a room's CMD chain, one row per command.
    // Synonyms producing the same effect collapse onto one row the way teleport
    // destinations do, so 8/461's "touch statue" and "move statue" read as a single
    // line. Pass alreadyShown to suppress commands the caller renders by another
    // means (the hover tooltip passes its teleport / cast / room-action keywords, so
    // 7/142's "touch gem" reads once as the teleport it is rather than twice).
    // Empty when the room has no CMD, no TBInfo store, or nothing but flavour text.
    public static IReadOnlyList<RoomEffectRow> ResolveRoomEffectRows(
        Room room, GameDataCache? data, TBInfoStore? tbinfo,
        IEnumerable<string>? alreadyShown = null)
    {
        ArgumentNullException.ThrowIfNull(room);
        if (tbinfo is null || room.Cmd <= 0) return Array.Empty<RoomEffectRow>();

        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (alreadyShown is not null) shown.UnionWith(alreadyShown);

        Dictionary<string, TBInfoActionResolver.CommandRequirement> requirementByKeyword =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (TBInfoActionResolver.CommandRequirement pc
                 in TBInfoActionResolver.EnumerateCommandRequirements(tbinfo, room.Cmd))
            requirementByKeyword[pc.Keyword] = pc;

        // One effect per KEYWORD, at its highest priority. The export writes a
        // separate line per outcome branch of the same command — 14/6275's "destroy
        // portal" summons the guardian on the attempt line and awards the quest
        // ability on the line for after you've beaten it — so one keyword can carry
        // several effects across lines. Collapsing to the best of them (the
        // declaration order of RoomEffectKind) is the same rule that picks between
        // two effects on a single line, and it stops a command being listed twice.
        Dictionary<string, (TBInfoActionResolver.RoomEffectKind Kind, int TargetId, string Cost)>
            bestByKeyword = new(StringComparer.OrdinalIgnoreCase);
        var keywordOrder = new List<string>();

        foreach (TBInfoActionResolver.RoomEffectCommand ec
                 in TBInfoActionResolver.EnumerateEffectCommands(tbinfo, room.Cmd))
        {
            if (shown.Contains(ec.Keyword)) continue;
            string cost = requirementByKeyword.TryGetValue(ec.Keyword, out TBInfoActionResolver.CommandRequirement pc)
                ? FormatRequirement(pc)
                : string.Empty;
            if (bestByKeyword.TryGetValue(ec.Keyword, out var existing))
            {
                if (existing.Kind <= ec.Kind) continue;
                bestByKeyword[ec.Keyword] = (ec.Kind, ec.TargetId, cost);
            }
            else
            {
                bestByKeyword[ec.Keyword] = (ec.Kind, ec.TargetId, cost);
                keywordOrder.Add(ec.Keyword);
            }
        }

        // Then fold synonyms together: same effect AND same cost is one offer (two
        // keywords that summon the same monster for different prices are not).
        var keys = new List<(TBInfoActionResolver.RoomEffectKind Kind, int TargetId, string Cost)>();
        var keywordsPerRow = new List<List<string>>();
        Dictionary<(TBInfoActionResolver.RoomEffectKind, int, string), int> rowIndex = new();

        foreach (string keyword in keywordOrder)
        {
            var key = bestByKeyword[keyword];
            if (!rowIndex.TryGetValue(key, out int i))
            {
                rowIndex[key] = i = keys.Count;
                keys.Add(key);
                keywordsPerRow.Add(new List<string>());
            }
            keywordsPerRow[i].Add(keyword);
        }

        var rows = new List<RoomEffectRow>(keys.Count);
        for (int i = 0; i < keys.Count; i++)
            rows.Add(new RoomEffectRow(
                keywordsPerRow[i], keys[i].Kind, keys[i].TargetId,
                FormatRoomEffect(data, keys[i].Kind, keys[i].TargetId), keys[i].Cost));
        return rows;
    }

    // Plain-language effect for a room command the directive resolvers explain by
    // outcome rather than destination ("summons obsidian statue", "teaches form of
    // the crane"). An unresolvable id degrades to "#N" rather than dropping the row
    // — knowing a command summons *something* still matters.
    private static string FormatRoomEffect(GameDataCache? data,
        TBInfoActionResolver.RoomEffectKind kind, int targetId)
    {
        string Named(string table) =>
            LookupName(data, table, targetId) is { Length: > 0 } n ? n : $"#{targetId}";

        return kind switch
        {
            TBInfoActionResolver.RoomEffectKind.Summon => $"summons {Named("Monsters")}",
            TBInfoActionResolver.RoomEffectKind.LearnSpell => $"teaches {Named("Spells")}",
            TBInfoActionResolver.RoomEffectKind.PlaceRoomItem => $"drops {Named("Items")} in the room",
            TBInfoActionResolver.RoomEffectKind.TakeItem => $"takes {Named("Items")}",
            // Ability ids resolve against no shipped table, so this one is unnamed
            // by construction (see RoomEffectCommand).
            _ => "grants an ability",
        };
    }

    // Everything a command demands, in one phrase: "costs 200 Platinum",
    // "Level 50+", or "costs 200 Platinum, Level 50+". A captain's passage
    // carries both, and surfacing only the fare hid why the sailing would be
    // refused.
    // includeLevel is false when the caller has already rendered the floor —
    // a teleport prints it beside the destination it reads it from.
    private static string FormatRequirement(
        TBInfoActionResolver.CommandRequirement pc, bool includeLevel = true)
    {
        string charge = pc.MaxCopper > 0 ? FormatCharge(pc) : string.Empty;
        string level = includeLevel && pc.MinLevel > 0
            ? RoomExit.FormatLevelGate(pc.MinLevel, 0)
            : string.Empty;
        if (charge.Length == 0) return level;
        return level.Length == 0 ? charge : $"{charge}, {level}";
    }

    // "costs 100 Gold", or for a tiered charge (the jail bribe-guard's escalating
    // prices) "costs up to 10 Runic (takes the most you can afford)". Copper is
    // reduced to its friendliest coin by the shared shop formatter.
    private static string FormatCharge(TBInfoActionResolver.CommandRequirement pc)
    {
        string amount = ShopPriceCalculator.FormatCopper(pc.MaxCopper);
        return pc.Tiered
            ? $"costs up to {amount} (takes the most you can afford)"
            : $"costs {amount}";
    }

    private static string FormatDest(RoomGraphManager graph, RoomKey key)
    {
        Room? dest = graph.GetRoom(key);
        return dest is not null ? $"{dest.DisplayName} ({key})" : key.ToString();
    }

    // One cast-delivered teleport command (a keyword set + the rooms it can drop
    // the player into). Several synonyms casting the same teleport spell share a
    // group; Random is set when the spell lands in a random room of a multi-room
    // range.
    private sealed class CastTeleportGroup
    {
        public List<string> Keywords { get; } = new();
        public IReadOnlyList<RoomKey> Destinations { get; init; } = Array.Empty<RoomKey>();
        public bool Random { get; init; }
        public int MinLevel { get; set; }
    }

    private static List<CastTeleportGroup> ResolveCastGroups(
        Room room, TBInfoStore tbinfo, Game.Spells.KnownSpellCatalog? spellCatalog)
    {
        List<CastTeleportGroup> groups = new();
        if (spellCatalog is null) return groups;

        Dictionary<string, CastTeleportGroup> bySig = new();
        foreach ((string keyword, IReadOnlyList<RoomKey> dests, bool random, int minLevel)
                 in TBInfoCastTeleportResolver.EnumerateCastTeleports(
                        tbinfo, room.Cmd, room.Key.Map, spellCatalog))
        {
            string sig = string.Join(",", dests);
            if (!bySig.TryGetValue(sig, out CastTeleportGroup? g))
            {
                g = new CastTeleportGroup { Destinations = dests, Random = random };
                bySig[sig] = g;
                groups.Add(g);
            }
            if (!g.Keywords.Contains(keyword)) g.Keywords.Add(keyword);
            if (minLevel > g.MinLevel) g.MinLevel = minLevel;
        }
        return groups;
    }

    // ----- Lair tag parsing -----------------------------------------

    // Extracts just the Max-regen count, for the "Max Regen: N" line.
    public static bool TryParseLairMax(string? lairTag, out int max)
    {
        max = 0;
        if (string.IsNullOrEmpty(lairTag)) return false;
        Match m = s_maxPattern.Match(lairTag);
        if (!m.Success) return false;
        return int.TryParse(m.Groups["n"].Value, out max);
    }

    // Pulls the Max-N + monster ID list out of a raw lair tag. Tolerant of NMR
    // < 1.83 (no trailing bracket) and NMR ≥ 1.83 (trailing [group-index]).
    public static void ParseLairTag(string lairTag, out int? max, out IReadOnlyList<int> monsterIds)
    {
        max = null;
        monsterIds = Array.Empty<int>();

        Match mm = s_maxPattern.Match(lairTag);
        if (mm.Success && int.TryParse(mm.Groups["n"].Value, out int m))
            max = m;

        int colon = lairTag.IndexOf(':');
        if (colon < 0 || colon == lairTag.Length - 1) return;

        string tail = lairTag[(colon + 1)..].Trim();
        var ids = new List<int>();
        foreach (string token in tail.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = token.Trim();
            if (trimmed.StartsWith('[')) break;       // group-index bracket
            if (int.TryParse(trimmed, out int id) && id > 0) ids.Add(id);
        }
        monsterIds = ids;
    }

    private static readonly Regex s_maxPattern = new(@"\(Max\s+(?<n>\d+)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ----- Regen time ----------------------------------------------

    private static string BuildRegenTime(int delay)
    {
        // GreaterMUD formula: (Delay-1) minutes + 30 seconds.
        if (delay <= 0) return string.Empty;
        int minutes = delay - 1;
        return minutes > 0 ? $"{minutes}m 30s" : "30s";
    }

    // ----- GameDataCache lookup ------------------------------------

    private static string? LookupName(GameDataCache? data, string table, int id)
        => data?.FindNameByNumber(table, id);
}
