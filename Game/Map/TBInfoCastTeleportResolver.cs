using System.Collections.Generic;
using FujinTerm.Game.Spells;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Resolves a TBInfo CMD chain whose keyword fires a teleport <i>via a
/// spell cast</i> (a <c>cast &lt;spell&gt;</c> directive) into the
/// keyword plus the set of rooms the player can land in. Third sibling
/// to <see cref="TBInfoTeleportResolver"/> (literal
/// <c>teleport &lt;room&gt; &lt;map&gt;</c>) and
/// <see cref="TBInfoActionResolver"/> (<c>remoteaction</c>).
/// </summary>
/// <remarks>
/// <para>
/// MajorMUD delivers some room teleports indirectly: the CMD chain
/// casts a spell whose <c>TeleportRoom</c> (Abil 140) / <c>TeleportMap</c>
/// (Abil 141) abilities move the caster. Example — v1.11p map 1 rooms
/// 178-180, CMD 9115 → spell 923 "bridge jump":
/// </para>
/// <code>
/// jump west:message 2664:cast 923
/// jump east:message 2664:cast 923
/// </code>
/// <para>
/// When the spell's <c>AbilVal-140</c> is a fixed room number the
/// destination is that single room. When it's <c>0</c> the destination
/// is a <b>random</b> room in the spell's <c>MinBase..MaxBase</c> range —
/// "bridge jump" plops the player into one of 5 river rooms. The walker
/// can't predict which, so the map surfaces every possibility (and the
/// caller treats the post-jump position as uncertain).
/// </para>
/// <para>
/// MMUD-Explorer never resolves this for its map view — its
/// <c>GetTextblockCMDS</c> lists keywords only; only its spell pane
/// (<c>PullSpellEQ</c>) expands the range. Surfacing it on the room
/// tooltip is a deliberate FujinTerm improvement.
/// </para>
/// </remarks>
public static class TBInfoCastTeleportResolver
{
    // MME ability codes (AbilityNames / modMMudDatabase.bas): the room
    // and map a teleport spell moves the caster to. AbilVal-140 == 0
    // means "random room in MinBase..MaxBase"; non-zero is a fixed room.
    private const int TeleportRoomCode = 140;
    private const int TeleportMapCode  = 141;

    // Defensive ceiling on a random range's size. A real teleport range
    // is a handful of rooms; a wildly larger span is a misparse (or a
    // non-teleport spell sharing the 140 slot) we don't want exploding
    // the tooltip into hundreds of lines.
    private const int MaxRandomRange = 64;

    /// <summary>
    /// Walk every <c>cast &lt;spell&gt;</c> directive in the CMD's Action
    /// chain whose spell teleports, and yield <c>(keyword, destinations,
    /// random, minLevel)</c>. <paramref name="sourceMap"/> is the map of
    /// the room the command is typed in — used as the destination map when
    /// the spell carries no explicit <c>TeleportMap</c> (Abil 141) value.
    /// <paramref name="catalog"/> resolves the spell number to its formula
    /// + ability list. Lines whose cast spell isn't a teleport are skipped.
    /// </summary>
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
