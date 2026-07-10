using System.Collections.Generic;
using System.Globalization;
using FujinTerm.Game.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// One decoded line in a monster-greet breakdown. Text is the display string
// (already resolved to names); Depth is the indent level — 0 is the player
// command keyword, deeper levels are the effects/flags that command triggers.
// The view turns Depth into leading spaces so the tree reads as an outline.
public readonly record struct TBInfoActionLine(string Text, int Depth);

// Faithful port of MegaMUD Explorer's textblock command decoder (its
// AddCommandNode). A monster's greet textblock lists player-typeable keywords,
// one per line as `keyword:pointer`, where pointer is another textblock whose
// colon-separated directives are the "flags" that keyword triggers — cast a
// spell, take/give an item, teach a spell, award experience, teleport, summon,
// gate on a class/race/ability check, roll a weighted random outcome, and so on.
// This walks that tree and produces a flat, depth-tagged line list the greet
// popup renders: "what can I ask, and what happens when I do".
//
// Two intentional departures from the reference so the popup stays tight:
//   • Synonym keywords that point at the SAME block are grouped onto one line
//     ("tolgard OR box OR package") and the block decoded once, rather than
//     repeating the identical subtree per keyword.
//   • Generic/raw directives are always shown (equivalent to the reference with
//     its "hide textblocks" option off) so no flag is silently dropped.
public static class TBInfoActionDecoder
{
    // Bounds mirroring the reference's nest cap — a malformed self-referential
    // chain can't spin the UI. The per-branch ancestor set breaks true cycles;
    // these bound pathological depth/breadth too.
    private const int MaxDepth = 40;
    private const int MaxLines = 400;

    // Decode a monster's greet textblock (GreetTXT) into the keyword/effect tree.
    // Empty when the number is unknown or the block carries no player keywords
    // (a pure dialogue greet).
    public static IReadOnlyList<TBInfoActionLine> DecodeGreet(
        TBInfoStore store, GameDataCache cache, RoomGraphManager? rooms, int greetNumber)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cache);
        Decoder d = new(store, cache, rooms);
        d.ProcessGreetBlock(greetNumber);
        return d.Lines;
    }

    private sealed class Decoder
    {
        private readonly TBInfoStore _store;
        private readonly GameDataCache _cache;
        private readonly RoomGraphManager? _rooms;
        private readonly HashSet<int> _greetVisited = new();
        public readonly List<TBInfoActionLine> Lines = new();

        public Decoder(TBInfoStore store, GameDataCache cache, RoomGraphManager? rooms)
        {
            _store = store;
            _cache = cache;
            _rooms = rooms;
        }

        private bool Capped => Lines.Count >= MaxLines;

        private void Emit(string text, int depth)
        {
            if (!Capped) Lines.Add(new TBInfoActionLine(text, depth));
        }

        // A greet block's lines are `keyword:pointer`. Group synonyms sharing a
        // pointer, emit the joined keyword line, then decode the pointed-to
        // directive block once beneath it.
        public void ProcessGreetBlock(int number)
        {
            if (number <= 0 || Capped) return;
            if (!_greetVisited.Add(number)) return;

            TBInfoEntry? entry = _store.GetEntry(number);
            if (entry is null) return;
            if (string.IsNullOrEmpty(entry.Action))
            {
                // A greet block with no keyword lines is pure dialogue; the real
                // keyword block can hang off its LinkTo.
                if (entry.LinkTo > 0) ProcessGreetBlock(entry.LinkTo);
                return;
            }

            List<string> order = new();
            Dictionary<string, List<string>> keywordsByKey = new();
            Dictionary<string, (int Pointer, string Tail)> payloadByKey = new();

            foreach (string line in SplitLines(entry.Action))
            {
                int colon = line.IndexOf(':');
                if (colon < 0) continue; // not a keyword line
                string keyword = CleanKeyword(line[..colon]);
                if (keyword.Length == 0) continue;

                string rest = line[(colon + 1)..];
                int pointer = Val(rest);
                string key = pointer > 0 ? $"P{pointer}" : $"I{rest.Trim()}";

                if (!keywordsByKey.TryGetValue(key, out List<string>? kws))
                {
                    kws = new List<string>();
                    keywordsByKey[key] = kws;
                    payloadByKey[key] = (pointer, rest);
                    order.Add(key);
                }
                if (!kws.Contains(keyword)) kws.Add(keyword);
            }

            foreach (string key in order)
            {
                if (Capped) break;
                Emit(string.Join(" OR ", keywordsByKey[key]), 0);
                (int pointer, string rest) = payloadByKey[key];
                HashSet<int> ancestors = new() { number };
                if (pointer > 0)
                    DecodeBlock(pointer, 1, ancestors);
                else if (!string.IsNullOrWhiteSpace(rest))
                    DecodeDirectiveLine(rest, 1, ancestors);
            }
        }

        // A directive block: each newline-separated line is a colon-separated
        // sequence of directives. A block with no Action is dialogue-only and
        // continues at its LinkTo.
        private void DecodeBlock(int number, int depth, HashSet<int> ancestors)
        {
            if (Capped || depth > MaxDepth) return;
            if (ancestors.Contains(number))
            {
                Emit($"(loop → textblock {number})", depth);
                return;
            }
            TBInfoEntry? entry = _store.GetEntry(number);
            if (entry is null)
            {
                Emit($"(textblock {number} not found)", depth);
                return;
            }

            ancestors.Add(number);
            try
            {
                if (string.IsNullOrEmpty(entry.Action))
                {
                    if (entry.LinkTo > 0) DecodeBlock(entry.LinkTo, depth, ancestors);
                    return;
                }

                List<string> lines = SplitLines(entry.Action);
                bool multi = lines.Count > 1;
                int lineNo = 0;
                foreach (string line in lines)
                {
                    if (Capped) break;
                    if (multi)
                    {
                        lineNo++;
                        Emit($"Line {lineNo}", depth);
                        DecodeDirectiveLine(line, depth + 1, ancestors);
                    }
                    else
                    {
                        DecodeDirectiveLine(line, depth, ancestors);
                    }
                }
            }
            finally
            {
                ancestors.Remove(number);
            }
        }

        // A weighted-random block: each line is `weight:directives`, where the
        // running weight is cumulative so the per-outcome chance is the delta.
        private void DecodeRandomBlock(int number, int depth, HashSet<int> ancestors)
        {
            if (Capped || depth > MaxDepth) return;
            if (ancestors.Contains(number))
            {
                Emit($"(loop → random {number})", depth);
                return;
            }
            TBInfoEntry? entry = _store.GetEntry(number);
            if (entry is null)
            {
                Emit($"(textblock {number} not found)", depth);
                return;
            }

            ancestors.Add(number);
            try
            {
                if (string.IsNullOrEmpty(entry.Action))
                {
                    if (entry.LinkTo > 0) DecodeBlock(entry.LinkTo, depth, ancestors);
                    return;
                }

                int prev = 0;
                foreach (string line in SplitLines(entry.Action))
                {
                    if (Capped) break;
                    int colon = line.IndexOf(':');
                    if (colon < 0) continue;
                    int cur = Val(line[..colon]);
                    int delta = cur - prev;
                    prev = cur;
                    Emit($"{delta}%", depth);
                    string rest = line[(colon + 1)..];
                    if (!string.IsNullOrWhiteSpace(rest))
                        DecodeDirectiveLine(rest, depth + 1, ancestors);
                }
            }
            finally
            {
                ancestors.Remove(number);
            }
        }

        // Decode one line's colon-separated directive tokens. Consecutive
        // identical tokens collapse to a single "… (xN)" line, mirroring the
        // reference (blocks that repeat "cast 5:cast 5:cast 5").
        private void DecodeDirectiveLine(string line, int depth, HashSet<int> ancestors)
        {
            List<string> tokens = SplitColonTokens(line);
            int i = 0;
            while (i < tokens.Count && !Capped)
            {
                string tok = tokens[i];
                int reps = 1;
                while (i + reps < tokens.Count &&
                       string.Equals(tokens[i + reps], tok, StringComparison.Ordinal))
                    reps++;

                int before = Lines.Count;
                DecodeDirective(tok, depth, ancestors);
                if (reps > 1 && Lines.Count > before)
                {
                    TBInfoActionLine first = Lines[before];
                    Lines[before] = first with { Text = first.Text + $" (x{reps})" };
                }
                i += reps;
            }
        }

        // The directive decode table — same order and semantics as the
        // reference's If/ElseIf chain. Each recognised directive resolves its
        // record id to a name; unrecognised tokens (minlevel, align, message,
        // text, takeitem-0, testability, …) fall through to a raw line so no
        // flag is hidden.
        private void DecodeDirective(string tok, int depth, HashSet<int> ancestors)
        {
            if (Contains(tok, "cast "))
            {
                int v = Extract(tok, "cast ");
                if (v > 0) Emit($"Cast: {SpellName(v)}", depth);
            }
            else if (Contains(tok, "item "))
            {
                int v = Extract(tok, "item ");
                if (v > 0)
                {
                    string prefix = tok[..IndexOf(tok, "item ")].Trim();
                    Emit(prefix.Length > 0
                        ? $"Item, {prefix}: {ItemName(v)}"
                        : $"Item: {ItemName(v)}", depth);
                }
                else if (Contains(tok, "clearitem 0"))
                {
                    Emit("Clear all items from room", depth);
                }
            }
            else if (Contains(tok, "ability ") && !Contains(tok, "testability"))
            {
                int v = Extract(tok, "ability ");
                if (v > 0)
                {
                    string prefix = tok[..IndexOf(tok, "ability ")].Trim();
                    string ability = AbilityNames.GetName(v) ?? $"ability {v}";
                    Emit(prefix.Length > 0
                        ? $"Ability, {prefix}: {ability} ({tok.Trim()})"
                        : $"Ability: {ability} ({tok.Trim()})", depth);
                }
            }
            else if (Contains(tok, "class "))
            {
                int v = Extract(tok, "class ");
                if (v > 0) Emit($"Class: {ClassName(v)}", depth);
            }
            else if (Contains(tok, "race "))
            {
                int v = Extract(tok, "race ");
                if (v > 0) Emit($"Race: {RaceName(v)}", depth);
            }
            else if (Contains(tok, "addexp "))
            {
                int v = Extract(tok, "addexp ");
                if (v > 0) Emit($"AddExp: {Commas(v)}", depth);
            }
            else if (Contains(tok, "learnspell "))
            {
                int v = Extract(tok, "learnspell ");
                if (v > 0) Emit($"Learnspell: {SpellName(v)}", depth);
            }
            else if (Contains(tok, "checkspell "))
            {
                int v = Extract(tok, "checkspell ");
                if (v > 0) Emit($"Checkspell: {SpellName(v)}", depth);
                // A second number is the textblock jumped to when the check
                // fails; the reference tolerates a stray double space before it.
                int v2 = Extract(tok, $"checkspell {v}");
                if (v2 == 0) v2 = Extract(tok, $"checkspell  {v}");
                if (v2 > 0) DecodeBlock(v2, depth + 1, ancestors);
            }
            else if (Contains(tok, "summon "))
            {
                int v = Extract(tok, "summon ");
                if (v > 0) Emit($"Summon: {MonsterName(v)}", depth);
            }
            else if (Contains(tok, "random "))
            {
                int v = Extract(tok, "random ");
                if (v > 0)
                {
                    Emit($"random: {v}", depth);
                    DecodeRandomBlock(v, depth + 1, ancestors);
                }
            }
            else if (Contains(tok, "price "))
            {
                int v = Extract(tok, "price ");
                if (v > 0) Emit($"Cost: {Commas(v)}{Coin(tok)}", depth);
            }
            else if (Contains(tok, "givecoins "))
            {
                int v = Extract(tok, "givecoins ");
                if (v > 0) Emit($"Givecoins: {Commas(v)}{Coin(tok)}", depth);
            }
            else if (Contains(tok, "teleport "))
            {
                (int room, int map) = ParseTeleport(tok);
                if (room != 0 && map != 0) Emit($"Teleport: {RoomName(map, room)}", depth);
                else Emit(tok.Trim(), depth);
            }
            else if (Contains(tok, "remoteaction "))
            {
                (int room, string exit) = ParseRemoteAction(tok);
                // No default-map context in a monster greet, so the target room
                // is shown by number rather than resolved to a name.
                if (room != 0) Emit($"Remote Action{exit}: room {room}", depth);
                else Emit(tok.Trim(), depth);
            }
            else if (Contains(tok, "testskill "))
            {
                int target = ParseTestskillTarget(tok);
                Emit(tok.Trim(), depth);
                if (target != 0) DecodeBlock(target, depth + 1, ancestors);
            }
            else
            {
                Emit(tok.Trim(), depth);
            }
        }

        // ----- Name resolvers -----

        private string SpellName(int n)   => Named("Spells",   n, "spell");
        private string ItemName(int n)    => Named("Items",    n, "item");
        private string MonsterName(int n) => Named("Monsters", n, "monster");
        private string ClassName(int n)   => Named("Classes",  n, "class");
        private string RaceName(int n)    => Named("Races",    n, "race");

        private string Named(string table, int number, string kind)
        {
            string? name = _cache.FindNameByNumber(table, number);
            return string.IsNullOrEmpty(name) ? $"{kind} #{number}" : name;
        }

        private string RoomName(int map, int room)
        {
            if (_rooms is not null)
            {
                Room? r = _rooms.GetRoom(new RoomKey(map, room));
                if (r is not null && !r.HasUnknownName) return $"{r.Name} ({map}/{room})";
            }
            return $"{map}/{room}";
        }
    }

    // ----- Directive-argument parsers (port of the reference's inline scans) -----

    // `teleport <room> <map>` — room is the first integer, map the second.
    private static (int Room, int Map) ParseTeleport(string tok)
    {
        int idx = tok.IndexOf("teleport ", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return (0, 0);
        int p = idx + "teleport ".Length;
        int i = p;
        while (i < tok.Length && IsDigit(tok[i])) i++;
        if (i == p) return (0, 0);
        int room = ParseSlice(tok, p, i);
        if (i < tok.Length && tok[i] == ' ') i++;
        int j = i;
        while (j < tok.Length && IsDigit(tok[j])) j++;
        int map = j > i ? ParseSlice(tok, i, j) : 0;
        return (room, map);
    }

    // `remoteaction <room> <message> <var> <direction> …` — read the room, skip
    // the message word and one variable, then read the direction index.
    private static (int Room, string Exit) ParseRemoteAction(string tok)
    {
        int idx = tok.IndexOf("remoteaction ", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return (0, string.Empty);
        int p = idx + "remoteaction ".Length;
        int i = p;
        while (i < tok.Length && IsDigit(tok[i])) i++;
        if (i == p) return (0, string.Empty);
        int room = ParseSlice(tok, p, i);

        int sp1 = tok.IndexOf(' ', i + 1);      // space after the message word
        if (sp1 < 0) return (room, string.Empty);
        int sp2 = tok.IndexOf(' ', sp1 + 1);    // space after the first variable
        if (sp2 < 0) return (room, string.Empty);

        int d = sp2 + 1;
        int j = d;
        while (j < tok.Length && IsDigit(tok[j])) j++;
        if (j == d) return (room, string.Empty);

        return (room, ParseSlice(tok, d, j) switch
        {
            0 => " (on the N exit)",
            1 => " (on the S exit)",
            2 => " (on the E exit)",
            3 => " (on the W exit)",
            4 => " (on the NE exit)",
            5 => " (on the NW exit)",
            6 => " (on the SE exit)",
            7 => " (on the SW exit)",
            8 => " (on the U exit)",
            9 => " (on the D exit)",
            _ => string.Empty,
        });
    }

    // `testskill <skill> <amount> <targetTB> …` — skip the skill and amount
    // words, then read the target textblock the check jumps to.
    private static int ParseTestskillTarget(string tok)
    {
        int idx = tok.IndexOf("testskill ", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;
        int p = idx + "testskill ".Length;
        int sp1 = tok.IndexOf(' ', p);          // after the skill word
        if (sp1 < 0) return 0;
        int sp2 = tok.IndexOf(' ', sp1 + 1);    // after the test amount
        if (sp2 < 0) return 0;
        int t = sp2 + 1;
        int j = t;
        while (j < tok.Length && IsDigit(tok[j])) j++;
        return j > t ? ParseSlice(tok, t, j) : 0;
    }

    // ----- Small helpers -----

    // Port of the reference's ExtractValueFromString: case-insensitively find
    // search, skip any leading spaces/asterisks after it, then read the
    // contiguous digit run. 0 when none.
    private static int Extract(string whole, string search)
    {
        int idx = whole.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;
        int x = idx + search.Length;
        int y = x;
        while (y < whole.Length)
        {
            char c = whole[y];
            if (IsDigit(c)) { y++; continue; }
            if (c == ' ' || c == '*')
            {
                if (y > x) break;   // already collected digits
                x++; y++;           // skip a leading space/asterisk
                continue;
            }
            break;                  // any other char ends the run
        }
        return y > x ? ParseSlice(whole, x, y) : 0;
    }

    // Leading-integer parse (VB val semantics): skip leading whitespace, take an
    // optional sign and the digit run, ignore the rest. 0 when no digits.
    private static int Val(string s)
    {
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        int start = i;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
        int digits = i;
        while (i < s.Length && IsDigit(s[i])) i++;
        if (i == digits) return 0;
        return int.TryParse(s[start..i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
    }

    private static int ParseSlice(string s, int from, int to)
        => int.TryParse(s.AsSpan(from, to - from), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private static bool Contains(string s, string sub) => s.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;

    private static int IndexOf(string s, string sub) => s.IndexOf(sub, StringComparison.OrdinalIgnoreCase);

    private static string Commas(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    // Trailing coin letter → denomination, matching the reference's Right()
    // switch (R/P/G/S, else copper).
    private static string Coin(string tok)
    {
        char last = tok.Length > 0 ? char.ToUpperInvariant(tok[^1]) : ' ';
        return last switch
        {
            'R' => " runic",
            'P' => " platinum",
            'G' => " gold",
            'S' => " silver",
            _   => " copper",
        };
    }

    // Non-empty, trimmed newline-separated lines.
    private static List<string> SplitLines(string action)
    {
        List<string> result = new();
        foreach (string raw in action.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length > 0) result.Add(line);
        }
        return result;
    }

    // Non-empty, trimmed colon-separated tokens of one directive line.
    private static List<string> SplitColonTokens(string line)
    {
        List<string> result = new();
        foreach (string raw in line.Split(':'))
        {
            string tok = raw.Trim();
            if (tok.Length > 0) result.Add(tok);
        }
        return result;
    }

    // Keyword cleaning matches the reference: '*' (hidden-command marker) is
    // stripped and '|' (alternate-keyword separator) becomes " OR ".
    private static string CleanKeyword(string raw)
        => raw.Replace("*", string.Empty)
              .Replace("|", " OR ")
              .Trim();
}
