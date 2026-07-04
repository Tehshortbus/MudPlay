using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.Combat;

/// <summary>
/// Fast lookup of a spell's <c>ReqLevel</c> (its learn-level requirement) by
/// the spell's <c>Short</c> cast-code in the active game-data set.
/// </summary>
/// <remarks>
/// <para>
/// Spell immunity gates by level: a spell affects a monster only when its
/// <c>ReqLevel</c> is ≥ the monster's <c>SpellImmu</c>
/// (<see cref="MonsterMagicIndex"/>). This applies to both attack spells and
/// debuffs.
/// </para>
/// <para>
/// The key is the <c>Short</c> cast-code because the combat spell slots
/// (<see cref="Models.Profile.CombatSpellSlot.SpellName"/>) store the
/// cast-code that gets typed to the server, not the spell's display Name —
/// see <see cref="Spells.CastCoordinator.TryCast"/>. <c>ReqLevel</c> is a
/// top-level integer column on each Spells row.
/// </para>
/// <para>
/// Mirrors <see cref="SeeHiddenIndex"/> / <see cref="MonsterMagicIndex"/>:
/// built lazily by scanning the raw <c>Spells</c> table, cached, and dropped
/// on game-data set switch. Unknown cast-code → <c>-1</c> (fail-open: the
/// caller treats "no data" as "don't block the configured spell").
/// </para>
/// </remarks>
public sealed class SpellReqLevelIndex
{
    private readonly GameDataCache _cache;
    private Dictionary<string, int>? _byShort;

    public SpellReqLevelIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => _byShort = null;
    }

    /// <summary>The spell's <c>ReqLevel</c>, or <c>-1</c> when the cast-code
    /// is unknown (no Spells row with that <c>Short</c>) — fail-open.</summary>
    public int ReqLevel(string? castCode)
    {
        if (string.IsNullOrWhiteSpace(castCode)) return -1;
        return Build().TryGetValue(castCode.Trim(), out int level) ? level : -1;
    }

    private Dictionary<string, int> Build()
    {
        if (_byShort is { } cached) return cached;

        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is not null)
        {
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("Short", out JsonElement shortEl)) continue;
                if (shortEl.ValueKind != JsonValueKind.String) continue;
                string? code = shortEl.GetString();
                if (string.IsNullOrWhiteSpace(code)) continue;

                int reqLevel = 0;
                if (row.TryGetProperty("ReqLevel", out JsonElement lvlEl)
                    && lvlEl.ValueKind == JsonValueKind.Number
                    && lvlEl.TryGetInt32(out int parsed))
                    reqLevel = parsed;

                // Last writer wins on duplicate cast-codes (rare in game data).
                map[code.Trim()] = reqLevel;
            }
        }

        // Folded into the map — release the pinned raw Spells JsonDocument.
        _cache.EvictTable("Spells");
        _byShort = map;
        return map;
    }
}
