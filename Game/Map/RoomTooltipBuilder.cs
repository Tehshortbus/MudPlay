using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FujinTerm.Game.Light;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Builds the plain-text hover tooltip for a room on the Navigation map. Lair
// Exp/HP/Dmg-per-clear is intentionally omitted — those numbers need
// character-side calculations we don't track yet.
//
// Field order (blank-line separated where indicated):
//   1. Name (Map/Room)
//   2. Also Here (NPC + lair monsters with Max-N)
//   3. Light description ("pitch black" / "very dark" / "barely visible" /
//      "dimly lit") — surfaced when the room's own Light is significantly
//      negative.
//   4. blank
//   5. Shop: …
//   6. Room Spell: …
//   7. blank
//   8. Obvious exits: per-direction list with destination room name +
//      (map/room) + Door / Trap / gated annotation.
//   9. blank
//   10. Room Light: ±N
//   11. Max Regen: N @ (Delay-1)m 30s
//
// Lair string format expected (per the MDB): "(Max N): id,id,...,[group-index]".
// Older NMR < 1.83 imports may omit the trailing bracket; the parser tolerates
// both.
public static class RoomTooltipBuilder
{
    public static string Build(Room room, RoomGraphManager graph, GameDataCache? data,
        TBInfoStore? tbinfo = null, MonsterSpawnIndex? spawnIndex = null,
        Game.Spells.KnownSpellCatalog? spellCatalog = null, int charIllu = 0)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(graph);

        StringBuilder sb = new();

        // 1. Name (Map/Room)
        sb.Append(room.DisplayName).Append(" (").Append(room.Key).Append(')');

        // 2. Also Here
        string alsoHere = BuildAlsoHere(room, data, spawnIndex);
        if (alsoHere.Length > 0) sb.Append('\n').Append(alsoHere);

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
        string commandsBlock = BuildRoomCommandsBlock(room, graph, tbinfo, spellCatalog);
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

        // 11. Max Regen + regen time.
        if (TryParseLairMax(room.RawLairTag, out int maxRegen))
        {
            if (needBottomBlank) { sb.Append('\n'); needBottomBlank = false; }
            sb.Append('\n').Append("Max Regen: ").Append(maxRegen);
            string regenTime = BuildRegenTime(room.Delay);
            if (regenTime.Length > 0) sb.Append(" @ ").Append(regenTime);
        }

        return sb.ToString();
    }

    // ----- Also Here -------------------------------------------------

    // A monster present in a room, resolved to its record Number + display name.
    public readonly record struct RoomMonsterRef(int Id, string Name);

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

    private static string BuildAlsoHere(Room room, GameDataCache? data, MonsterSpawnIndex? spawnIndex)
    {
        IReadOnlyList<RoomMonsterRef> refs = ResolveAlsoHere(room, data, spawnIndex, out int? max);
        if (refs.Count == 0) return string.Empty;

        string prefix = max is { } m ? $"Also Here ({m}): " : "Also Here: ";
        return prefix + string.Join(", ", refs.Select(r => r.Name));
    }

    // ----- Light description ---------------------------------------

    private static string BuildLightDescription(int light, int charIllu)
        // Visibility is a function of V = charIllu + roomLight: a lit lantern or
        // worn +illu gear lifts a dark room out of the "can't see" bands, so the
        // phrase reflects what the player actually sees, not the room's raw
        // offset. Shares LightModel's band table so the tooltip and the route
        // predictor never drift.
        => LightModel.Describe(LightModel.Classify(charIllu, roomLight: light));

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

        string commandsBlock = BuildRoomCommandsBlock(room, graph, tbinfo, spellCatalog);
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
                return itemName is { Length: > 0 }
                    ? $"{label}: {itemName}"
                    : $"{label}: #{exit.KeyItemId}";
            }

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
        TBInfoStore? tbinfo, Game.Spells.KnownSpellCatalog? spellCatalog)
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

        // Room-action keywords (`remoteaction` CMD lines — "pull drawer",
        // "clear rubble", etc.) that change the world in place rather than
        // teleporting. These already surface beneath a MultiActionHidden exit
        // via AppendTbInfoActionFallback, so only list them here when no such
        // exit claimed them — otherwise a room with both would render the same
        // keyword twice. A room whose only special interaction is a standalone
        // room action (e.g. 1/381's "pull drawer", with just a normal door
        // exit) has no MultiActionHidden exit, so the fallback never fired and
        // the keyword would go unshown without this.
        List<string> actionKeywords = new();
        bool shownByExit = room.Exits.Values.Any(e =>
            e.Hint == RoomExitHint.MultiActionHidden
            && e.MultiAction is not { Actions.Count: > 0 });
        if (!shownByExit)
        {
            foreach (string kw in TBInfoActionResolver.EnumerateRemoteActionKeywords(tbinfo, room.Cmd))
                if (!actionKeywords.Contains(kw, StringComparer.OrdinalIgnoreCase))
                    actionKeywords.Add(kw);
        }

        if (byDest.Count == 0 && castGroups.Count == 0 && actionKeywords.Count == 0)
            return string.Empty;

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
        }
        foreach (CastTeleportGroup g in castGroups)
        {
            sb.Append('\n').Append("  ")
              .Append(string.Join(" / ", g.Keywords)).Append(" → ");
            if (g.Destinations.Count == 1)
            {
                sb.Append(FormatDest(graph, g.Destinations[0]));
                if (g.MinLevel > 0)
                    sb.Append(" (").Append(RoomExit.FormatLevelGate(g.MinLevel, 0)).Append(')');
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
                sb.Append(':');
                foreach (RoomKey d in g.Destinations)
                    sb.Append('\n').Append("      ").Append(FormatDest(graph, d));
            }
        }
        if (actionKeywords.Count > 0)
            sb.Append('\n').Append("  ").Append(string.Join(" / ", actionKeywords))
              .Append(" (room action)");
        return sb.ToString();
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
