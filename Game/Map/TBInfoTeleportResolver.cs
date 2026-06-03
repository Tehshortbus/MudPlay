using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Resolves a teleport-style exit (room <see cref="Room.Cmd"/> &gt; 0
/// + exit modifier <c>(Item: N)</c>) into the verbatim command the
/// walker types to traverse it.
/// </summary>
/// <remarks>
/// <para>
/// TBInfo <see cref="TBInfoEntry.Action"/> chains are newline-separated
/// lines; each line is colon-separated directives whose first token
/// is the keyword the player types. The teleport target is encoded
/// as a <c>teleport &lt;room&gt; &lt;map&gt;</c> directive somewhere
/// in the chain. Example:
/// </para>
/// <code>
/// go hole:message 767:teleport 487 2:message 768
/// enter hole:message 767:teleport 487 2:message 768
/// crawl hole:message 767:teleport 487 2:message 768
/// </code>
/// <para>
/// All three lines lead to room 487 on map 2; the resolver returns
/// the first matching keyword. Lines without a teleport directive
/// (NPC services, gambling outcomes, etc.) are skipped.
/// </para>
/// </remarks>
public static class TBInfoTeleportResolver
{
    /// <summary>
    /// Find the first keyword in <paramref name="store"/>'s entry for
    /// <paramref name="roomCmd"/> whose teleport directive matches
    /// <paramref name="destination"/>. Returns <c>null</c> when:
    /// the entry isn't in the store, the entry has no
    /// <c>Action</c> chain, or no line teleports to the requested key.
    /// </summary>
    public static string? Resolve(TBInfoStore store, int roomCmd, RoomKey destination)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (roomCmd <= 0) return null;

        TBInfoEntry? entry = store.GetEntry(roomCmd);
        if (entry is null || string.IsNullOrWhiteSpace(entry.Action)) return null;

        foreach (string raw in entry.Action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            string[] parts = line.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;

            string keyword = parts[0];
            if (string.IsNullOrWhiteSpace(keyword)) continue;

            for (int i = 1; i < parts.Length; i++)
            {
                if (!parts[i].StartsWith("teleport ", StringComparison.OrdinalIgnoreCase)) continue;

                // `teleport <roomNum> <mapNum>` — note Action field
                // uses (room, map) order (verified in real Rooms.json
                // dumps; map is the second token).
                string[] args = parts[i][9..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (args.Length < 2) continue;
                if (!int.TryParse(args[0], out int room)) continue;
                if (!int.TryParse(args[1], out int map))  continue;

                if (room == destination.Room && map == destination.Map)
                    return keyword;
            }
        }
        return null;
    }
}
