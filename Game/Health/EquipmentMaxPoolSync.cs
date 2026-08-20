using System;
using System.Collections.Generic;
using MudPlay.Game.Inventory;

namespace MudPlay.Game.Health;

// Keeps PlayerState.MaxHp / MaxMa in step with worn gear that grants a flat
// pool bonus (Items.Abil 88 = +Max HP, Abil 69 = +Max Mana — e.g. the
// severed head of Goru-Nezar's +50 mana). PromptParser's prompt-driven
// high-water mark and its periodic stat-screen resync (ApplyStatScreenMax)
// both assume the ceiling only moves via level-up or a manual stat check —
// neither reacts to an equip/remove that changes the pool mid-session, so a
// character wearing a +50-mana item read a stale max the moment it came off
// (report paradigm-20260820: max mana read 234 worn / 184 bare, but the
// rest-trigger and "pool is full" checks kept using whichever figure the
// ratchet had last learned).
//
// Tracks the worn set's aggregate flat-bonus total and, whenever it changes,
// applies the DELTA — not an absolute value — so it composes with whatever
// base the ratchet/stat-screen already established instead of overriding it.
// The first observation each "session" (since construction or the last
// Reset) only seeds the baseline; no delta fires then, since that base
// already reflects whatever is currently worn.
public sealed class EquipmentMaxPoolSync
{
    private readonly Func<IReadOnlyList<EquippedItem>, (int Hp, int Ma)> _equipmentMax;
    private readonly Action<int, int> _applyDelta;

    private bool _seeded;
    private int _lastHp;
    private int _lastMa;

    public EquipmentMaxPoolSync(
        Func<IReadOnlyList<EquippedItem>, (int Hp, int Ma)> equipmentMax,
        Action<int, int> applyDelta)
    {
        ArgumentNullException.ThrowIfNull(equipmentMax);
        ArgumentNullException.ThrowIfNull(applyDelta);
        _equipmentMax = equipmentMax;
        _applyDelta = applyDelta;
    }

    // Drop the remembered baseline — call on profile load / active game-data
    // set change, so the next worn-set observation reseeds instead of
    // diffing against a stale character's totals.
    public void Reset() => _seeded = false;

    // Call whenever the worn set may have changed (InventoryManager.Changed),
    // gated by the caller on InventoryManager.IsLoaded — an unloaded snapshot
    // has nothing meaningful to baseline.
    public void OnEquippedItemsChanged(IReadOnlyList<EquippedItem> equipped)
    {
        (int hp, int ma) = _equipmentMax(equipped);
        if (!_seeded)
        {
            _lastHp = hp;
            _lastMa = ma;
            _seeded = true;
            return;
        }

        int hpDelta = hp - _lastHp;
        int maDelta = ma - _lastMa;
        if (hpDelta == 0 && maDelta == 0) return;

        _lastHp = hp;
        _lastMa = ma;
        _applyDelta(hpDelta, maDelta);
    }
}
