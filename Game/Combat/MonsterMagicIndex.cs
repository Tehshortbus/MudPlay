using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.Combat;

/// <summary>
/// Fast lookup of a monster's magic-hit requirement (<c>Magical</c>,
/// MajorMUD ability code 28) and spell immunity (<c>SpellImmu</c>, code
/// 139) by monster Number in the active game-data set.
/// </summary>
/// <remarks>
/// <para>
/// Both values gate combat eligibility deterministically from game data:
/// </para>
/// <list type="bullet">
/// <item><b>Magical N</b> — a physical weapon hits this monster only when
/// the weapon's <c>HitMagic</c> (<see cref="ItemMagicIndex"/>) is ≥ N.
/// A monster with no Magical ability is level 0, so any weapon hits.</item>
/// <item><b>SpellImmu N</b> — a spell affects this monster only when its
/// <c>ReqLevel</c> (<see cref="SpellReqLevelIndex"/>) is ≥ N. Applies to
/// both attack spells and debuffs. No SpellImmu = 0, so any spell is
/// allowed.</item>
/// </list>
/// <para>
/// Mirrors <see cref="SeeHiddenIndex"/>: the map is built lazily by
/// scanning the raw <c>Monsters</c> table's paired <c>Abil-0..9</c> /
/// <c>AbilVal-0..9</c> columns, cached, and dropped on game-data set
/// switch so the next query rebuilds against the new set. Unlike
/// SeeHidden we read the <c>AbilVal</c> too, because the level value (not
/// mere presence) is what gates the comparison.
/// </para>
/// </remarks>
public sealed class MonsterMagicIndex
{
    /// <summary>MajorMUD ability code for the magic-hit requirement
    /// (per <see cref="GameData.AbilityNames"/>).</summary>
    private const int MagicalAbilityCode = 28;

    /// <summary>MajorMUD ability code for spell immunity.</summary>
    private const int SpellImmuneAbilityCode = 139;

    /// <summary>Number of <c>Abil-N</c> slots on a Monsters row.</summary>
    private const int AbilitySlots = 10;

    private readonly GameDataCache _cache;
    private Dictionary<int, MonsterMagic>? _byNumber;

    public MonsterMagicIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => _byNumber = null;
    }

    /// <summary>The monster's magic-hit requirement (0 when it carries no
    /// <c>Magical</c> ability — any weapon hits).</summary>
    public int MagicalLevel(int monsterNumber) =>
        Build().TryGetValue(monsterNumber, out MonsterMagic m) ? m.Magical : 0;

    /// <summary>The monster's spell immunity (0 when it carries no
    /// <c>SpellImmu</c> ability — any spell is allowed).</summary>
    public int SpellImmunity(int monsterNumber) =>
        Build().TryGetValue(monsterNumber, out MonsterMagic m) ? m.SpellImmu : 0;

    private Dictionary<int, MonsterMagic> Build()
    {
        if (_byNumber is { } cached) return cached;

        Dictionary<int, MonsterMagic> map = new();
        JsonDocument? doc = _cache.GetRawTable("Monsters");
        if (doc is not null)
        {
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("Number", out JsonElement numEl)) continue;
                if (numEl.ValueKind != JsonValueKind.Number) continue;
                if (!numEl.TryGetInt32(out int number)) continue;

                int magical = 0;
                int spellImmu = 0;
                for (int i = 0; i < AbilitySlots; i++)
                {
                    if (!row.TryGetProperty($"Abil-{i}", out JsonElement abilEl)) continue;
                    if (abilEl.ValueKind != JsonValueKind.Number) continue;
                    if (!abilEl.TryGetInt32(out int code)) continue;
                    if (code != MagicalAbilityCode && code != SpellImmuneAbilityCode) continue;
                    if (!row.TryGetProperty($"AbilVal-{i}", out JsonElement valEl)) continue;
                    if (valEl.ValueKind != JsonValueKind.Number) continue;
                    if (!valEl.TryGetInt32(out int val)) continue;
                    if (code == MagicalAbilityCode) magical = val;
                    else spellImmu = val;
                }

                if (magical != 0 || spellImmu != 0)
                    map[number] = new MonsterMagic(magical, spellImmu);
            }
        }

        _byNumber = map;
        return map;
    }

    /// <summary>A monster's paired magic-hit requirement + spell immunity.</summary>
    private readonly record struct MonsterMagic(int Magical, int SpellImmu);
}
