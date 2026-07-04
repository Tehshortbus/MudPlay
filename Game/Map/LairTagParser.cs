using System.Text.RegularExpressions;

namespace FujinTerm.Game.Map;

// Parses the Rooms.Lair cell into the structured fields the Auto-Lair
// scheduler needs. The MDB stores two shapes:
//   NMR 1.83+: [GROUP-INDEX][MAX-REGEN]Group(lair): MAP/ROOM — the lair's
//     spawn-rule group key (the table key into Lairs.json), the per-room
//     max simultaneous spawn count, and a back-pointer to the "canonical"
//     lair room in the group.
//   Pre-1.83: comma-separated monster ids (e.g. "12, 34, 56"). No group
//     key; the scheduler falls back to the slowest respawn across the
//     listed ids.
public static partial class LairTagParser
{
    // Parse raw (the Room.RawLairTag content). Returns null when the input
    // is empty, whitespace, or doesn't match any known shape — the room is
    // a lair, but we can't extract structure from the tag alone.
    public static LairTagInfo? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string text = raw.Trim();

        Match groupMatch = GroupTagRegex().Match(text);
        if (groupMatch.Success)
        {
            string groupIndex = groupMatch.Groups["group"].Value;
            int.TryParse(groupMatch.Groups["regen"].ValueSpan, out int maxRegen);
            int.TryParse(groupMatch.Groups["map"].ValueSpan, out int refMap);
            int.TryParse(groupMatch.Groups["room"].ValueSpan, out int refRoom);
            RoomKey? referenceKey = refMap > 0 && refRoom > 0
                ? new RoomKey(refMap, refRoom)
                : (RoomKey?)null;
            return new LairTagInfo(groupIndex, maxRegen, referenceKey, MonsterIds: Array.Empty<int>());
        }

        // Pre-1.83 fallback: a literal list of monster ids.
        Match listMatch = MonsterListRegex().Match(text);
        if (listMatch.Success)
        {
            string list = listMatch.Value;
            List<int> ids = new();
            foreach (string token in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(token, out int id) && id > 0) ids.Add(id);
            }
            if (ids.Count > 0)
                return new LairTagInfo(GroupIndex: null, MaxRegen: 0, ReferenceRoom: null, MonsterIds: ids);
        }

        return null;
    }

    // NMR 1.83+ shape.
    [GeneratedRegex(@"^\[(?<group>[\d-]+)\]\[(?<regen>\d+)\]Group\(lair\):\s*(?<map>\d+)/(?<room>\d+)\s*$",
        RegexOptions.Compiled)]
    private static partial Regex GroupTagRegex();

    // Pre-1.83 shape — comma-separated monster ids only.
    [GeneratedRegex(@"^\d+(?:\s*,\s*\d+)*\s*$", RegexOptions.Compiled)]
    private static partial Regex MonsterListRegex();
}

// Structured form of a Room.RawLairTag. Either GroupIndex is set (NMR
// 1.83+) OR MonsterIds is non-empty (pre-1.83) — never both, and never
// neither (in which case LairTagParser.TryParse returns null).
//   GroupIndex — 3-hyphenated-numbers spawn-rule group key, or null for pre-1.83.
//   MaxRegen — per-room max simultaneous spawn count (NMR 1.83+ only).
//   ReferenceRoom — back-pointer to the canonical lair room in the group (NMR 1.83+ only).
//   MonsterIds — pre-1.83 monster id list; empty when the tag carries a GroupIndex instead.
public sealed record LairTagInfo(
    string? GroupIndex,
    int MaxRegen,
    RoomKey? ReferenceRoom,
    IReadOnlyList<int> MonsterIds);
