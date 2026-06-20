using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Inventory;

/// <summary>
/// Applies saved gear sets (<see cref="EquipmentSet"/>) — the engine half of the
/// Workshop's Equipment tab. Given a set, it diffs the desired controlled-slot
/// items against the live worn loadout (<see cref="InventoryManager.Snapshot"/>)
/// and walks the character into that set: physical slots get spaced
/// <c>wear &lt;item&gt;</c> commands (the game auto-removes whatever occupied the
/// slot), while the two <i>virtual</i> slots (Alternate Weapon / Off-Hand) never
/// hit the wire — they write <see cref="CombatSettings.AlternateWeapon"/> /
/// <see cref="CombatSettings.AlternateOffHand"/> so the combat weapon-swap matrix
/// picks them up.
/// </summary>
/// <remarks>
/// <para>
/// Settings are read live each call through the injected delegates (the same
/// pattern the Phase-9 engines use), so a set edited in the UI or a profile swap
/// is reflected without re-subscription.
/// </para>
/// <para>
/// This PR ships the apply core + the <c>@equip-&lt;set&gt;</c> entry point.
/// Per-slot item filtering / "find best" (the Workshop's slot grid + find-items views)
/// and auto-equip trigger evaluation land in later Phase-10 PRs that consume
/// this engine.
/// </para>
/// </remarks>
public sealed class EquipmentManager
{
    /// <summary>LogService category — <c>[Equipment]</c> rows per apply.</summary>
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

    /// <summary>Bind the wire sink. Idempotent; later binds replace earlier ones.</summary>
    public void SetWireSender(Action<byte[]> send) => _wire.Bind(send);

    /// <summary>Every buffer the engine has pushed to the wire, for tests.</summary>
    internal IReadOnlyList<byte[]> LastSentForTests => _wire.LastSentForTests;

    // ----- @equip-<set> ---------------------------------------------------

    /// <summary>
    /// Resolve a gear set by <see cref="EquipmentSet.Keyword"/> (case-insensitive,
    /// set <see cref="EquipmentSet.Name"/> as a fallback) and apply it. Declines
    /// while an apply is already in flight.
    /// </summary>
    public EquipResult ApplyByKeyword(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return EquipResult.NotFound;
        if (_isEquipping) return EquipResult.Busy;
        EquipmentSet? set = FindSet(keyword.Trim());
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

    /// <summary>
    /// The ordered <c>wear</c> commands for a set's controlled, physical slots
    /// whose item isn't already worn. Virtual slots are excluded (handled by
    /// <see cref="ApplyVirtualSlots"/>); empty item names are skipped; an
    /// already-worn item is skipped so re-applying a set issues no redundant
    /// wears. The game auto-removes whatever occupies a slot when the new item is
    /// worn, so no explicit <c>remove</c> is needed for a full-loadout swap.
    /// </summary>
    internal static List<string> BuildWearCommands(EquipmentSet set, ISet<string> wornNames)
    {
        var cmds = new List<string>();
        foreach (EquipmentSlotEntry e in set.Slots)
        {
            if (!e.Controlled || IsVirtual(e.Slot)) continue;
            string? name = e.ItemName?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (wornNames.Contains(name)) continue;
            cmds.Add($"wear {name}");
        }
        return cmds;
    }

    /// <summary>
    /// Fold a set's controlled virtual-slot items into <paramref name="combat"/>
    /// (Alternate Weapon → <see cref="CombatSettings.AlternateWeapon"/>, Alternate
    /// Off-Hand → <see cref="CombatSettings.AlternateOffHand"/>) and report whether
    /// anything changed. An empty virtual item leaves the field untouched, per the
    /// <see cref="EquipmentSlotEntry"/> contract.
    /// </summary>
    internal static bool ApplyVirtualSlots(EquipmentSet set, CombatSettings combat)
    {
        bool changed = false;
        foreach (EquipmentSlotEntry e in set.Slots)
        {
            if (!e.Controlled || !IsVirtual(e.Slot)) continue;
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
