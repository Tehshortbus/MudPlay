using System.Collections.Generic;
using MudPlay.Services;

namespace MudPlay.Game.Map;

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

    // One room-CMD remoteaction that reveals a hidden exit: the keyword the player
    // types, the room whose exit it opens, and the direction index of that exit
    // (0..9 = N,S,E,W,NE,NW,SE,SW,U,D — the RemoteAction arg order the game uses).
    public readonly record struct RemoteActionExit(string Keyword, int RoomNumber, int DirectionIndex);

    // Yields each `<keyword>:…:remoteaction <room> <msg> <var> <dir>` line in a
    // room's CMD chain as a RemoteActionExit. Unlike EnumerateRemoteActionKeywords
    // (tooltip-only — keyword alone), this carries the room + direction the action
    // reveals so the graph build can promote that hidden exit into a routable,
    // satisfiable action. A middle `testskill` is ignored here: the walker just
    // issues the keyword and the reveal follows (a zero-difficulty check never
    // fails; a real one is a separate concern the dispatch would surface).
    public static IEnumerable<RemoteActionExit> EnumerateRemoteActions(TBInfoStore store, int roomCmd)
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
                if (!parts[i].StartsWith("remoteaction", StringComparison.OrdinalIgnoreCase)) continue;
                if (TryParseRemoteAction(parts[i], out int room, out int dir) && room > 0 && dir is >= 0 and <= 9)
                    yield return new RemoteActionExit(keyword, room, dir);
                break;
            }
        }
    }

    // `remoteaction <room> <message> <var> <direction> …` — the room is the first
    // number, the direction index is the fourth (message + one variable sit
    // between). Mirrors TBInfoActionDecoder.ParseRemoteAction's arg positions.
    private static bool TryParseRemoteAction(string token, out int room, out int direction)
    {
        room = 0;
        direction = -1;
        string[] w = token.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length < 5) return false;                 // remoteaction room msg var dir
        return int.TryParse(w[1], out room) & int.TryParse(w[4], out direction);
    }

    // Yields the player-typed keyword from each line whose directive chain performs
    // an in-place effect — a `giveitem` or `random` directive — the mine / gather
    // commands in the Dwarven Mines ("mine ore", "mine vein", …, which yield ore
    // via a checkitem/testskill gate). Lines that teleport, cast, or fire a
    // remoteaction are surfaced by their own resolvers (TBInfoTeleportResolver /
    // TBInfoCastTeleportResolver / EnumerateRemoteActionKeywords), so they're
    // excluded here to keep a keyword from being listed twice. Order preserved
    // (recognised verbs first, synonyms second); duplicates left for the caller to
    // dedupe.
    public static IEnumerable<string> EnumerateRoomActionKeywords(TBInfoStore store, int roomCmd)
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

            bool yieldsItem = false;
            bool routedElsewhere = false;
            for (int i = 1; i < parts.Length; i++)
            {
                string directive = parts[i];
                if (directive.StartsWith("giveitem", StringComparison.OrdinalIgnoreCase)
                 || directive.StartsWith("random", StringComparison.OrdinalIgnoreCase))
                    yieldsItem = true;
                else if (directive.StartsWith("teleport", StringComparison.OrdinalIgnoreCase)
                      || directive.StartsWith("cast", StringComparison.OrdinalIgnoreCase)
                      || directive.StartsWith("remoteaction", StringComparison.OrdinalIgnoreCase))
                    routedElsewhere = true;
            }
            if (yieldsItem && !routedElsewhere) yield return keyword;
        }
    }

    // One paid room command: the player keyword, the highest `price` it charges
    // (in copper, the base coin), and whether the line carries several DISTINCT
    // escalating prices. The tiered case is the jail "bribe guard" — its six
    // powers-of-ten prices mean "the guard takes the largest tier you can afford,
    // up to the max" (confirmed by the user), so only the ceiling is meaningful.
    // MinLevel is the `minlevel N [failTextblock]` floor on the same line, 0 when
    // the command has none. A charge and a level floor travel together often
    // enough (a captain's passage carries both) that they share a row.
    public readonly record struct PricedCommand(string Keyword, long MaxCopper, bool Tiered, int MinLevel = 0);

    // Yields the paid commands in a room's CMD chain — every keyword line that
    // carries at least one `price <copper> [failTextblock]` directive (gambling,
    // healer / summon buys, passage fares, bribe guard). The first integer after
    // `price` is the copper cost; a second integer is the can't-afford textblock,
    // not a cost. MaxCopper is the largest price on the line; Tiered is set when
    // the line lists more than one distinct price.
    public static IEnumerable<PricedCommand> EnumeratePricedCommands(TBInfoStore store, int roomCmd)
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

            long max = 0;
            int minLevel = 0;
            var distinct = new HashSet<long>();
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("minlevel ", StringComparison.OrdinalIgnoreCase))
                {
                    // `minlevel <N> [failTextblock]` — the second arg is the
                    // refusal text, not part of the level.
                    string[] lvl = parts[i][9..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (lvl.Length >= 1) int.TryParse(lvl[0], out minLevel);
                    continue;
                }
                if (!parts[i].StartsWith("price", StringComparison.OrdinalIgnoreCase)) continue;
                if (TryFirstAmount(parts[i], out long copper) && copper > 0)
                {
                    distinct.Add(copper);
                    if (copper > max) max = copper;
                }
            }
            // A level floor alone earns a row: the player still needs telling why
            // the command will refuse them, charge or no charge.
            if (distinct.Count == 0 && minLevel <= 0) continue;
            yield return new PricedCommand(keyword, max, distinct.Count > 1, minLevel);
        }
    }

    // First whole number in a `price 100 1560` token (skips the `price` word,
    // returns 100 — the copper cost, not the trailing fail-textblock id).
    private static bool TryFirstAmount(string token, out long value)
    {
        foreach (string word in token.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (long.TryParse(word, out value)) return true;
        value = 0;
        return false;
    }

    // What a room command does when none of the resolvers above explains it. The
    // four they cover (teleport, price, remoteaction, giveitem/random) are not the
    // whole directive vocabulary, so a room whose only command was one of these
    // rendered an empty tooltip — 8/461's "touch statue" summons the obsidian
    // statue carrying the town gate key and nothing surfaced it (report
    // paradigm-20260911-010954). Across the shipped sets that blind spot covers 25
    // summoning rooms, 13 ability-granting rooms, and each realm's spell-trainer
    // hall.
    public enum RoomEffectKind
    {
        Summon,         // `summon <monster>` — spawns a monster in the room.
        LearnSpell,     // `learnspell <spell>` — a trainer / totem turn-in teaches it.
        GrantAbility,   // `giveability` / `addability` — an ability or quest-flag award.
        PlaceRoomItem,  // `roomitem <item>` — drops an item on the floor to `get`.
        TakeItem,       // `takeitem <item>` — hands an item over with nothing returned.
    }

    // One room command explained by its effect. TargetId is the monster / spell /
    // item the directive names, or 0 for GrantAbility — an ability id has no table
    // to resolve against (quests.json ships empty in every set), so the caller
    // phrases that one without a name.
    public readonly record struct RoomEffectCommand(string Keyword, RoomEffectKind Kind, int TargetId);

    // Yields each keyword line whose effect only one of the directives above
    // explains. Lines already surfaced elsewhere (teleport / cast / remoteaction /
    // giveitem / random) are skipped so a keyword never renders twice; `price`
    // lines are left in, and the caller drops them in favour of its own priced row
    // exactly as it does for the room-action keywords.
    //
    // One effect per keyword, highest-priority first: a summon outranks the rest
    // because a spawned monster is the consequence worth warning about before the
    // reward it guards ("touch hammer" both drops an item and summons a frost
    // hydra — the hydra is the part you want to read first).
    public static IEnumerable<RoomEffectCommand> EnumerateEffectCommands(TBInfoStore store, int roomCmd)
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
            if (!IsPlayerKeyword(keyword)) continue;

            bool routedElsewhere = false;
            RoomEffectKind? kind = null;
            int targetId = 0;

            for (int i = 1; i < parts.Length; i++)
            {
                string d = parts[i];
                if (StartsWithWord(d, "teleport") || StartsWithWord(d, "cast")
                    || StartsWithWord(d, "remoteaction") || StartsWithWord(d, "giveitem")
                    || StartsWithWord(d, "random"))
                {
                    routedElsewhere = true;
                    break;
                }
                TryTakeEffect(d, ref kind, ref targetId);
            }

            if (!routedElsewhere && kind is { } k)
                yield return new RoomEffectCommand(keyword, k, targetId);
        }
    }

    // Keep the highest-priority effect seen on a line. Priority is the declaration
    // order of RoomEffectKind, so a later-but-weaker directive can't displace one
    // already found.
    private static void TryTakeEffect(string directive, ref RoomEffectKind? kind, ref int targetId)
    {
        RoomEffectKind found;
        if (StartsWithWord(directive, "summon")) found = RoomEffectKind.Summon;
        else if (StartsWithWord(directive, "learnspell")) found = RoomEffectKind.LearnSpell;
        else if (StartsWithWord(directive, "giveability")
              || StartsWithWord(directive, "addability")) found = RoomEffectKind.GrantAbility;
        else if (StartsWithWord(directive, "roomitem")) found = RoomEffectKind.PlaceRoomItem;
        else if (StartsWithWord(directive, "takeitem")) found = RoomEffectKind.TakeItem;
        else return;

        if (kind is { } existing && existing <= found) return;
        kind = found;
        // An ability id names nothing resolvable, so it stays 0 and the caller
        // phrases the row without a subject.
        targetId = found == RoomEffectKind.GrantAbility ? 0 : FirstArg(directive);
    }

    // True when the directive's first word is exactly `word` — `StartsWith` alone
    // would read `addability` as `add` or `takecoins` as `takeitem`'s neighbour.
    private static bool StartsWithWord(string directive, string word)
        => directive.StartsWith(word, StringComparison.OrdinalIgnoreCase)
           && (directive.Length == word.Length || directive[word.Length] == ' ');

    // First integer argument of a `<verb> <id> [failTextblock]` directive.
    private static int FirstArg(string directive)
    {
        string[] w = directive.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return w.Length >= 2 && int.TryParse(w[1], out int id) && id > 0 ? id : 0;
    }

    // A room CMD block is a keyword menu, but it can also carry directive-led
    // continuation lines and numeric d100 roll bands ("90:message 4063:summon
    // 2111"). Only a genuine player-typed keyword earns a tooltip row — without
    // this guard a roll band would surface as a command called "90".
    //
    // Sharing a first word with a directive is NOT enough to reject a line: the
    // player-house command is literally `summon healer`, and the ganghouses have
    // `summon guard` / `summon elite guard` / `summon spellbreaker`. What separates
    // them from a directive is the ARGUMENT — a directive's arguments are record
    // numbers (`summon 347`, `message 3216`, `evilaligned -50`) or further directive
    // heads (`check class`, which leads the per-race continuation lines the export
    // repeats under a trainer's keyword), while a keyword's are ordinary words. So a
    // directive head disqualifies the token only when it stands alone or its
    // argument is itself directive-shaped.
    private static bool IsPlayerKeyword(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return false;

        bool allDigits = true;
        foreach (char c in keyword)
            if (!char.IsAsciiDigit(c)) { allDigits = false; break; }
        if (allDigits) return false;

        if (!EffectDirectiveHeads.Contains(FirstWord(keyword))) return true;
        string[] words = keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return false;
        return !int.TryParse(words[1], out _) && !EffectDirectiveHeads.Contains(words[1]);
    }

    private static string FirstWord(string s)
    {
        int space = s.IndexOf(' ');
        return space < 0 ? s : s[..space];
    }

    // The directive heads that can legitimately lead a line in a room CMD block.
    // A line starting with one of these is an engine continuation, not something
    // the player types.
    private static readonly HashSet<string> EffectDirectiveHeads =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "summon", "learnspell", "giveability", "addability", "roomitem", "takeitem",
            "giveitem", "teleport", "cast", "remoteaction", "price", "message", "text",
            "random", "checkitem", "clearitem", "checkability", "testability",
            "failability", "failitem", "failroomitem", "nomonsters", "needmonster",
            "minlevel", "maxlevel", "class", "race", "levelcheck", "goodaligned",
            "evilaligned", "addevil", "addgood", "check", "testskill", "givecoins",
            "takecoins", "adddelay", "delay", "addexp",
        };
}
