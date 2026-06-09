using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Builds the plain-text hover tooltip for a room on the Navigation
/// map. Field order + phrasing match MMUD-Explorer
/// (<c>frmMap.frm:MapMapExits</c>); Lair Exp/HP/Dmg-per-clear is
/// intentionally omitted per the user's directive — those numbers
/// need character-side calculations we don't track yet.
/// </summary>
/// <remarks>
/// <para>
/// <b>Field order</b> (blank-line separated where indicated):
/// <list type="number">
///   <item>Name (Map/Room)</item>
///   <item>Also Here (NPC + lair monsters with Max-N)</item>
///   <item>Light description ("pitch black" / "very dark" / "barely
///         visible" / "dimly lit") — surfaced when the room's own
///         Light is significantly negative.</item>
///   <item><i>blank</i></item>
///   <item>Shop: …</item>
///   <item>Room Spell: …</item>
///   <item><i>blank</i></item>
///   <item>Obvious exits: per-direction list with destination room
///         name + (map/room) + Door / Trap / gated annotation.</item>
///   <item><i>blank</i></item>
///   <item>Room Light: ±N</item>
///   <item>Max Regen: N @ (Delay-1)m 30s</item>
/// </list>
/// </para>
/// <para>
/// Lair string format expected (per the MDB):
/// <c>"(Max N): id,id,...,[group-index]"</c>. Older NMR &lt; 1.83
/// imports may omit the trailing bracket; the parser tolerates both.
/// </para>
/// </remarks>
public static class RoomTooltipBuilder
{
    public static string Build(Room room, RoomGraphManager graph, GameDataCache? data,
        TBInfoStore? tbinfo = null, MonsterSpawnIndex? spawnIndex = null)
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

        // 9. Room commands — TBInfo CMD chains for the room (use chime,
        // ring chime, etc. — keyword-triggered teleports that bypass
        // normal exits). Grouped per-destination so identical-target
        // synonyms collapse to one line.
        string commandsBlock = BuildRoomCommandsBlock(room, graph, tbinfo);
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
            string lightDesc = BuildLightDescription(room.Light);
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

    private static string BuildAlsoHere(Room room, GameDataCache? data, MonsterSpawnIndex? spawnIndex)
    {
        var names = new List<string>();
        int? max = null;

        if (!string.IsNullOrEmpty(room.RawLairTag))
        {
            ParseLairTag(room.RawLairTag, out max, out IReadOnlyList<int> monsterIds);
            foreach (int id in monsterIds)
            {
                string? name = LookupName(data, "Monsters", id);
                if (!string.IsNullOrEmpty(name) && !names.Contains(name)) names.Add(name);
            }
        }

        // Append boss / script-spawn monsters whose presence in this
        // room lives on the monster's "Summoned By" field instead of
        // (or in addition to) the room's lair tag. These don't count
        // against the lair tag's Max-N — they're separate respawn
        // mechanics — so the count prefix stays driven by the lair tag.
        if (spawnIndex is not null)
        {
            foreach (int id in spawnIndex.MonsterIdsSummonedAt(room.Key))
            {
                string? name = LookupName(data, "Monsters", id);
                if (!string.IsNullOrEmpty(name) && !names.Contains(name)) names.Add(name);
            }
        }

        if (names.Count == 0) return string.Empty;

        string prefix = max is { } m ? $"Also Here ({m}): " : "Also Here: ";
        return prefix + string.Join(", ", names);
    }

    // ----- Light description ---------------------------------------

    private static string BuildLightDescription(int light)
    {
        // MMUD-Explorer's mapping (frmMap.frm:44617-44626). We render
        // the descriptive line based on the room's own Light value;
        // the player-illu-relative variant lands once we have a stat
        // parser for the player's current illumination.
        if (light <= -200) return "The room is pitch black";
        if (light <= -150) return "The room is very dark — you can't see anything";
        if (light <= -100) return "The room is barely visible";
        if (light <  0)    return "The room is dimly lit";
        return string.Empty;
    }

    // ----- Exits block ---------------------------------------------

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
                    AppendMultiActionDetail(sb, room.Key, maDetail, graph);
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

    /// <summary>
    /// Per-step breakdown rendered beneath a MultiActionHidden exit:
    /// one indented line per <see cref="ExitAction"/> with the trigger
    /// room (when the action lives in another room) plus its
    /// alternative commands. Mirrors the format the walker actually
    /// executes — the user sees the same routing the path expander
    /// would do.
    /// </summary>
    private static void AppendMultiActionDetail(
        StringBuilder sb, RoomKey hostRoom, MultiActionExitData ma, RoomGraphManager graph)
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
        }
    }

    /// <summary>
    /// TBInfo fallback for MultiActionHidden exits whose unlock lives
    /// in a CMD chain rather than Action#N exit cells. Walks the
    /// chain via <see cref="TBInfoActionResolver"/> and renders the
    /// gathered keywords as a single indented "Try: kw1 / kw2 / …"
    /// line. The keywords all run in the room being hovered (TBInfo
    /// CMDs are local to their owning room), so no "here:" / "at X:"
    /// prefix is needed.
    /// </summary>
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

    /// <summary>
    /// Render the parenthetical exit qualifier, looking up the
    /// underlying record name when a hint carries a structured id.
    /// Item/Ticket → Items table. KeyLocked → Items table (the key is
    /// itself an Item record per MDB convention). Falls back to the
    /// raw hint string for unclassified modifiers so diagnostic info
    /// still shows.
    /// </summary>
    private static string FormatExitHint(RoomExit exit, GameDataCache? data)
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
                return string.IsNullOrEmpty(exit.RawHint) ? string.Empty : exit.RawHint!;

            default:
                return exit.Hint.ToString();
        }
    }

    private static string DirectionLabel(Direction d) => d switch
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

    // ----- Room commands (TBInfo CMD chains) ------------------------

    private static string BuildRoomCommandsBlock(Room room, RoomGraphManager graph, TBInfoStore? tbinfo)
    {
        if (tbinfo is null || room.Cmd <= 0) return string.Empty;

        // Group destination → list of keywords so multi-synonym CMDs
        // ("use chime" / "ring chime" both teleporting to 1/65) render
        // as one line instead of cluttering the tooltip.
        Dictionary<RoomKey, List<string>> byDest = new();
        foreach ((string keyword, RoomKey dest)
                 in TBInfoTeleportResolver.EnumerateTeleports(tbinfo, room.Cmd))
        {
            if (!byDest.TryGetValue(dest, out List<string>? words))
                byDest[dest] = words = new List<string>();
            if (!words.Contains(keyword)) words.Add(keyword);
        }
        if (byDest.Count == 0) return string.Empty;

        StringBuilder sb = new();
        sb.Append("Room commands:");
        foreach (KeyValuePair<RoomKey, List<string>> entry in byDest)
        {
            Room? dest = graph.GetRoom(entry.Key);
            string destName = dest is not null
                ? $"{dest.DisplayName} ({entry.Key})"
                : entry.Key.ToString();
            sb.Append('\n').Append("  ")
              .Append(string.Join(" / ", entry.Value))
              .Append(" → ").Append(destName);
        }
        return sb.ToString();
    }

    // ----- Lair tag parsing -----------------------------------------

    /// <summary>Extracts just the Max-regen count, for the "Max Regen: N" line.</summary>
    public static bool TryParseLairMax(string? lairTag, out int max)
    {
        max = 0;
        if (string.IsNullOrEmpty(lairTag)) return false;
        Match m = s_maxPattern.Match(lairTag);
        if (!m.Success) return false;
        return int.TryParse(m.Groups["n"].Value, out max);
    }

    /// <summary>
    /// Pulls the Max-N + monster ID list out of a raw lair tag.
    /// Tolerant of NMR &lt; 1.83 (no trailing bracket) and NMR ≥ 1.83
    /// (trailing <c>[group-index]</c>).
    /// </summary>
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
