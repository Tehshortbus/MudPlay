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
    public static string Build(Room room, RoomGraphManager graph, GameDataCache? data)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(graph);

        StringBuilder sb = new();

        // 1. Name (Map/Room)
        sb.Append(room.Name).Append(" (").Append(room.Key).Append(')');

        // 2. Also Here
        string alsoHere = BuildAlsoHere(room, data);
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
        string exitsBlock = BuildExitsBlock(room, graph);
        if (exitsBlock.Length > 0)
        {
            sb.Append('\n').Append('\n').Append(exitsBlock);
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

    private static string BuildAlsoHere(Room room, GameDataCache? data)
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

    private static string BuildExitsBlock(Room room, RoomGraphManager graph)
    {
        if (room.Exits.Count == 0) return string.Empty;

        StringBuilder sb = new();
        sb.Append("Obvious exits:");
        foreach (Direction dir in s_exitOrder)
        {
            if (!room.Exits.TryGetValue(dir, out RoomExit exit)) continue;

            Room? dest = graph.GetRoom(exit.Target);
            string destName = dest?.Name ?? exit.Target.ToString();

            sb.Append('\n').Append("  ").Append(DirectionLabel(dir)).Append(" → ");
            sb.Append(destName).Append(' ').Append('(').Append(exit.Target).Append(')');

            if (exit.Hint != RoomExitHint.None)
                sb.Append(" (").Append(exit.Hint).Append(')');
            else if (!string.IsNullOrEmpty(exit.RawHint))
                sb.Append(" (").Append(exit.RawHint).Append(')');
        }
        return sb.ToString();
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
