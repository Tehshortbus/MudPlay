using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.Combat;

// Fast lookup of which monster Numbers carry the SeeHidden ability (MajorMUD
// ability code 57) in the active game-data set.
//
// A monster with SeeHidden defeats sneak: walking into its room with a sneaking
// character reveals you to EVERY occupant, so the opening backstab is ruined.
// CombatManager consults this to skip the backstab (and fall through to a normal
// attack) when any monster in the room has the flag. The set is built lazily by
// scanning the raw Monsters table's Abil-0..9 columns, cached, and dropped on
// game-data set switch so the next query rebuilds against the new set.
public sealed class SeeHiddenIndex
{
    // MajorMUD ability code for SeeHidden (per GameData.AbilityNames).
    private const int SeeHiddenAbilityCode = 57;

    // Number of Abil-N slots on a Monsters row.
    private const int AbilitySlots = 10;

    private readonly GameDataCache _cache;
    private HashSet<int>? _numbers;

    public SeeHiddenIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => _numbers = null;
    }

    // True when the monster with the given Number carries the SeeHidden ability
    // in the active set.
    public bool Has(int monsterNumber) => Build().Contains(monsterNumber);

    private HashSet<int> Build()
    {
        if (_numbers is { } cached) return cached;

        HashSet<int> set = new();
        JsonDocument? doc = _cache.GetRawTable("Monsters");
        if (doc is not null)
        {
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("Number", out JsonElement numEl)) continue;
                if (numEl.ValueKind != JsonValueKind.Number) continue;
                if (!numEl.TryGetInt32(out int number)) continue;
                for (int i = 0; i < AbilitySlots; i++)
                {
                    if (!row.TryGetProperty($"Abil-{i}", out JsonElement abilEl)) continue;
                    if (abilEl.ValueKind != JsonValueKind.Number) continue;
                    if (abilEl.TryGetInt32(out int code) && code == SeeHiddenAbilityCode)
                    {
                        set.Add(number);
                        break;
                    }
                }
            }
        }

        // Folded into the HashSet — release the pinned raw Monsters
        // JsonDocument (GetRawTable re-parses it if another index asks).
        _cache.EvictTable("Monsters");
        _numbers = set;
        return set;
    }
}
