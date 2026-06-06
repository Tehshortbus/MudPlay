using System.Collections.Generic;
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

        foreach ((string keyword, RoomKey dest) in EnumerateTeleports(store, roomCmd))
        {
            if (dest.Equals(destination)) return keyword;
        }
        return null;
    }

    /// <summary>
    /// Walk every <c>teleport &lt;room&gt; &lt;map&gt;</c> directive in
    /// the CMD's Action chain and yield <c>(keyword, destination)</c>
    /// for each one. Used by the room tooltip to surface "use chime →
    /// 1/65" style commands so the user can see how to traverse a
    /// teleport-bypassed door without opening the game data browser.
    /// </summary>
    public static IEnumerable<(string Keyword, RoomKey Destination)>
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

                yield return (keyword, new RoomKey(map, room));
                break;  // first teleport in the line is the destination — don't yield duplicates if a chained teleport appears later
            }
        }
    }
}
