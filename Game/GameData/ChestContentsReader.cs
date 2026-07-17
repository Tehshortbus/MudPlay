using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.GameData;

// One possible drop from a chest: the item and the chance (0..1) that a single
// `open` yields at least one of it, aggregated across every draw the chest makes.
public readonly record struct ChestDrop(int ItemId, string ItemName, double Probability);

// A container's decoded loot table for display: the aggregate per-item drop
// chances (highest first) plus how many items one open yields. MinItems ==
// MaxItems when every draw is guaranteed to hand out exactly one item; they
// diverge when a draw can land on a message-only / failitem bracket (0 items) or
// on a nested table that hands out several. Coins are omitted — chests do drop
// coins in-game, but the loot tables carry no `givecoins` token so the amount
// isn't derivable from the data.
public sealed record ChestContents(int MinItems, int MaxItems, IReadOnlyList<ChestDrop> Drops);

// Resolves a container item's loot table from the active game-data set. The
// chain is Items(ItemType 8) → Abil 43 (CastSpell) → Spells → Abil 148 (castsp)
// → TBInfo, whose Action is the open script: `message` (flavour), `giveitem`
// (guaranteed), and `random T` tokens — one independent draw each. A `random`
// target table's lines are cumulative-threshold brackets; the selected bracket
// runs its own directives (giveitem / nested random / message / failitem). The
// per-item chance is at-least-once across all draws, `1 − ∏(1 − p_draw)`.
//
// Reads raw tables through GameDataCache like ShopInventoryReader — deliberately
// self-contained (no dependency on the map layer's greet decoder) even though
// both walk the same TBInfo grammar; the directive set a chest touches is a
// small, clean subset.
public static class ChestContentsReader
{
    private const int ContainerItemType = 8;
    private const int CastSpellAbil = 43;    // item ability whose value is the open spell
    private const int CastTextblockAbil = 148; // spell ability whose value is the loot textblock
    private const int AbilSlots = 20;
    private const int MaxDepth = 32;

    // Resolve itemNumber's chest loot table, or null when it isn't a container,
    // isn't wired to a loot textblock, or no set is active.
    public static ChestContents? Read(GameDataCache cache, int itemNumber)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (itemNumber <= 0) return null;

        JsonDocument? items = cache.GetRawTable("Items");
        if (items is null) return null;
        if (FindByNumber(items, itemNumber) is not { } itemRow) return null;
        if (ReadInt(itemRow, "ItemType") != ContainerItemType) return null;

        int spellNumber = FirstAbilValue(itemRow, CastSpellAbil);
        if (spellNumber <= 0) return null;

        JsonDocument? spells = cache.GetRawTable("Spells");
        if (spells is null) return null;
        if (FindByNumber(spells, spellNumber) is not { } spellRow) return null;

        int topTextblock = FirstAbilValue(spellRow, CastTextblockAbil);
        if (topTextblock <= 0) return null;

        JsonDocument? tbinfo = cache.GetRawTable("TBInfo");
        if (tbinfo is null) return null;

        var agg = new Aggregator(BuildTextblocks(tbinfo), BuildItemNames(items));
        return agg.Resolve(topTextblock);
    }

    // Batch decode: resolve every container's loot table in one pass, sharing the
    // item-name / textblock dictionaries and a single Aggregator so a table
    // referenced by several chests is decoded once. Returns containerItemId →
    // ChestContents (only for containers that actually yield a drop); empty when
    // no set is active or the tables are missing. Backs ItemSourceIndex's reverse
    // "found in" lookup, which needs the inverse (item → chests holding it).
    public static IReadOnlyDictionary<int, ChestContents> ReadAll(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var result = new Dictionary<int, ChestContents>();

        JsonDocument? items = cache.GetRawTable("Items");
        if (items is null) return result;
        JsonDocument? spells = cache.GetRawTable("Spells");
        if (spells is null) return result;
        JsonDocument? tbinfo = cache.GetRawTable("TBInfo");
        if (tbinfo is null) return result;

        // spellNumber → loot textblock (Abil 148 value), built once so the
        // per-container resolve is a dictionary hit rather than a Spells scan.
        var spellToTextblock = new Dictionary<int, int>();
        foreach (JsonElement spell in spells.RootElement.EnumerateArray())
        {
            if (spell.ValueKind != JsonValueKind.Object) continue;
            int number = ReadInt(spell, "Number");
            if (number <= 0) continue;
            int tb = FirstAbilValue(spell, CastTextblockAbil);
            if (tb > 0) spellToTextblock[number] = tb;
        }

        var agg = new Aggregator(BuildTextblocks(tbinfo), BuildItemNames(items));

        foreach (JsonElement item in items.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (ReadInt(item, "ItemType") != ContainerItemType) continue;
            int itemNumber = ReadInt(item, "Number");
            if (itemNumber <= 0) continue;

            int spellNumber = FirstAbilValue(item, CastSpellAbil);
            if (spellNumber <= 0) continue;
            if (!spellToTextblock.TryGetValue(spellNumber, out int topTextblock) || topTextblock <= 0)
                continue;

            ChestContents contents = agg.Resolve(topTextblock);
            if (contents.Drops.Count > 0)
                result[itemNumber] = contents;
        }
        return result;
    }

    // Walks the loot-textblock tree, memoising each weighted table's per-draw
    // item chances so a table drawn N times is decoded once. Cycles are broken by
    // the per-branch ancestor set; depth is bounded as a backstop.
    private sealed class Aggregator
    {
        private readonly IReadOnlyDictionary<int, (string Action, int LinkTo)> _tb;
        private readonly IReadOnlyDictionary<int, string> _itemNames;
        private readonly Dictionary<int, Dictionary<int, double>> _drawMemo = new();
        private readonly Dictionary<int, (int Min, int Max)> _countMemo = new();

        public Aggregator(
            IReadOnlyDictionary<int, (string, int)> tb,
            IReadOnlyDictionary<int, string> itemNames)
        {
            _tb = tb;
            _itemNames = itemNames;
        }

        // Process the top-level open script (a plain directive line, no
        // thresholds): guaranteed giveitems and one draw per `random` token.
        public ChestContents Resolve(int topTextblock)
        {
            string action = ResolveAction(topTextblock, new HashSet<int>(), depth: 0);

            var miss = new Dictionary<int, double>();
            int min = 0, max = 0;
            foreach (string line in SplitLines(action))
            {
                foreach (string tok in SplitTokens(line))
                {
                    if (TryArg(tok, "giveitem", out int giveId) && giveId > 0)
                    {
                        miss[giveId] = 0.0;   // guaranteed
                        min++; max++;
                    }
                    else if (TryArg(tok, "random", out int table) && table > 0)
                    {
                        Dictionary<int, double> draw = DrawChances(table, new HashSet<int>(), depth: 1);
                        foreach ((int id, double p) in draw)
                            miss[id] = (miss.TryGetValue(id, out double m) ? m : 1.0) * (1.0 - p);
                        (int dMin, int dMax) = DrawCount(table, new HashSet<int>(), depth: 1);
                        min += dMin; max += dMax;
                    }
                }
            }

            var drops = new List<ChestDrop>();
            foreach ((int id, double missProb) in miss)
            {
                double p = 1.0 - missProb;
                if (p <= 0.0) continue;
                drops.Add(new ChestDrop(id, ItemName(id), p));
            }
            drops.Sort(static (a, b) =>
            {
                int c = b.Probability.CompareTo(a.Probability);
                return c != 0 ? c : string.Compare(a.ItemName, b.ItemName, StringComparison.OrdinalIgnoreCase);
            });

            return new ChestContents(min, max, drops);
        }

        // Per-single-draw item → P(item appears) for weighted table `number`.
        // Brackets are mutually exclusive, so an item's chance is Σ q_b · p_b.
        private Dictionary<int, double> DrawChances(int number, HashSet<int> ancestors, int depth)
        {
            if (_drawMemo.TryGetValue(number, out Dictionary<int, double>? cached)) return cached;
            var result = new Dictionary<int, double>();
            if (depth > MaxDepth || !ancestors.Add(number)) return result;

            string action = ResolveAction(number, ancestors, depth);
            int prev = 0;
            foreach (string line in SplitLines(action))
            {
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                int cur = LeadingInt(line[..colon]);
                double q = (cur - prev) / 100.0;
                prev = cur;
                if (q <= 0.0) continue;

                foreach ((int id, double p) in BracketChances(line[(colon + 1)..], ancestors, depth + 1))
                    result[id] = (result.TryGetValue(id, out double acc) ? acc : 0.0) + q * p;
            }

            ancestors.Remove(number);
            _drawMemo[number] = result;
            return result;
        }

        // Within one selected bracket, item → P(item appears at least once). A
        // giveitem is certain; a nested `random` contributes its own draw chances
        // (survival-combined if the token repeats within the bracket).
        private Dictionary<int, double> BracketChances(string rest, HashSet<int> ancestors, int depth)
        {
            var miss = new Dictionary<int, double>();
            foreach (string tok in SplitTokens(rest))
            {
                if (TryArg(tok, "giveitem", out int giveId) && giveId > 0)
                {
                    miss[giveId] = 0.0;
                }
                else if (TryArg(tok, "random", out int table) && table > 0)
                {
                    foreach ((int id, double p) in DrawChances(table, ancestors, depth))
                        miss[id] = (miss.TryGetValue(id, out double m) ? m : 1.0) * (1.0 - p);
                }
                // message / failitem / price / givecoins → no item obtained.
            }

            var result = new Dictionary<int, double>(miss.Count);
            foreach ((int id, double m) in miss) result[id] = 1.0 - m;
            return result;
        }

        // Items handed out by a single draw of table `number`: min over brackets
        // (a message/fail bracket, or an uncovered remainder, is 0), max over
        // brackets. Nested `random` recurses.
        private (int Min, int Max) DrawCount(int number, HashSet<int> ancestors, int depth)
        {
            if (_countMemo.TryGetValue(number, out (int, int) cached)) return cached;
            if (depth > MaxDepth || !ancestors.Add(number)) return (0, 0);

            string action = ResolveAction(number, ancestors, depth);
            int min = int.MaxValue, max = 0, prev = 0;
            bool anyBracket = false;
            foreach (string line in SplitLines(action))
            {
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                int cur = LeadingInt(line[..colon]);
                if (cur - prev <= 0) { prev = cur; continue; }
                prev = cur;
                anyBracket = true;

                (int bMin, int bMax) = BracketCount(line[(colon + 1)..], ancestors, depth + 1);
                if (bMin < min) min = bMin;
                if (bMax > max) max = bMax;
            }
            // An uncovered tail (thresholds stop short of 100) rolls to nothing.
            if (!anyBracket) min = 0;
            else if (prev < 100) min = 0;

            ancestors.Remove(number);
            var value = (min == int.MaxValue ? 0 : min, max);
            _countMemo[number] = value;
            return value;
        }

        private (int Min, int Max) BracketCount(string rest, HashSet<int> ancestors, int depth)
        {
            int min = 0, max = 0;
            foreach (string tok in SplitTokens(rest))
            {
                if (TryArg(tok, "giveitem", out int giveId) && giveId > 0)
                {
                    min++; max++;
                }
                else if (TryArg(tok, "random", out int table) && table > 0)
                {
                    (int dMin, int dMax) = DrawCount(table, ancestors, depth);
                    min += dMin; max += dMax;
                }
            }
            return (min, max);
        }

        // A textblock's directive text, following LinkTo when the entry itself
        // carries no Action (a pure pass-through link).
        private string ResolveAction(int number, HashSet<int> ancestors, int depth)
        {
            int guard = 0;
            while (_tb.TryGetValue(number, out (string Action, int LinkTo) entry))
            {
                if (!string.IsNullOrWhiteSpace(entry.Action)) return entry.Action;
                if (entry.LinkTo <= 0 || entry.LinkTo == number || ++guard > MaxDepth) break;
                number = entry.LinkTo;
            }
            return string.Empty;
        }

        private string ItemName(int id)
            => _itemNames.TryGetValue(id, out string? n) && !string.IsNullOrEmpty(n)
                ? n
                : $"#{id.ToString(CultureInfo.InvariantCulture)}";
    }

    // ----- Static parsing helpers (chest-directive grammar) -----

    // Non-empty, trimmed newline-separated lines.
    private static List<string> SplitLines(string action)
    {
        var result = new List<string>();
        foreach (string raw in action.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length > 0) result.Add(line);
        }
        return result;
    }

    // Non-empty, trimmed colon-separated tokens of one directive line.
    private static List<string> SplitTokens(string line)
    {
        var result = new List<string>();
        foreach (string raw in line.Split(':'))
        {
            string tok = raw.Trim();
            if (tok.Length > 0) result.Add(tok);
        }
        return result;
    }

    // `<keyword> <int>` → the trailing int, when tok begins with keyword. Tokens
    // are already colon-split and trimmed, so a simple prefix check suffices.
    private static bool TryArg(string tok, string keyword, out int value)
    {
        value = 0;
        if (!tok.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)) return false;
        int i = keyword.Length;
        if (i >= tok.Length || tok[i] != ' ') return false;
        value = LeadingInt(tok[(i + 1)..]);
        return true;
    }

    // Leading unsigned integer of a trimmed string, ignoring anything after the
    // digit run. 0 when it doesn't start with a digit.
    private static int LeadingInt(string s)
    {
        int i = 0;
        while (i < s.Length && s[i] == ' ') i++;
        int start = i;
        while (i < s.Length && s[i] is >= '0' and <= '9') i++;
        return i > start
            && int.TryParse(s.AsSpan(start, i - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n
            : 0;
    }

    private static Dictionary<int, (string Action, int LinkTo)> BuildTextblocks(JsonDocument doc)
    {
        var map = new Dictionary<int, (string, int)>();
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            int number = ReadInt(row, "Number");
            if (number <= 0) continue;
            map[number] = (ReadString(row, "Action"), ReadInt(row, "LinkTo"));
        }
        return map;
    }

    private static Dictionary<int, string> BuildItemNames(JsonDocument doc)
    {
        var map = new Dictionary<int, string>();
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            int number = ReadInt(row, "Number");
            if (number > 0) map[number] = ReadString(row, "Name");
        }
        return map;
    }

    // First AbilVal-N whose Abil-N equals code, scanning the item/spell's ability
    // slots. 0 when the code isn't present.
    private static int FirstAbilValue(JsonElement row, int code)
    {
        for (int i = 0; i < AbilSlots; i++)
        {
            if (ReadInt(row, $"Abil-{i}") == code)
                return ReadInt(row, $"AbilVal-{i}");
        }
        return 0;
    }

    private static JsonElement? FindByNumber(JsonDocument doc, int number)
    {
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (ReadInt(row, "Number") == number) return row;
        }
        return null;
    }

    private static int ReadInt(JsonElement row, string property)
        => row.TryGetProperty(property, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out int v)
            ? v
            : 0;

    private static string ReadString(JsonElement row, string property)
        => row.TryGetProperty(property, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;
}
