using System.Collections.Generic;
using FujinTerm.Game.Spells;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Resolves a TBInfo CMD chain whose keyword fires a teleport via a spell cast (a
// cast <spell> directive) into the keyword plus the set of rooms the player can
// land in. Third sibling to TBInfoTeleportResolver (literal
// teleport <room> <map>) and TBInfoActionResolver (remoteaction).
//
// MajorMUD delivers some room teleports indirectly: the CMD chain casts a spell
// whose TeleportRoom (Abil 140) / TeleportMap (Abil 141) abilities move the
// caster. Example — v1.11p map 1 rooms 178-180, CMD 9115 → spell 923 "bridge
// jump":
//
//     jump west:message 2664:cast 923
//     jump east:message 2664:cast 923
//
// When the spell's AbilVal-140 is a fixed room number the destination is that
// single room. When it's 0 the destination is a random room in the spell's
// MinBase..MaxBase range — "bridge jump" plops the player into one of 5 river
// rooms. The walker can't predict which, so the map surfaces every possibility
// (and the caller treats the post-jump position as uncertain).
public static class TBInfoCastTeleportResolver
{
    // Ability codes: the room and map a teleport spell moves the caster to.
    // AbilVal-140 == 0 means "random room in MinBase..MaxBase"; non-zero is a
    // fixed room.
    private const int TeleportRoomCode = 140;
    private const int TeleportMapCode  = 141;

    // Defensive ceiling on a random range's size. A real teleport range
    // is a handful of rooms; a wildly larger span is a misparse (or a
    // non-teleport spell sharing the 140 slot) we don't want exploding
    // the tooltip into hundreds of lines.
    private const int MaxRandomRange = 64;

    // Walk every cast <spell> directive in the CMD's Action chain whose spell
    // teleports, and yield (keyword, destinations, random, minLevel). sourceMap
    // is the map of the room the command is typed in — used as the destination
    // map when the spell carries no explicit TeleportMap (Abil 141) value.
    // catalog resolves the spell number to its formula + ability list. Lines
    // whose cast spell isn't a teleport are skipped.
    public static IEnumerable<(string Keyword, IReadOnlyList<RoomKey> Destinations, bool Random, int MinLevel)>
        EnumerateCastTeleports(TBInfoStore store, int roomCmd, int sourceMap, KnownSpellCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);
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

            int spellNumber = 0;
            int minLevel = 0;
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("cast ", StringComparison.OrdinalIgnoreCase))
                {
                    // `cast <spell> [args]` — the first token after the
                    // verb is the Spells.Number; ignore any trailing args.
                    string arg = parts[i][5..].Trim();
                    int sp = arg.IndexOf(' ');
                    if (sp >= 0) arg = arg[..sp];
                    int.TryParse(arg, out spellNumber);
                }
                else if (parts[i].StartsWith("minlevel ", StringComparison.OrdinalIgnoreCase))
                {
                    // `minlevel <N> [failTB]` — first arg is the level floor.
                    string[] lvlArgs = parts[i][9..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (lvlArgs.Length >= 1) int.TryParse(lvlArgs[0], out minLevel);
                }
            }
            if (spellNumber <= 0) continue;

            if (catalog.GetFormulaByNumber(spellNumber) is not { } spell) continue;

            int? teleRoom = null;
            int? teleMap = null;
            foreach (SpellAbility ab in spell.Abilities)
            {
                if (ab.Code == TeleportRoomCode) teleRoom = ab.Value;
                else if (ab.Code == TeleportMapCode) teleMap = ab.Value;
            }
            if (teleRoom is null) continue; // cast spell isn't a teleport

            int map = teleMap is { } tm && tm > 0 ? tm : sourceMap;

            List<RoomKey> dests = new();
            bool random;
            if (teleRoom.Value > 0)
            {
                // Fixed destination room.
                dests.Add(new RoomKey(map, teleRoom.Value));
                random = false;
            }
            else
            {
                // Random destination — one room in MinBase..MaxBase.
                int lo = spell.MinBase;
                int hi = spell.MaxBase;
                if (hi < lo) (lo, hi) = (hi, lo);
                if (lo <= 0 || hi - lo + 1 > MaxRandomRange) continue;
                for (int r = lo; r <= hi; r++) dests.Add(new RoomKey(map, r));
                random = dests.Count > 1;
            }
            if (dests.Count == 0) continue;

            yield return (keyword, dests, random, minLevel);
        }
    }
}
