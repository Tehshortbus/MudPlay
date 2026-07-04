using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Resolves a teleport-style exit (room Room.Cmd > 0 + exit modifier (Item: N))
// into the verbatim command the walker types to traverse it.
//
// TBInfo Action chains are newline-separated lines; each line is colon-separated
// directives whose first token is the keyword the player types. The teleport
// target is encoded as a teleport <room> <map> directive somewhere in the
// chain. Example:
//
//     go hole:message 767:teleport 487 2:message 768
//     enter hole:message 767:teleport 487 2:message 768
//     crawl hole:message 767:teleport 487 2:message 768
//
// All three lines lead to room 487 on map 2; the resolver returns the first
// matching keyword. Lines without a teleport directive (NPC services, gambling
// outcomes, etc.) are skipped.
public static class TBInfoTeleportResolver
{
    // Find the first keyword in store's entry for roomCmd whose teleport
    // directive matches destination. Returns null when the entry isn't in the
    // store, the entry has no Action chain, or no line teleports to the
    // requested key.
    public static string? Resolve(TBInfoStore store, int roomCmd, RoomKey destination)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (roomCmd <= 0) return null;

        foreach ((string keyword, RoomKey dest, int _) in EnumerateTeleports(store, roomCmd))
        {
            if (dest.Equals(destination)) return keyword;
        }
        return null;
    }

    // Walk every teleport <room> <map> directive in the CMD's Action chain and
    // yield (keyword, destination, minLevel) for each one. Used by the room
    // tooltip to surface "use chime → 1/65" style commands (and any "Level 20+"
    // gate) so the user can see how to traverse a teleport-bypassed door without
    // opening the game data browser.
    //
    // A "minlevel N [failTB]" directive anywhere in the same line gates the
    // teleport: the player must be level ≥ N or the game jumps to the fail
    // textblock instead of teleporting. We surface N (the fail textblock id is
    // irrelevant to the walker).
    public static IEnumerable<(string Keyword, RoomKey Destination, int MinLevel)>
        EnumerateTeleports(TBInfoStore store, int roomCmd)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (roomCmd <= 0) yield break;

        TBInfoEntry? entry = store.GetEntry(roomCmd);
        if (entry is null || string.IsNullOrWhiteSpace(entry.Action)) yield break;

        foreach (string raw in entry.Action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            string[] parts = line.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;

            string keyword = parts[0];
            if (string.IsNullOrWhiteSpace(keyword)) continue;

            int minLevel = 0;
            RoomKey dest = default;
            bool haveDest = false;

            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("minlevel ", StringComparison.OrdinalIgnoreCase))
                {
                    // `minlevel <N> [failTB]` — first arg is the level floor.
                    string[] lvlArgs = parts[i][9..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (lvlArgs.Length >= 1) int.TryParse(lvlArgs[0], out minLevel);
                    continue;
                }

                if (haveDest || !parts[i].StartsWith("teleport ", StringComparison.OrdinalIgnoreCase)) continue;

                // `teleport <roomNum> <mapNum>` — note Action field
                // uses (room, map) order (verified in real Rooms.json
                // dumps; map is the second token).
                string[] args = parts[i][9..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (args.Length < 2) continue;
                if (!int.TryParse(args[0], out int room)) continue;
                if (!int.TryParse(args[1], out int map))  continue;

                dest = new RoomKey(map, room);
                haveDest = true;
                // Keep scanning the rest of the line: minlevel can appear
                // before OR after the teleport directive in the chain.
            }

            if (haveDest) yield return (keyword, dest, minLevel);
        }
    }
}
