using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Resolves a TBInfo CMD chain into the player-typed keywords for a remoteaction
// directive. Sibling to TBInfoTeleportResolver — same Action-string parse shape,
// different terminal directive.
//
// A remoteaction line looks like this (verified against the v1.11p data for map
// 9 / room 1012, CMD 1422):
//
//     clear rubble:testskill strength 0 1423:remoteaction 1012 1840 0 0
//     move rubble:testskill strength 0 1423:remoteaction 1012 1840 0 0
//     push rubble:testskill strength 0 1423:remoteaction 1012 1840 0 0
//
// Each line's first colon-separated token is the keyword the player types. The
// middle testskill may gate success on a stat check; the final remoteaction
// describes what changes in the world when the check passes. For the hover
// tooltip we only care about surfacing the keyword candidates — the user wants
// to know what to type. The walker's prerequisite-action expander owns the full
// semantics (skill check + remote-action firing).
public static class TBInfoActionResolver
{
    // Yields the player-typed keyword from each line in the CMD's Action chain
    // that ends with a remoteaction directive. Skips lines without such a
    // directive (teleport / message-only branches are not action
    // prerequisites). Duplicates preserved because the order in the Action chain
    // may be meaningful to the user (recognised verbs first, synonyms second).
    public static IEnumerable<string> EnumerateRemoteActionKeywords(TBInfoStore store, int roomCmd)
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

            bool hasRemote = false;
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("remoteaction", StringComparison.OrdinalIgnoreCase))
                {
                    hasRemote = true;
                    break;
                }
            }
            if (hasRemote) yield return keyword;
        }
    }
}
