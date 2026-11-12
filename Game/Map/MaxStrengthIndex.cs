using System.Text.Json;
using MudPlay.Game.GameData;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Per-set ceiling for "what is the highest Strength any build can reach on
// this game-data set?" — the number the door FSM (DoorPolicy) uses to
// decide whether a strength-gated door is bashable by anyone, replacing
// the old hardcoded 200.
//
// The ceiling is maxRacialStrength + bestGearBonus + heldBonus:
//   - the strongest race's trainable Strength cap (Races.xSTR, plus any
//     innate Strength ability — 0 in stock data, folded for completeness);
//   - the best per-slot +Strength gear (item ability code 46) obtainable
//     by any one class — each wear slot contributes its single strongest
//     wearable piece, and the two-ring / two-wrist slots contribute their
//     best piece twice; and
//   - the best +Strength held item per Unique-Pool (item ability code
//     188) — a "held" item (ParaMUD's floating spheres) grants its stats
//     just by being carried, ungated by class or StrReq, but only one item
//     may be held per pool, so a pool contributes its single strongest
//     +Strength piece and distinct pools stack.
//
// Race and class optimise independently: item eligibility (ItemEquipFilter)
// gates on class / weapon / armour type but never race, so the maximum is
// the strongest race paired with whichever class unlocks the richest
// +Strength gear — a "highest-Strength race × every class × best item per
// slot" walk. Level and alignment filters are switched off (a ceiling
// weighs every band), and a candidate item is only admitted when its
// StrReq is one the strongest race can train to. The estimate is
// deliberately generous: a door wrongly ruled unbashable would hide a real
// path, whereas an over-high ceiling is still gated by the live player's
// own Strength before any bash is attempted.
//
// Mirrors LightItemIndex — computed lazily, memoised, and dropped on a
// game-data set switch so the next read rebuilds against the new set.
// Falls back to DoorPolicy.UnbashableStrengthThreshold when the active set
// lacks the Races / Classes / Items tables the walk needs.
public sealed class MaxStrengthIndex
{
    // Race / item ability code for a flat +Strength bonus (AbilityNames 46).
    private const int StrengthAbilityCode = 46;

    // Ability slots on a Races row (Abil-0..9).
    private const int RaceAbilitySlots = 10;

    // Ability slots on an Items row (Abil-0..19).
    private const int ItemAbilitySlots = 20;

    private readonly GameDataCache _cache;
    private int? _max;

    public MaxStrengthIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => _max = null;
    }

    // The highest Strength any race + class + gear build can reach on the
    // active set. Built on first read, memoised until the set changes.
    // Falls back to DoorPolicy.UnbashableStrengthThreshold when the active
    // set is missing the tables the walk needs.
    public int MaxAchievableStrength => _max ??= Compute();

    private int Compute()
    {
        GameTable? races = _cache.GetTable("Races");
        GameTable? classes = _cache.GetTable("Classes");
        GameTable? items = _cache.GetTable("Items");
        if (races is null || classes is null || items is null)
            return DoorPolicy.UnbashableStrengthThreshold;

        int baseStrength = MaxRacialStrength(races);
        if (baseStrength <= 0) return DoorPolicy.UnbashableStrengthThreshold;

        // Split +Strength items into worn/wielded gear and "held" Unique-Pool items.
        //  - Gear: slotted, per-class equip-gated below; an item whose StrReq exceeds what
        //    the strongest race can train to is unreachable, so it never counts.
        //  - Held: a Unique-Pool (code 188) item grants its stats just by being carried
        //    (ParaMUD's floating spheres), ungated by class / StrReq. Only one item may be
        //    held per pool, so a pool contributes its single strongest +Strength piece;
        //    distinct pools stack.
        List<(GameRow Row, int Bonus, EquipmentSlot Slot)> candidates = new();
        Dictionary<int, int> bestPerPool = new();
        foreach (GameRow row in items.Rows)
        {
            int bonus = StrengthBonus(row, ItemAbilitySlots);

            if (AbilityNames.TryUniquePool(row, out int pool, ItemAbilitySlots))
            {
                if (!bestPerPool.TryGetValue(pool, out int best) || bonus > best)
                    bestPerPool[pool] = bonus;
                continue;   // never also counted as worn gear
            }

            if (bonus <= 0) continue;
            if (ReadInt(row, "StrReq") > baseStrength) continue;
            if (EquipmentSlotMap.SlotForItem(row) is not { } slot) continue;
            candidates.Add((row, bonus, slot));
        }

        int bestGear = 0;
        foreach (GameRow classRow in classes.Rows)
        {
            string? name = ReadString(classRow, "Name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            ClassEquipProfile cp = ItemEquipFilter.ResolveClassProfile(_cache, name);
            bestGear = Math.Max(bestGear, GearBonusForClass(candidates, cp));
        }

        int heldBonus = 0;
        foreach (int b in bestPerPool.Values) heldBonus += b;

        return baseStrength + bestGear + heldBonus;
    }

    // Highest trainable Strength across races: Races.xSTR plus any innate +Strength.
    private static int MaxRacialStrength(GameTable races)
    {
        int best = 0;
        foreach (GameRow race in races.Rows)
        {
            int str = ReadInt(race, "xSTR") + StrengthBonus(race, RaceAbilitySlots);
            if (str > best) best = str;
        }
        return best;
    }

    // Sum of the strongest wearable +Strength item per slot for one class; the doubled
    // Finger / Wrist slots count their best piece twice.
    private static int GearBonusForClass(
        IReadOnlyList<(GameRow Row, int Bonus, EquipmentSlot Slot)> candidates,
        ClassEquipProfile cp)
    {
        Dictionary<EquipmentSlot, int> bestPerSlot = new();
        foreach ((GameRow row, int bonus, EquipmentSlot slot) in candidates)
        {
            // level 0 / null alignment ⇒ only class + weapon/armour-type gating applies.
            if (!ItemEquipFilter.CanEquip(row, level: 0, cp, alignment: null)) continue;
            if (!bestPerSlot.TryGetValue(slot, out int cur) || bonus > cur)
                bestPerSlot[slot] = bonus;
        }

        int total = 0;
        foreach ((EquipmentSlot slot, int bonus) in bestPerSlot)
            total += bonus * PhysicalSlots(slot);
        return total;
    }

    // SlotForItem folds the two-ring / two-wrist worn codes onto their slot-1 primary,
    // so that primary stands in for both physical slots.
    private static int PhysicalSlots(EquipmentSlot slot) =>
        slot is EquipmentSlot.Finger1 or EquipmentSlot.Wrist1 ? 2 : 1;

    private static int StrengthBonus(GameRow row, int slots)
    {
        int sum = 0;
        for (int i = 0; i < slots; i++)
        {
            if (ReadInt(row, $"Abil-{i}") != StrengthAbilityCode) continue;
            sum += ReadInt(row, $"AbilVal-{i}");
        }
        return sum;
    }

    private static int ReadInt(GameRow row, string property)
        => row.TryGetProperty(property, out GameValue el)
           && el.ValueKind == JsonValueKind.Number
           && el.TryGetInt32(out int v) ? v : 0;

    private static string? ReadString(GameRow row, string property)
        => row.TryGetProperty(property, out GameValue el)
           && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}
