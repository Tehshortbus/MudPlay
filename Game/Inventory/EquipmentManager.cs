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
    // Resolves whether a weapon name is two-handed (Items.WeaponType 2H). Injected
    // so the actuator stays game-data-free; null ⇒ never two-handed (one-handed
    // off-hand behaviour, the safe default for tests).
    private readonly Func<string?, bool> _isTwoHanded;
    // Inventory-fallback resolvers (game-data-aware, injected to keep the actuator
    // game-data-free like _isTwoHanded). _resolveItemSlot maps a carried item name
    // to the physical EquipmentSlot it fills (null ⇒ not wearable gear);
    // _canEquipItem gates it against the live character's level / class / alignment.
    // Both null in tests / before game data is wired, which disables the fallback —
    // the manual apply paths then fall back to the set-only worn diff.
    private readonly Func<string, EquipmentSlot?>? _resolveItemSlot;
    private readonly Func<string, bool>? _canEquipItem;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();

    // True when the combat engine currently owns the weapon slot with a
    // per-monster alternate-weapon override. Wired post-construction (the combat
    // engine is built after this manager, so a ctor injection would be circular).
    // Auto-fire gear-set applies consult it to leave the weapon/off-hand to combat
    // rather than clobbering its swap — see ApplySet.
    private Func<bool>? _combatOwnsWeaponSlot;

    private readonly Queue<string> _pending = new();
    private DispatcherTimer? _applyTimer;
    private bool _isEquipping;

    public EquipmentManager(
        Func<EquipmentSettings> readEquipment,
        Func<InventorySnapshot> getSnapshot,
        Func<CombatSettings> readCombat,
        Action<CombatSettings> writeCombat,
        Func<string?, bool>? isTwoHanded = null,
        Func<string, EquipmentSlot?>? resolveItemSlot = null,
        Func<string, bool>? canEquipItem = null,
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
        _isTwoHanded = isTwoHanded ?? (static _ => false);
        _resolveItemSlot = resolveItemSlot;
        _canEquipItem = canEquipItem;
        _log = log;
    }

    // Bind the wire sink. Idempotent; later binds replace earlier ones.
    public void SetWireSender(Action<byte[]> send) => _wire.Bind(send);

    // Bind the combat weapon-ownership probe (CombatManager.IsWeaponOverrideActive).
    // Wired post-construction to break the manager ↔ combat-engine build cycle.
    public void SetCombatWeaponOwnershipProbe(Func<bool> probe) => _combatOwnsWeaponSlot = probe;

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
        // User-initiated gear-up: top empty / unowned slots up from carried gear.
        return ApplySet(set, fillFromInventory: true) ? EquipResult.Applied : EquipResult.NoChange;
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
        // Auto-fire (resting / combat triggers): apply the set as configured, no
        // inventory fallback — silently equipping unrelated carried gear on a
        // scheduled trigger would be surprising.
        return ApplySet(set, fillFromInventory: false) ? EquipResult.Applied : EquipResult.NoChange;
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
        // "Equip All" is a manual gear-up: top empty / unowned slots up from carried gear.
        return ApplySet(set, fillFromInventory: true) ? EquipResult.Applied : EquipResult.NoChange;
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

    // ----- immediate weapon swap (combat fast path) -----------------------

    // Equip weapon + off-hand NOW, bypassing the paced queue. A mid-combat
    // weapon flip must land before the next swing, so it can't sit behind — or
    // be declined by — a running full-loadout apply; it also doesn't set
    // _isEquipping, so the paced queue and this fast path stay independent.
    // Diffs against live worn gear (the single source of truth): a weapon
    // already in the Weapon Hand is skipped (a redundant `eq` draws "You do not
    // have X left unequipped."); a two-hander first `rem`s whatever occupies the
    // off-hand (the game refuses the wield with a hand full — the auto-trade
    // doesn't apply), while a one-hander equips its configured off-hand when
    // that isn't already worn. Empty weapon ⇒ no-op.
    public void SwapWeapon(string? weapon, string? offHand)
    {
        string? w = weapon?.Trim();
        if (string.IsNullOrEmpty(w)) return;

        InventorySnapshot snap = _getSnapshot();

        // Before the first 'i' dump the worn loadout is unknown. MajorMUD persists
        // equipment across logins, so whatever combat wants is already worn — a
        // speculative `eq` here only draws "You do not have X left unequipped."
        // (the already-on normal case) or, after a rare cleanup EP-zap, fails with
        // "You may not use that weapon." Defer to the diff below, which runs once
        // the dump lands and the real worn/held state is known.
        if (snap.LastUpdated == DateTimeOffset.MinValue) return;

        string? wornWeapon = SlotItem(snap, "Weapon Hand");
        string? wornOffHand = SlotItem(snap, "Off-Hand");
        bool twoHanded = _isTwoHanded(w);
        // Gate equips on what's actually in the pack: a weapon lost to a deathpile
        // can't be wielded, and blindly sending `eq` only draws "You do not have X
        // left unequipped." on every combat round.
        ISet<string>? held = HeldNames(snap);

        if (!string.Equals(w, wornWeapon, StringComparison.OrdinalIgnoreCase)
            && IsHeld(held, w))
        {
            if (twoHanded && !string.IsNullOrWhiteSpace(wornOffHand))
                _wire.Send($"rem {wornOffHand!.Trim()}");
            _log?.Info(LogCategory,
                $"swap weapon={w} offhand={(twoHanded ? "<two-handed>" : offHand ?? "<none>")}");
            _wire.Send($"eq {w}");
        }

        if (twoHanded) return;   // a two-hander fills both hands — no off-hand equip

        string? oh = offHand?.Trim();
        if (!string.IsNullOrEmpty(oh)
            && !string.Equals(oh, wornOffHand, StringComparison.OrdinalIgnoreCase)
            && IsHeld(held, oh))
            _wire.Send($"eq {oh}");
    }

    // The carried-but-unworn item names for an observed inventory — the pool a
    // wear / eq can actually draw from — or null when no 'i' dump has been parsed
    // yet (availability unknown, so callers don't gate). Only meaningful after a
    // dump; the carried list is patched live on pickup / drop thereafter.
    private static ISet<string>? HeldNames(InventorySnapshot snap) =>
        snap.LastUpdated == DateTimeOffset.MinValue
            ? null
            : new HashSet<string>(snap.CarriedItems, StringComparer.OrdinalIgnoreCase);

    // A named item can be equipped only if it's in the pack. Null availability
    // (no dump parsed) can't gate, so it's allowed through unchanged.
    private static bool IsHeld(ISet<string>? held, string name) =>
        held is null || held.Contains(name);

    private static string? SlotItem(InventorySnapshot snap, string slot)
    {
        foreach (EquippedItem e in snap.EquippedItems)
            if (string.Equals(e.Slot, slot, StringComparison.OrdinalIgnoreCase))
                return e.Name;
        return null;
    }

    // ----- Backstab-set armor (pre-move prep) -----------------------------

    // Apply the Backstab set's ARMOR as part of the pre-move approach sequence.
    // The combat engine calls this (via PrepBackstabForMove) right before the
    // sneak: equipping breaks sneak, so the armor MUST be sent before the sn —
    // it can't sit on the paced queue and trail into the move. The whole delta
    // is therefore sent as one synchronous burst (deltas only, so the burst is
    // usually a piece or two: the Backstab set overlaps the worn loadout).
    // Weapon + off-hand slots are excluded — the immediate weapon swap owns
    // those. No-op unless a Backstab set exists and is Enabled ("automation may
    // equip this set"); declines while a paced full-loadout apply is in flight.
    public EquipResult ApplyBackstabArmor()
    {
        if (_isEquipping) return EquipResult.Busy;
        EquipmentSet? set = _readEquipment().Sets
            .FirstOrDefault(s => s.Trigger == EquipTriggerType.Backstab);
        if (set is not { Enabled: true }) return EquipResult.NotFound;

        InventorySnapshot snap = _getSnapshot();
        var worn = new HashSet<string>(
            snap.EquippedItems.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        List<string> cmds = BuildWearCommands(set, worn, armorOnly: true, availableNames: HeldNames(snap));
        if (cmds.Count == 0) return EquipResult.NoChange;

        _log?.Info(LogCategory, $"backstab armor — {cmds.Count} piece(s)");
        foreach (string cmd in cmds) _wire.Send(cmd);
        return EquipResult.Applied;
    }

    // True when the apply produced a change — a wear sequence started or a virtual
    // slot wrote CombatSettings. False when the set is already fully in effect.
    // fillFromInventory lets the user-initiated paths top empty / unowned slots up
    // from carried gear (see BuildApplyCommands); auto-fires pass it false.
    private bool ApplySet(EquipmentSet set, bool fillFromInventory)
    {
        bool combatChanged = false;
        CombatSettings combat = _readCombat();
        if (ApplyVirtualSlots(set, combat))
        {
            _writeCombat(combat);
            combatChanged = true;
        }

        // Auto-fire applies (resting / combat triggers, fill=false) defer the
        // weapon + off-hand to the combat engine whenever it's mid-swap for a
        // monster. Otherwise the Default-set trigger that fires on combat-entry
        // re-wears the normal weapon and cancels combat's per-monster alternate
        // swap, which then re-swaps next round — the reported weapon flapping.
        // Manual gear-ups (Equip All / @equip, fill=true) carry explicit intent
        // and always control the weapon.
        bool deferWeaponToCombat = !fillFromInventory && _combatOwnsWeaponSlot?.Invoke() == true;

        List<string> cmds = BuildApplyCommands(set, _getSnapshot(), fillFromInventory, deferWeaponToCombat);

        if (cmds.Count == 0)
            return combatChanged;

        _log?.Info(LogCategory, $"applying gear set '{set.Name}' — {cmds.Count} command(s)");
        StartPacedSend(cmds);
        return true;
    }

    // Pick the command list for an apply: the inventory-aware plan when the caller
    // allows it, an 'i' dump has actually been parsed, and the game-data resolvers
    // are wired; otherwise the set-only worn diff. The set-only path is also what
    // every existing test exercises (their snapshots are never-observed, so
    // haveInventory is false) and what an auto-fire uses.
    private List<string> BuildApplyCommands(
        EquipmentSet set, InventorySnapshot snap, bool fillFromInventory, bool armorOnly = false)
    {
        bool haveInventory = snap.LastUpdated != DateTimeOffset.MinValue;
        if (fillFromInventory && haveInventory
            && _resolveItemSlot is not null && _canEquipItem is not null)
        {
            return BuildEquipCommands(
                set, snap.CarriedItems, snap.EquippedItems, _resolveItemSlot, _canEquipItem);
        }

        var worn = new HashSet<string>(
            snap.EquippedItems.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        // Gate the set-only diff on the pack once we've parsed an 'i' so an
        // auto-fire trigger doesn't flood failed wears for gear we no longer hold
        // (e.g. after a death dumped the whole loadout into a deathpile).
        return BuildWearCommands(set, worn, armorOnly: armorOnly,
            availableNames: haveInventory ? HeldNames(snap) : null);
    }

    // ----- pure apply logic (unit-tested directly) ------------------------

    // The ordered wear commands for a set's physical slots whose item isn't
    // already worn. Virtual slots are excluded (handled by ApplyVirtualSlots);
    // {no change} (empty item) slots are skipped; an already-worn item is
    // skipped so re-applying a set issues no redundant wears. The game
    // auto-removes whatever occupies a slot when the new item is worn, so no
    // explicit remove is needed for a full-loadout swap. armorOnly additionally
    // skips the held slots (Weapon / Off-Hand) — both the backstab auto-fire and
    // an auto-fire set applied mid-swap leave the weapon to the combat engine's
    // immediate per-monster swap.
    // availableNames, when non-null, is the set of items the character actually
    // holds (carried-but-unworn); an item that's neither worn nor in it is
    // skipped, since the wear would only draw "You do not have X left
    // unequipped." When null (no 'i' parsed, or a test), availability is unknown
    // and every not-worn set item is issued, preserving the pre-gate behaviour.
    internal static List<string> BuildWearCommands(
        EquipmentSet set, ISet<string> wornNames, bool armorOnly = false,
        ISet<string>? availableNames = null)
    {
        var cmds = new List<string>();
        foreach (EquipmentSlotEntry e in set.Slots)
        {
            if (IsVirtual(e.Slot)) continue;
            if (armorOnly && e.Slot is EquipmentSlot.Weapon or EquipmentSlot.OffHand) continue;
            string? name = e.ItemName?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (wornNames.Contains(name)) continue;
            if (availableNames is not null && !availableNames.Contains(name)) continue;
            cmds.Add($"wear {name}");
        }
        return cmds;
    }

    // Inventory-aware apply plan for the user-initiated equip paths (Equip All /
    // @equip-<set>). Honors the set's picks the character actually carries (or
    // already wears), then fills any slot the set left empty — or named an item
    // that isn't carried — from equippable carried gear, first-come-first-served.
    //
    // MajorMUD lets only one of each *named* item be worn, and the finger / wrist
    // families each hold two pieces; the plan respects both — distinct names only,
    // and never more per family than its physical slot count. Single slots aren't
    // capacity-gated: a wear trades places with whatever occupies the slot, so a
    // set's explicit pick replaces the worn piece there. resolveSlot returns null
    // for an item the realm can't wear (skipped); canEquip drops gear the live
    // character can't use (wrong class / level / alignment). Weapons take the
    // universal `eq` verb (wear is armor-only); everything worn takes `wear`.
    internal static List<string> BuildEquipCommands(
        EquipmentSet set,
        IReadOnlyList<string> carried,
        IReadOnlyList<EquippedItem> worn,
        Func<string, EquipmentSlot?> resolveSlot,
        Func<string, bool> canEquip)
    {
        var result = new List<string>();
        var wornNames = new HashSet<string>(
            worn.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        var carriedSet = new HashSet<string>(
            carried.Select(c => StripStackCount(c.Trim())), StringComparer.OrdinalIgnoreCase);
        // One of each named item across the whole plan (also blocks re-wearing worn).
        var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Per-family fill count, seeded from currently-worn occupancy so the
        // fallback only tops a family up to its remaining empty slots.
        var used = new Dictionary<EquipmentSlot, int>();
        foreach (EquippedItem e in worn)
            if (EquipmentSlotMap.FromWornString(e.Slot) is EquipmentSlot s)
                Bump(used, FamilyOf(s));

        // Set pass — the set's picks we actually have.
        foreach (EquipmentSlotEntry entry in set.Slots)
        {
            if (IsVirtual(entry.Slot)) continue;
            string? name = entry.ItemName?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (wornNames.Contains(name)) { chosen.Add(name); continue; }
            if (chosen.Contains(name)) continue;
            // Not carried ⇒ leave the slot for the fallback to fill from what we have.
            if (!carriedSet.Contains(name)) continue;
            result.Add($"{Verb(entry.Slot)} {name}");
            chosen.Add(name);
            Bump(used, FamilyOf(entry.Slot));
        }

        // Fallback pass — fill remaining empty slots, first-come-first-served.
        foreach (string rawName in carried)
        {
            string name = StripStackCount(rawName.Trim());
            if (name.Length == 0 || chosen.Contains(name) || wornNames.Contains(name)) continue;
            if (resolveSlot(name) is not EquipmentSlot slot || IsVirtual(slot)) continue;
            EquipmentSlot family = FamilyOf(slot);
            if (used.GetValueOrDefault(family) >= Capacity(family)) continue;
            if (!canEquip(name)) continue;
            result.Add($"{Verb(slot)} {name}");
            chosen.Add(name);
            Bump(used, family);
        }

        return result;
    }

    // The game lists a stack of identical items as "<count> <name>" (e.g.
    // "2 padded helm"); a singleton has no prefix. Strip the count so a stacked
    // carried token still matches its set entry and resolves to a slot —
    // otherwise equip-all skips every doubled-up piece. Currency tokens
    // ("86 gold crowns") never reach here: the inventory parser filters them
    // out before the carried list is built.
    private static string StripStackCount(string token)
    {
        int space = token.IndexOf(' ');
        if (space <= 0) return token;
        for (int i = 0; i < space; i++)
            if (!char.IsDigit(token[i])) return token;
        string rest = token[(space + 1)..];
        return rest.Length == 0 ? token : rest;
    }

    private static void Bump(Dictionary<EquipmentSlot, int> counts, EquipmentSlot family)
        => counts[family] = counts.GetValueOrDefault(family) + 1;

    // The paired finger / wrist slots collapse onto their slot-1 member so both
    // physical placements share one capacity budget; every other slot is its own
    // family.
    private static EquipmentSlot FamilyOf(EquipmentSlot slot) => slot switch
    {
        EquipmentSlot.Finger2 => EquipmentSlot.Finger1,
        EquipmentSlot.Wrist2 => EquipmentSlot.Wrist1,
        _ => slot,
    };

    // Physical slot count for a family — two for fingers / wrists, one otherwise.
    private static int Capacity(EquipmentSlot family) =>
        family is EquipmentSlot.Finger1 or EquipmentSlot.Wrist1 ? 2 : 1;

    // The equip verb: weapons take the universal `eq` (wear is armor-only per the
    // game's verb set); everything worn takes `wear`, matching the set-only diff.
    private static string Verb(EquipmentSlot slot) =>
        slot == EquipmentSlot.Weapon ? "eq" : "wear";

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
