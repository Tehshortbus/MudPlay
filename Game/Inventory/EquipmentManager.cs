using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Inventory;

// Applies saved gear sets (EquipmentSet) — the engine half of the Workshop's
// Equipment tab. Given a set, it diffs the desired controlled-slot items against
// the live worn loadout (InventoryManager.Snapshot) and walks the character into
// that set: physical slots get spaced "wear <item>" commands (the game
// auto-removes whatever occupied the slot), while the two virtual slots
// (Alternate Weapon / Off-Hand) never hit the wire — they write
// CombatSettings.AlternateWeapon / AlternateOffHand so the combat weapon-swap
// matrix picks them up.
//
// Settings are read live each call through the injected delegates, so a set
// edited in the UI or a profile swap is reflected without re-subscription.
public sealed class EquipmentManager
{
    // LogService category — [Equipment] rows per apply.
    public const string LogCategory = "Equipment";

    // Spacing between successive wear commands. The game's flood / spam guards
    // dislike a burst of ~20 wears, so drain the queue one step at a time.
    private static readonly TimeSpan ApplyStep = TimeSpan.FromMilliseconds(200);

    private readonly Func<EquipmentSettings> _readEquipment;
    private readonly Func<InventorySnapshot> _getSnapshot;
    private readonly Func<CombatSettings> _readCombat;
    private readonly Action<CombatSettings> _writeCombat;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();

    private readonly Queue<string> _pending = new();
    private DispatcherTimer? _applyTimer;
    private bool _isEquipping;

    public EquipmentManager(
        Func<EquipmentSettings> readEquipment,
        Func<InventorySnapshot> getSnapshot,
        Func<CombatSettings> readCombat,
        Action<CombatSettings> writeCombat,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(readEquipment);
        ArgumentNullException.ThrowIfNull(getSnapshot);
        ArgumentNullException.ThrowIfNull(readCombat);
        ArgumentNullException.ThrowIfNull(writeCombat);
        _readEquipment = readEquipment;
        _getSnapshot = getSnapshot;
        _readCombat = readCombat;
        _writeCombat = writeCombat;
        _log = log;
    }

    // Bind the wire sink. Idempotent; later binds replace earlier ones.
    public void SetWireSender(Action<byte[]> send) => _wire.Bind(send);

    // Every buffer the engine has pushed to the wire, for tests.
    internal IReadOnlyList<byte[]> LastSentForTests => _wire.LastSentForTests;

    // ----- @equip-<set> ---------------------------------------------------

    // Resolve a gear set by EquipmentSet.Keyword (case-insensitive, the set's
    // Name as a fallback) and apply it. Declines while an apply is already in
    // flight.
    public EquipResult ApplyByKeyword(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return EquipResult.NotFound;
        if (_isEquipping) return EquipResult.Busy;
        EquipmentSet? set = FindSet(keyword.Trim());
        if (set is null) return EquipResult.NotFound;
        return ApplySet(set) ? EquipResult.Applied : EquipResult.NoChange;
    }

    // Resolve a gear set by its stable EquipmentSet.Id and apply it. Auto-equip
    // triggers reference their target set by Id (it survives renames), so this
    // is the trigger coordinator's entry point. Declines while an apply is
    // already in flight; reports NotFound for an empty or unresolved id (e.g. a
    // trigger pointing at a since-deleted set).
    public EquipResult ApplyBySetId(string setId)
    {
        if (string.IsNullOrWhiteSpace(setId)) return EquipResult.NotFound;
        if (_isEquipping) return EquipResult.Busy;
        EquipmentSet? set = _readEquipment().Sets
            .FirstOrDefault(s => string.Equals(s.Id, setId, StringComparison.Ordinal));
        if (set is null) return EquipResult.NotFound;
        return ApplySet(set) ? EquipResult.Applied : EquipResult.NoChange;
    }

    // Resolve the gear set whose Trigger matches and apply it. The local
    // Action-menu / toolbar "Equip All" drives this with Default — the baseline
    // loadout. Declines while an apply is in flight; NotFound when no set is
    // configured for the trigger.
    public EquipResult ApplyByTrigger(EquipTriggerType trigger)
    {
        if (_isEquipping) return EquipResult.Busy;
        EquipmentSet? set = _readEquipment().Sets
            .FirstOrDefault(s => s.Trigger == trigger);
        if (set is null) return EquipResult.NotFound;
        return ApplySet(set) ? EquipResult.Applied : EquipResult.NoChange;
    }

    private EquipmentSet? FindSet(string keyword)
    {
        EquipmentSettings cfg = _readEquipment();
        // Keyword is the @equip-<set> suffix contract; fall back to the set's
        // display name so a caller can type either.
        foreach (EquipmentSet s in cfg.Sets)
            if (!string.IsNullOrEmpty(s.Keyword)
                && string.Equals(s.Keyword, keyword, StringComparison.OrdinalIgnoreCase))
                return s;
        foreach (EquipmentSet s in cfg.Sets)
            if (string.Equals(s.Name, keyword, StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }

    // True when the apply produced a change — a wear sequence started or a virtual
    // slot wrote CombatSettings. False when the set is already fully in effect.
    private bool ApplySet(EquipmentSet set)
    {
        bool combatChanged = false;
        CombatSettings combat = _readCombat();
        if (ApplyVirtualSlots(set, combat))
        {
            _writeCombat(combat);
            combatChanged = true;
        }

        InventorySnapshot snap = _getSnapshot();
        var worn = new HashSet<string>(
            snap.EquippedItems.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        List<string> cmds = BuildWearCommands(set, worn);

        if (cmds.Count == 0)
            return combatChanged;

        _log?.Info(LogCategory, $"applying gear set '{set.Name}' — {cmds.Count} command(s)");
        StartPacedSend(cmds);
        return true;
    }

    // ----- pure apply logic (unit-tested directly) ------------------------

    // The ordered wear commands for a set's physical slots whose item isn't
    // already worn. Virtual slots are excluded (handled by ApplyVirtualSlots);
    // {no change} (empty item) slots are skipped; an already-worn item is
    // skipped so re-applying a set issues no redundant wears. The game
    // auto-removes whatever occupies a slot when the new item is worn, so no
    // explicit remove is needed for a full-loadout swap.
    internal static List<string> BuildWearCommands(EquipmentSet set, ISet<string> wornNames)
    {
        var cmds = new List<string>();
        foreach (EquipmentSlotEntry e in set.Slots)
        {
            if (IsVirtual(e.Slot)) continue;
            string? name = e.ItemName?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (wornNames.Contains(name)) continue;
            cmds.Add($"wear {name}");
        }
        return cmds;
    }

    // Fold a set's virtual-slot items into combat (Alternate Weapon →
    // CombatSettings.AlternateWeapon, Alternate Off-Hand → AlternateOffHand) and
    // report whether anything changed. An empty virtual item leaves the field
    // untouched, per the EquipmentSlotEntry contract.
    internal static bool ApplyVirtualSlots(EquipmentSet set, CombatSettings combat)
    {
        bool changed = false;
        foreach (EquipmentSlotEntry e in set.Slots)
        {
            if (!IsVirtual(e.Slot)) continue;
            string? name = e.ItemName?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            switch (e.Slot)
            {
                case EquipmentSlot.AlternateWeapon:
                    if (!string.Equals(combat.AlternateWeapon, name, StringComparison.Ordinal))
                    {
                        combat.AlternateWeapon = name;
                        changed = true;
                    }
                    break;
                case EquipmentSlot.AlternateOffHand:
                    if (!string.Equals(combat.AlternateOffHand, name, StringComparison.Ordinal))
                    {
                        combat.AlternateOffHand = name;
                        changed = true;
                    }
                    break;
            }
        }
        return changed;
    }

    private static bool IsVirtual(EquipmentSlot slot) =>
        slot is EquipmentSlot.AlternateWeapon or EquipmentSlot.AlternateOffHand;

    // ----- paced send (UI plumbing — DispatcherTimer not pumped in tests) -

    private void StartPacedSend(IEnumerable<string> cmds)
    {
        StopTimer();
        _pending.Clear();
        foreach (string c in cmds) _pending.Enqueue(c);
        if (_pending.Count == 0) return;
        _isEquipping = true;
        _applyTimer = new DispatcherTimer(ApplyStep, DispatcherPriority.Background,
            (_, _) => SendNext());
        _applyTimer.Start();
    }

    private void SendNext()
    {
        if (_pending.Count == 0)
        {
            FinishEquip();
            return;
        }
        _wire.Send(_pending.Dequeue());
    }

    private void FinishEquip()
    {
        StopTimer();
        _isEquipping = false;
    }

    private void StopTimer()
    {
        _applyTimer?.Stop();
        _applyTimer = null;
    }
}
