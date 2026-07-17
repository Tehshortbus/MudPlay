using System.Collections.Generic;
using System.Globalization;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Decodes a placed monster's greet chain into the `ask` commands that open a
// door on the monster's OWN room — the "ask the guard the password and the gate
// lifts" mechanic (e.g. the grove's shadow guard, who raises the west door to
// Morukai's chamber when a Phoenix-quest character asks about "morukai").
//
// A monster greet textblock (Monsters.GreetTXT) lists player-askable keywords,
// one `keyword:pointer` per line. Following a keyword's pointer (through any
// empty-Action LinkTo hops) lands on a directive block whose colon-separated
// tokens can include:
//   • checkability <ability> <n>            — gate the effect on a quest ability
//   • remoteaction <room> <msg> <var> <dir> — operate the <dir> exit of <room>
// When <room> is the monster's own home room and <dir> names one of that room's
// exits, the keyword is a spoken command that opens that exit. The player types
// `ask <noun> <keyword>`, where <noun> is the last word of the monster's name
// ("shadow guard" → "guard").
//
// This is the lair-monster analogue of the room-CMD lever data the graph already
// promotes: RoomGraphManager feeds the resolved command into the same
// Door → MultiActionHidden promotion so the walker crosses the guarded door with
// the ask-then-move dispatch, instead of the route planner discarding it as an
// unpickable / unbashable door.
public static class GuardDoorCommandResolver
{
    // One guard-opened exit: the verbatim command that opens it, the exit
    // direction it lifts, and the ability the game gates the open behind (0 when
    // ungated). The gate is advisory — the client can't track quest abilities, so
    // the walker issues the command and reacts to whether the door actually opens.
    public readonly record struct GuardDoorCommand(
        string Command, Direction Direction, int AbilityGate);

    // Depth cap mirrors the display decoder — a malformed self-referential greet
    // chain can't spin the resolver. The per-walk visited set breaks true cycles.
    private const int MaxDepth = 40;

    // Yield every guard-door command a monster's greet exposes for its own home
    // room. greetNumber is Monsters.GreetTXT; monsterName is Monsters.Name (its
    // last word becomes the `ask` noun); hostRoomNumber is the Room Number the
    // monster stands in (matched against each remoteaction's target room). Empty
    // when the greet exposes no keyword that operates a home-room exit.
    public static IEnumerable<GuardDoorCommand> Resolve(
        TBInfoStore store, int greetNumber, string? monsterName, int hostRoomNumber)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (greetNumber <= 0 || hostRoomNumber <= 0) yield break;

        string noun = LastWord(monsterName);
        if (noun.Length == 0) yield break;

        TBInfoEntry? greet = ResolveGreetBlock(store, greetNumber, new HashSet<int>());
        if (greet is null || string.IsNullOrEmpty(greet.Action)) yield break;

        foreach (string raw in greet.Action.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':');
            if (colon < 0) continue; // not a keyword line

            string keyword = CleanKeyword(line[..colon]);
            if (keyword.Length == 0) continue;

            int pointer = LeadingInt(line[(colon + 1)..]);
            if (pointer <= 0) continue;

            if (TryResolveExit(store, pointer, hostRoomNumber, new HashSet<int>(),
                    out Direction dir, out int abilityGate))
            {
                yield return new GuardDoorCommand($"ask {noun} {keyword}", dir, abilityGate);
            }
        }
    }

    // Follow empty-Action LinkTo hops until an entry with keyword lines (a real
    // greet block) is reached — a pure-dialogue greet hangs its keyword block off
    // LinkTo. Returns null on a dead or looping chain.
    private static TBInfoEntry? ResolveGreetBlock(TBInfoStore store, int number, HashSet<int> visited)
    {
        while (number > 0 && visited.Add(number))
        {
            TBInfoEntry? entry = store.GetEntry(number);
            if (entry is null) return null;
            if (!string.IsNullOrEmpty(entry.Action)) return entry;
            number = entry.LinkTo;
        }
        return null;
    }

    // Walk the directive block a greet keyword points at (through empty-Action
    // LinkTo hops) and report the first remoteaction that operates an exit of
    // hostRoomNumber, plus any checkability ability gate seen on the way. Only the
    // pointed block (and its empty-Action LinkTo chain) is scanned — a non-empty
    // block that carries no matching remoteaction is a dead end for our purposes.
    private static bool TryResolveExit(TBInfoStore store, int number, int hostRoomNumber,
        HashSet<int> visited, out Direction direction, out int abilityGate)
    {
        direction = default;
        abilityGate = 0;
        int depth = 0;
        while (number > 0 && depth++ < MaxDepth && visited.Add(number))
        {
            TBInfoEntry? entry = store.GetEntry(number);
            if (entry is null) return false;
            if (string.IsNullOrEmpty(entry.Action))
            {
                number = entry.LinkTo;
                continue;
            }

            foreach (string raw in entry.Action.Split('\n'))
            {
                foreach (string tokenRaw in raw.Split(':'))
                {
                    string token = tokenRaw.Trim();
                    if (token.StartsWith("checkability ", StringComparison.OrdinalIgnoreCase))
                    {
                        int abil = FirstIntAfter(token, "checkability ");
                        if (abil > 0) abilityGate = abil;
                    }
                    else if (token.StartsWith("remoteaction ", StringComparison.OrdinalIgnoreCase)
                             && TryParseRemoteAction(token, out int targetRoom, out Direction dir)
                             && targetRoom == hostRoomNumber)
                    {
                        direction = dir;
                        return true;
                    }
                }
            }
            return false;
        }
        return false;
    }

    // `remoteaction <room> <msg> <var> <dir>` — room is the 1st integer after the
    // verb, dir the 4th (a message word and one variable sit between). Mirrors the
    // game's field layout and the display decoder's parse.
    private static bool TryParseRemoteAction(string token, out int room, out Direction direction)
    {
        room = 0;
        direction = default;
        string[] parts = token.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5) return false;
        if (!int.TryParse(parts[1], out room)) return false;
        if (!int.TryParse(parts[4], out int dirIndex)) return false;
        Direction? d = DirectionFromIndex(dirIndex);
        if (d is null) return false;
        direction = d.Value;
        return true;
    }

    // remoteaction direction index → Direction. 0=N … 9=D, matching the game's
    // exit-slot ordering (identical to the display decoder's map and the
    // Direction enum's own values).
    private static Direction? DirectionFromIndex(int index) => index switch
    {
        0 => Direction.N,
        1 => Direction.S,
        2 => Direction.E,
        3 => Direction.W,
        4 => Direction.NE,
        5 => Direction.NW,
        6 => Direction.SE,
        7 => Direction.SW,
        8 => Direction.U,
        9 => Direction.D,
        _ => null,
    };

    private static string LastWord(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        string[] words = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? string.Empty : words[^1];
    }

    // Strip the hidden-command '*' marker and take the first alternate before a
    // '|' — the walker types one concrete keyword.
    private static string CleanKeyword(string raw)
    {
        string s = raw.Replace("*", string.Empty).Trim();
        int bar = s.IndexOf('|');
        if (bar >= 0) s = s[..bar].Trim();
        return s;
    }

    private static int FirstIntAfter(string token, string prefix)
    {
        int idx = token.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;
        return LeadingInt(token[(idx + prefix.Length)..]);
    }

    // Skip leading whitespace, then read the contiguous digit run. 0 when none.
    private static int LeadingInt(string s)
    {
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        int start = i;
        while (i < s.Length && s[i] is >= '0' and <= '9') i++;
        return i > start && int.TryParse(s.AsSpan(start, i - start), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int n) ? n : 0;
    }
}
