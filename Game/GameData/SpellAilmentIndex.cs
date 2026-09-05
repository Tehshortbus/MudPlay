using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.GameData;

// Maps a Spells-table row to the ailment keyword(s) it applies, read from its
// ability codes (following the EndCast cast-chain, e.g. poison bolt -> poison
// bite). Mirrors the flag-derivation rule recorded in GAME_MECHANICS: a spell
// APPLIES an ailment when its effective codes include the ailment's code.
//
// Backs the Spells tab's ailment-keyword filter (type "poison" / "confuse" /
// "blind" / "hold" to surface every spell that applies it). Built lazily off the
// active set's raw Spells table and rebuilt when the active set changes.
public sealed class SpellAilmentIndex
{
    // ailment keyword -> the Abil code that applies it. "hold" is HoldPerson (74,
    // MovementPrevented); confuse/poison/blind mirror the Effects-flag mapping.
    public static readonly IReadOnlyDictionary<string, int> AilmentCodes =
        new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["poison"]  = 19,
            ["confuse"] = 71,
            ["blind"]   = 107,
            ["hold"]    = 74,
        };

    // Only EndCast (151) is followed — a damage spell that EndCasts the real DoT
    // (poison bolt -> poison bite). GiveTempSpell (160) GRANTS a castable spell to
    // the caster, so its code would confuse only when the player later casts it —
    // never followed (matches the flag-derivation script's decision).
    private const int EndCastCode = 151;

    private readonly GameDataCache _cache;
    private Dictionary<int, HashSet<string>>? _byNumber;
    private string? _builtForSet;

    public SpellAilmentIndex(GameDataCache cache)
    {
        System.ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    // True when the spell applies the named ailment (case-insensitive keyword).
    public bool Applies(int spellNumber, string ailment)
        => Map().TryGetValue(spellNumber, out HashSet<string>? set) && set.Contains(ailment);

    // The ailment keywords a spell applies (empty when none / unknown).
    public IReadOnlySet<string> AilmentsOf(int spellNumber)
        => Map().TryGetValue(spellNumber, out HashSet<string>? set)
            ? set
            : (IReadOnlySet<string>)System.Collections.Immutable.ImmutableHashSet<string>.Empty;

    private Dictionary<int, HashSet<string>> Map()
    {
        if (_byNumber is not null && _builtForSet == _cache.ActiveSet) return _byNumber;
        _byNumber = Build(_cache);
        _builtForSet = _cache.ActiveSet;
        return _byNumber;
    }

    private static Dictionary<int, HashSet<string>> Build(GameDataCache cache)
    {
        var result = new Dictionary<int, HashSet<string>>();
        JsonDocument? doc = cache.GetRawTable("Spells");
        if (doc is null) return result;

        // number -> (own codes, EndCast targets) — one pass, then resolve chains.
        var codes = new Dictionary<int, HashSet<int>>();
        var endcasts = new Dictionary<int, List<int>>();
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            int num = ReadInt(row, "Number");
            if (num <= 0) continue;
            var own = new HashSet<int>();
            var refs = new List<int>();
            for (int i = 0; i < 10; i++)
            {
                int c = ReadInt(row, $"Abil-{i}");
                if (c == 0) continue;
                own.Add(c);
                if (c == EndCastCode)
                {
                    int v = ReadInt(row, $"AbilVal-{i}");
                    if (v > 0) refs.Add(v);
                }
            }
            codes[num] = own;
            if (refs.Count > 0) endcasts[num] = refs;
        }

        foreach (int num in codes.Keys)
        {
            HashSet<int> effective = EffectiveCodes(num, codes, endcasts, new HashSet<int>(), 0);
            var tokens = AilmentTokens(effective);
            if (tokens.Count > 0) result[num] = tokens;
        }
        return result;
    }

    // Union of a spell's own codes plus every EndCast target's codes (bounded
    // depth, cycle-guarded).
    private static HashSet<int> EffectiveCodes(
        int num, Dictionary<int, HashSet<int>> codes, Dictionary<int, List<int>> endcasts,
        HashSet<int> seen, int depth)
    {
        var acc = new HashSet<int>();
        if (depth > 5 || !seen.Add(num) || !codes.TryGetValue(num, out HashSet<int>? own)) return acc;
        acc.UnionWith(own);
        if (endcasts.TryGetValue(num, out List<int>? refs))
            foreach (int r in refs)
                acc.UnionWith(EffectiveCodes(r, codes, endcasts, seen, depth + 1));
        return acc;
    }

    // Pure map from a set of ability codes to the ailment keywords they imply —
    // the testable core of the index.
    public static HashSet<string> AilmentTokens(IEnumerable<int> effectiveCodes)
    {
        var present = new HashSet<int>(effectiveCodes);
        var tokens = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach ((string keyword, int code) in AilmentCodes)
            if (present.Contains(code)) tokens.Add(keyword);
        return tokens;
    }

    private static int ReadInt(JsonElement row, string prop)
        => row.TryGetProperty(prop, out JsonElement e)
           && e.ValueKind == JsonValueKind.Number
           && e.TryGetInt32(out int n) ? n : 0;
}
