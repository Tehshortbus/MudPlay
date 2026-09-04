using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Inventory;

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
    // Wearability-restriction probe for the block feature: true when the item
    // EXISTS in game data but the live character can't wear it (alignment /
    // level / class). Distinct from _canEquipItem, which also returns false for
    // an unknown item — an unknown name must NOT be block-flagged (it just isn't
    // queued). Null in tests / before game data is wired disables the block
    // machinery entirely.
    private readonly Func<string, bool>? _isEquipRestricted;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();

    // True when the combat engine currently owns the weapon slot with a
    // per-monster alternate-weapon override. Wired post-construction (the combat
    // engine is built after this manager, so a ctor injection would be circular).
    // Auto-fire gear-set applies consult it to leave the weapon/off-hand to combat
    // rather than clobbering its swap — see ApplySet.
    private Func<bool>? _combatOwnsWeaponSlot;

    // True on Paradigm (where a full-pair eq/wear evicts SLOT 1), false on Stock
    // (evicts SLOT 2) — see ComposePairedSlotCommands. Null before wiring / in tests
    // ⇒ Paradigm behaviour (the historical default the set-only tests exercise).
    private Func<bool>? _paradigmPairedEviction;

    // Thrash guard: a gear set that keeps producing commands without converging (a
    // paired-slot swap the game won't satisfy, say) must not re-apply forever. Count
    // applies-that-produced-commands for the SAME set within a window; past the limit,
    // hold off and warn instead of flooding wear/rem. A converged set produces no
    // commands so it never counts, and the hold self-releases once the window clears.
    private const int ThrashApplyLimit = 6;
    private static readonly TimeSpan ThrashWindow = TimeSpan.FromSeconds(8);
    private string? _thrashSetId;
    private readonly Queue<DateTimeOffset> _thrashApplies = new();
    private bool _thrashHolding;
    private bool _isEquipping;

    // ----- unwearable-slot blocks ----------------------------------------
    // A (set, slot) the live character can't wear the configured item in — either
    // detected up front (alignment / level / class, ServerConfirmed=false) or
    // confirmed by the game refusing the wear/eq (ServerConfirmed=true). Blocked
    // slots are skipped when building commands, so a swap never re-bonks a piece
    // we already know we can't wear (e.g. after an alignment drift EP-zap). In
    // memory only — self-heals: a fresh session re-detects on the next apply.
    private readonly record struct BlockInfo(string Name, bool ServerConfirmed);
    private readonly Dictionary<(string SetId, EquipmentSlot Slot), BlockInfo> _blocked = new();
    // Silent-seed guard: the first block evaluation after a profile load colours
    // the tab without a terminal notice (a saved set's already-unwearable items
    // aren't news); later additions (a drift, an add-time pick, a refusal) do
    // announce. Reset by ResetBlocks on profile load.
    private bool _blocksSeeded;

    // A gear-set block surfaced to the user — bridged to a terminal notice.
    public readonly record struct EquipBlock(string SetId, EquipmentSlot Slot, string ItemName);

    // Fires whenever the blocked-slot set changes — the Equipment tab recolours.
    public event Action? BlocksChanged;
    // Fires when a NEW block is surfaced (announce path only) — the terminal
    // notice bridge. Not fired on the silent post-load seed.
    public event Action<EquipBlock>? SlotBlockedAnnounced;

    // Ordered wear/eq attempts from the in-flight / last apply so an anonymous
    // refusal line ("You may not wear that item!" / "You may not use that
    // weapon.") can be attributed to the specific slot+item it concerns.
    // Successful wears are removed as their confirmation line arrives; the oldest
    // remaining attempt of the matching kind is the one the refusal blocks.
    private readonly record struct PendingEquip(string SetId, EquipmentSlot Slot, string ItemName);
    private readonly List<PendingEquip> _pending = new();
    private DateTimeOffset _pendingStamp;
    private static readonly TimeSpan PendingWindow = TimeSpan.FromSeconds(6);

    // True while a gear-set apply is streaming its `wear`/`rem` commands.
    // HealthManager reads this to hold its rest re-issue during a swap: each `wear`
    // stands the character, and without this the rest engine re-sends `rest` between
    // every command — a rest/stand thrash for the whole burst (report
    // paradigm-20260825-103537). The swap finishes, then rest resumes once.
    public bool IsApplyingSet => _isEquipping;

    // Fires true when a gear-set apply starts streaming its `wear`/`rem` commands and
    // false when it finishes. Wired to a MovementCoordinator gear-swap gate so the
    // loop holds in-room until the swap completes (report paradigm-20260826-140341).
    public event Action<bool>? ApplyingChanged;

    // The id of the last gear set the engine applied (or confirmed already worn) —
    // the "last set applied" the Workshop surfaces as Currently Equipped. Null until
    // the first apply this session; a stale id (after a profile swap) simply resolves
    // to no set name at the display side. CurrentSetChanged fires when it changes.
    public string? CurrentSetId { get; private set; }
    public event Action? CurrentSetChanged;

    public EquipmentManager(
        Func<EquipmentSettings> readEquipment,
        Func<InventorySnapshot> getSnapshot,
        Func<CombatSettings> readCombat,
        Action<CombatSettings> writeCombat,
        Func<string?, bool>? isTwoHanded = null,
        Func<string, EquipmentSlot?>? resolveItemSlot = null,
        Func<string, bool>? canEquipItem = null,
        Func<string, bool>? restrictsEquip = null,
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
        _isEquipRestricted = restrictsEquip;
        _log = log;
    }

    // Bind the wire sink. Idempotent; later binds replace earlier ones.
    public void SetWireSender(Action<byte[]> send) => _wire.Bind(send);

    // Bind the realm probe (true on Paradigm) so paired-slot swaps pick the right
    // evicted slot. Read live per-apply, so a mid-session set swap is honoured.
    public void SetRealmProbe(Func<bool> onParadigm) => _paradigmPairedEviction = onParadigm;

    private bool ParadigmPairedEviction => _paradigmPairedEviction?.Invoke() ?? true;

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
    // force: send the `eq` unconditionally, bypassing the worn-diff + carried-pack
    // gates. Used for a combat-critical swap (recovering from a weapon-no-effect
    // that the engine believed was already on the alternate): a stale/desynced
    // snapshot is precisely why the alternate isn't physically on the hand, so the
    // gates would wrongly suppress the swap. Normal (unforced) callers keep the
    // snapshot diff.
    public void SwapWeapon(string? weapon, string? offHand, bool force = false)
    {
        string? w = weapon?.Trim();
        if (string.IsNullOrEmpty(w)) return;

        InventorySnapshot snap = _getSnapshot();

        // Before the first 'i' dump the worn loadout is unknown. MajorMUD persists
        // equipment across logins, so whatever combat wants is already worn — a
        // speculative `eq` here only draws "You do not have X left unequipped."
        // (the already-on normal case) or, after a rare cleanup EP-zap, fails with
        // "You may not use that weapon." Defer to the diff below, which runs once
        // the dump lands and the real worn/held state is known. A forced swap can't
        // defer — it must recover now — so it sends regardless.
        if (!force && snap.LastUpdated == DateTimeOffset.MinValue) return;

        // This combat weapon path sends wear/eq too, but isn't a set apply — drop
        // any set-apply pending so a refusal it draws can't be misattributed back
        // to a set slot (which would wrongly block a wearable set item).
        _pending.Clear();

        string? wornWeapon = SlotItem(snap, "Weapon Hand");
        string? wornOffHand = SlotItem(snap, "Off-Hand");
        bool twoHanded = _isTwoHanded(w);
        // Gate equips on what's actually in the pack: a weapon lost to a deathpile
        // can't be wielded, and blindly sending `eq` only draws "You do not have X
        // left unequipped." on every combat round. (Bypassed on force.)
        ISet<string>? held = HeldNames(snap);

        if (force
            || (!string.Equals(w, wornWeapon, StringComparison.OrdinalIgnoreCase) && IsHeld(held, w)))
        {
            if (twoHanded && !string.IsNullOrWhiteSpace(wornOffHand))
                _wire.Send($"rem {wornOffHand!.Trim()}");
            _log?.Info(LogCategory,
                $"swap weapon={w} offhand={(twoHanded ? "<two-handed>" : offHand ?? "<none>")}{(force ? " (forced)" : "")}");
            _wire.Send($"eq {w}");
        }

        if (twoHanded) return;   // a two-hander fills both hands — no off-hand equip

        string? oh = offHand?.Trim();
        if (!string.IsNullOrEmpty(oh)
            && (force
                || (!string.Equals(oh, wornOffHand, StringComparison.OrdinalIgnoreCase) && IsHeld(held, oh))))
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
        List<string> cmds = BuildWearCommands(set, worn, armorOnly: true,
            availableNames: HeldNames(snap), blockedNames: BlockedNamesForSet(set));
        if (cmds.Count == 0) return EquipResult.NoChange;

        // Not tracked as set-apply attempts (this is a synchronous pre-sneak
        // burst); clear any set-apply pending so a refusal here can't misblock a
        // set slot.
        _pending.Clear();
        _log?.Info(LogCategory, $"backstab armor — {cmds.Count} piece(s)");
        foreach (string cmd in cmds) _wire.Send(cmd);
        return EquipResult.Applied;
    }

    // True when the apply produced a change — a wear sequence started or a virtual
    // slot wrote CombatSettings. False when the set is already fully in effect.
    // fillFromInventory lets the user-initiated paths top empty / unowned slots up
    // from carried gear (see BuildApplyCommands); auto-fires pass it false.
    // Note a set as the current loadout and fire CurrentSetChanged when it differs
    // from the last. An empty id (an unsaved set) is ignored.
    private void SetCurrentSet(EquipmentSet set)
    {
        if (string.IsNullOrEmpty(set.Id) || string.Equals(CurrentSetId, set.Id, StringComparison.Ordinal))
            return;
        CurrentSetId = set.Id;
        CurrentSetChanged?.Invoke();
    }

    private bool ApplySet(EquipmentSet set, bool fillFromInventory)
    {
        // Record it as the current loadout — whether it needs commands (a real
        // swap) or is already fully worn (a no-op Equip Now still confirms it's on).
        SetCurrentSet(set);

        // Re-evaluate this set's slots against the live character before building,
        // so an item the current alignment / level / class can't wear is blocked,
        // surfaced, and skipped below instead of bonking the game. A realigned
        // character's proactive blocks clear here too.
        RefreshBlocksForSet(set, announce: true);

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

        // In-flight guard: the previous apply of this set is still awaiting the
        // game's `wear` confirmations, so the worn snapshot hasn't caught up and
        // re-diffing re-emits the very same wears. A burst of auto-fire triggers
        // (a rest-complete + posture + walker churn while Default and a pre-rest
        // set overlap the same slots, with HP hovering at the rest threshold)
        // otherwise re-sends the identical `wear`s several times a second — the
        // first lands, the rest bounce off as "you do not have X left unequipped"
        // (report paradigm-20260831-071637). Hold until the confirmations arrive
        // (NoteEquipSucceeded clears _pending) or the pending window lapses; the
        // thrash net still backstops a genuine non-convergence.
        if (IsReapplyInFlight(set, cmds))
        {
            _log?.Debug(LogCategory,
                $"gear set '{set.Name}': prior apply still in flight — not re-sending "
                + $"{cmds.Count} duplicate command(s)");
            return combatChanged;
        }

        if (IsThrashing(set))
            return combatChanged;

        _log?.Info(LogCategory, $"applying gear set '{set.Name}' — {cmds.Count} command(s)");
        RecordPending(set, cmds);
        SendSet(cmds);
        return true;
    }

    // True when every item this apply would (re)wear is already outstanding for
    // THIS set from a prior apply whose confirmations haven't landed yet — a pure
    // re-send. A genuinely new / changed slot (not yet pending) falls through so
    // the real swap still goes out. _pending is cleared as each wear confirms
    // (NoteEquipSucceeded) or when the pending window lapses (ExpirePending).
    private bool IsReapplyInFlight(EquipmentSet set, IReadOnlyList<string> cmds)
    {
        ExpirePending();
        if (_pending.Count == 0) return false;

        bool any = false;
        foreach (string c in cmds)
        {
            string? name =
                c.StartsWith("wear ", StringComparison.Ordinal) ? c["wear ".Length..] :
                c.StartsWith("eq ", StringComparison.Ordinal) ? c["eq ".Length..] : null;
            if (name is null) continue;   // a `rem` prepend isn't a wear
            any = true;
            string n = name.Trim();
            if (!_pending.Any(p => string.Equals(p.SetId, set.Id, StringComparison.Ordinal)
                                   && string.Equals(p.ItemName, n, StringComparison.OrdinalIgnoreCase)))
                return false;             // a wear that isn't already in flight — let it through
        }
        return any;
    }

    // Whether the same set has produced commands too many times in the window — i.e.
    // it isn't converging (the paired-slot rem-then-wear should normally fix that, so
    // this is the safety net). Holds off (returns true) past the limit until the
    // window clears, then lets it try again.
    private bool IsThrashing(EquipmentSet set)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        if (!string.Equals(_thrashSetId, set.Id, StringComparison.Ordinal))
        {
            _thrashSetId = set.Id;
            _thrashApplies.Clear();
            _thrashHolding = false;
        }
        while (_thrashApplies.Count > 0 && now - _thrashApplies.Peek() > ThrashWindow)
            _thrashApplies.Dequeue();

        if (_thrashApplies.Count >= ThrashApplyLimit)
        {
            if (!_thrashHolding)
            {
                _thrashHolding = true;
                _log?.Warn(LogCategory,
                    $"gear set '{set.Name}' isn't converging — {_thrashApplies.Count} applies in "
                    + $"{ThrashWindow.TotalSeconds:0}s; holding off to avoid thrash "
                    + "(check the paired finger / wrist picks against what's worn)");
            }
            return true;
        }

        _thrashHolding = false;
        _thrashApplies.Enqueue(now);
        return false;
    }

    // Pick the command list for an apply: the inventory-aware plan when the caller
    // allows it, an 'i' dump has actually been parsed, and the game-data resolvers
    // are wired; otherwise the set-only worn diff. The set-only path is also what
    // every existing test exercises (their snapshots are never-observed, so
    // haveInventory is false) and what an auto-fire uses.
    private List<string> BuildApplyCommands(
        EquipmentSet set, InventorySnapshot snap, bool fillFromInventory, bool armorOnly = false)
        => PrependTwoHandOffHandConflictRems(set, snap.EquippedItems, _isTwoHanded,
            BuildApplyCommandsCore(set, snap, fillFromInventory, armorOnly));

    private List<string> BuildApplyCommandsCore(
        EquipmentSet set, InventorySnapshot snap, bool fillFromInventory, bool armorOnly = false)
    {
        // Slots we already know the live character can't wear (alignment / level /
        // class, or a prior game refusal) — skipped so a swap never re-bonks them.
        HashSet<string> blocked = BlockedNamesForSet(set);

        bool haveInventory = snap.LastUpdated != DateTimeOffset.MinValue;
        if (fillFromInventory && haveInventory
            && _resolveItemSlot is not null && _canEquipItem is not null)
        {
            return BuildEquipCommands(
                set, snap.CarriedItems, snap.EquippedItems, _resolveItemSlot, _canEquipItem,
                blocked, ParadigmPairedEviction);
        }

        var worn = new HashSet<string>(
            snap.EquippedItems.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        // Gate the set-only diff on the pack once we've parsed an 'i' so an
        // auto-fire trigger doesn't flood failed wears for gear we no longer hold
        // (e.g. after a death dumped the whole loadout into a deathpile).
        List<string> wears = BuildWearCommands(set, worn, armorOnly: armorOnly,
            availableNames: haveInventory ? HeldNames(snap) : null, blockedNames: blocked);
        if (wears.Count == 0) return wears;

        // Compose the paired finger / wrist frees around the equips so the pick bound
        // for the OTHER slot rems its odd-out first (the pick bound for the evicted
        // slot rides the eq's auto-evict) — see ComposePairedSlotCommands. Only
        // families actually gaining a member are touched.
        return ComposePairedSlotCommands(set, snap.EquippedItems, wears, ParadigmPairedEviction);
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
        ISet<string>? availableNames = null, ISet<string>? blockedNames = null)
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
            // Known-unwearable (blocked) picks are skipped so a swap doesn't
            // re-bonk on an item the character can't wear (alignment / refusal).
            if (blockedNames is not null && blockedNames.Contains(name)) continue;
            cmds.Add($"{Verb(e.Slot)} {name}");
        }
        return cmds;
    }

    private static readonly EquipmentSlot[] PairedFamilies =
        { EquipmentSlot.Finger1, EquipmentSlot.Wrist1 };

    // Compose the paired finger / wrist frees around the set's equips. CONFIRMED
    // mechanic (user in-game tests, 2026-09-03, both realms): the `lo`/`i` listing
    // order IS reliable physical slot order (first-listed = slot 1). An `eq`/`wear`
    // into a FULL paired family target-swaps ONE physical slot — the new item evicts
    // that slot's occupant and takes its place; into a family with a free slot it
    // fills the empty slot. WHICH slot is evicted differs by realm:
    //   - Paradigm evicts SLOT 1 (the first-listed).
    //   - Stock evicts SLOT 2 (the last-listed).
    // Paired items are equipped with `eq` (see Verb), which takes the evicted slot in
    // place — `wear` on Paradigm appends the new ring to slot 2 and shuffles the
    // survivor into slot 1, which scrambled the order across a swap cycle and made the
    // client emit a needless `rem` every time (report paradigm-20260903-111522).
    //
    // So a set pick destined for the evicted slot needs NO `rem` (the eq auto-evicts
    // the odd-out sitting there); a pick destined for the OTHER slot — the one the eq
    // would leave alone — needs the odd-out remmed first (else the eq drops the member
    // the set keeps). We simulate the equips in order and emit a `rem` only when a
    // bare eq would evict a member the set keeps. Non-family commands pass through
    // untouched. evictsFirstListed = the realm's evicted slot (true = Paradigm/slot 1).
    internal static List<string> ComposePairedSlotCommands(
        EquipmentSet set, IReadOnlyList<EquippedItem> worn, List<string> wears,
        bool evictsFirstListed = true)
    {
        // Which family equips need a `rem` before them (keyed by the exact command).
        var remBefore = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (EquipmentSlot family in PairedFamilies)
        {
            var setMembers = set.Slots
                .Where(e => !IsVirtual(e.Slot) && FamilyOf(e.Slot) == family
                            && !string.IsNullOrWhiteSpace(e.ItemName))
                .Select(e => e.ItemName!.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (setMembers.Count == 0) continue;

            // This family's new-member equips, in the order they'll be sent (either verb).
            var familyWears = wears
                .Where(c => PairedCommandItem(c) is { } it && setMembers.Contains(it))
                .ToList();
            if (familyWears.Count == 0) continue;   // family isn't gaining a member

            // Worn family members in `i` (slot) order — index 0 is slot 1 (reliable).
            var wornList = worn
                .Where(e => EquipmentSlotMap.FromWornString(e.Slot) is { } s && FamilyOf(s) == family)
                .Select(e => e.Name.Trim())
                .ToList();

            foreach (string wearCmd in familyWears)
            {
                string newItem = PairedCommandItem(wearCmd)!;
                if (wornList.Count < 2)
                {
                    wornList.Add(newItem);   // a slot is free — the eq fills it, evicting nothing
                    continue;
                }
                // The physical slot a bare eq/wear evicts: slot 1 (index 0) on
                // Paradigm, slot 2 (the last) on Stock.
                int evictIdx = evictsFirstListed ? 0 : wornList.Count - 1;
                if (!setMembers.Contains(wornList[evictIdx]))
                {
                    wornList[evictIdx] = newItem;   // evicted slot holds an odd-out — the eq drops it, no `rem`
                }
                else
                {
                    // The evicted slot holds a member the set keeps — a bare eq would
                    // drop it, so free a slot by remming an odd-out first.
                    string? oddOut = wornList.FirstOrDefault(n => !setMembers.Contains(n));
                    if (oddOut is null) { wornList.Add(newItem); continue; }   // full of kept members (invalid set)
                    remBefore[wearCmd] = $"rem {oddOut}";
                    wornList.Remove(oddOut);
                    wornList.Add(newItem);
                }
            }
        }

        var result = new List<string>(wears.Count + remBefore.Count);
        foreach (string cmd in wears)
        {
            if (remBefore.TryGetValue(cmd, out string? rem)) result.Add(rem);
            result.Add(cmd);
        }
        return result;
    }

    // The item name from a paired-slot equip command ("eq X" / "wear X"), or null.
    private static string? PairedCommandItem(string cmd) =>
        cmd.StartsWith("eq ", StringComparison.Ordinal) ? cmd["eq ".Length..]
        : cmd.StartsWith("wear ", StringComparison.Ordinal) ? cmd["wear ".Length..]
        : null;

    // A two-handed weapon and an off-hand item can't coexist — the game rejects
    // whichever wear would violate that, so a set swap that changes the hands must
    // strip the conflicting worn piece FIRST or the wear fails and only sticks on a
    // later re-apply. Two symmetric cases (both mirror SwapWeapon's own guards):
    //   1. The set brings an OFF-HAND item on while a two-hander is worn — "You may
    //      not wear an off-hand item while you have a 2-handed weapon readied."
    //      (report paradigm-20260826-132742). Rem the worn two-hander first; the
    //      set's own one-handed weapon re-arms the hand after.
    //   2. The set wields a TWO-HANDER while an off-hand is worn — "You may not
    //      ready a 2-handed weapon with your <item> worn!" (report
    //      paradigm-20260826-142732). Rem the worn off-hand first.
    // Only fires when the plan actually wears the conflicting piece this pass (an
    // already-worn item isn't re-issued, so there's nothing to clear).
    internal static List<string> PrependTwoHandOffHandConflictRems(
        EquipmentSet set, IReadOnlyList<EquippedItem> worn,
        Func<string?, bool> isTwoHanded, List<string> cmds)
    {
        string? wornWeapon = WornSlotItem(worn, "Weapon Hand");
        string? wornOffHand = WornSlotItem(worn, "Off-Hand");

        // Case 1: set wears an off-hand while a two-hander is worn → rem the 2H.
        string? setOffHand = set.Slots
            .FirstOrDefault(e => e.Slot == EquipmentSlot.OffHand)?.ItemName?.Trim();
        if (!string.IsNullOrEmpty(setOffHand) && PlanEquips(cmds, setOffHand)
            && !string.IsNullOrWhiteSpace(wornWeapon) && isTwoHanded(wornWeapon))
        {
            cmds.Insert(0, $"rem {wornWeapon.Trim()}");
            return cmds;
        }

        // Case 2: set wields a two-hander while an off-hand is worn → rem the off-hand.
        string? setWeapon = set.Slots
            .FirstOrDefault(e => e.Slot == EquipmentSlot.Weapon)?.ItemName?.Trim();
        if (!string.IsNullOrEmpty(setWeapon) && PlanEquips(cmds, setWeapon) && isTwoHanded(setWeapon)
            && !string.IsNullOrWhiteSpace(wornOffHand))
        {
            cmds.Insert(0, $"rem {wornOffHand.Trim()}");
        }
        return cmds;
    }

    // The item worn in a real slot label ("Weapon Hand" / "Off-Hand"), or null.
    private static string? WornSlotItem(IReadOnlyList<EquippedItem> worn, string slot)
        => worn
            .Where(e => string.Equals(e.Slot, slot, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Name)
            .FirstOrDefault();

    // Whether the plan wears/wields the named item this pass — either verb, since a
    // weapon takes `eq` on the inventory-aware path and `wear` on the set-only diff.
    private static bool PlanEquips(List<string> cmds, string name) => cmds.Any(c =>
        (c.StartsWith("wear ", StringComparison.Ordinal)
            && string.Equals(c["wear ".Length..], name, StringComparison.OrdinalIgnoreCase))
        || (c.StartsWith("eq ", StringComparison.Ordinal)
            && string.Equals(c["eq ".Length..], name, StringComparison.OrdinalIgnoreCase)));

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
        Func<string, bool> canEquip,
        ISet<string>? blockedNames = null,
        bool evictsFirstListed = true)
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
            // Known-unwearable (blocked) pick — skip so we don't re-bonk on it.
            if (blockedNames is not null && blockedNames.Contains(name)) continue;
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
            if (blockedNames is not null && blockedNames.Contains(name)) continue;
            if (resolveSlot(name) is not EquipmentSlot slot || IsVirtual(slot)) continue;
            EquipmentSlot family = FamilyOf(slot);
            if (used.GetValueOrDefault(family) >= Capacity(family)) continue;
            if (!canEquip(name)) continue;
            result.Add($"{Verb(slot)} {name}");
            chosen.Add(name);
            Bump(used, family);
        }

        // Compose the paired finger / wrist frees around the wears — the same slot-2-
        // rems / slot-1-rides-the-wear logic the set-only path uses (see
        // ComposePairedSlotCommands). This inventory-aware path otherwise emitted only
        // wears, so swapping the SECOND ring / bracelet let the game's `wear` trade
        // with the member the set keeps and never settled (report
        // paradigm-20260825-103537). Only families actually gaining a member are touched.
        return ComposePairedSlotCommands(set, worn, result, evictsFirstListed);
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
    // The universal `eq` verb for weapons AND the paired finger / wrist families;
    // plain `wear` for single armour slots. `eq` on a paired item target-swaps the
    // evicted physical slot in place (the new ring takes that slot), where `wear`
    // could append and shuffle the pair's order across a swap cycle (report
    // paradigm-20260903-111522). Single slots don't care — either verb replaces.
    private static string Verb(EquipmentSlot slot) => slot switch
    {
        EquipmentSlot.Weapon => "eq",
        EquipmentSlot.Finger1 or EquipmentSlot.Finger2
            or EquipmentSlot.Wrist1 or EquipmentSlot.Wrist2 => "eq",
        _ => "wear",
    };

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

    // ----- unwearable-slot blocks ----------------------------------------

    // True when the (set, slot) is blocked — its item is skipped on apply.
    public bool IsSlotBlocked(string setId, EquipmentSlot slot) =>
        _blocked.ContainsKey((setId, slot));

    // Read-only snapshot of every currently-blocked slot — for the bug report.
    public IReadOnlyList<(string SetId, EquipmentSlot Slot, string ItemName, bool GameRefused)>
        BlockedSlotsSnapshot() =>
        _blocked.Select(kv =>
            (kv.Key.SetId, kv.Key.Slot, kv.Value.Name, kv.Value.ServerConfirmed)).ToList();

    // The blocked item names for a set, so the command builders can skip them.
    private HashSet<string> BlockedNamesForSet(EquipmentSet set)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<(string SetId, EquipmentSlot Slot), BlockInfo> kv in _blocked)
            if (string.Equals(kv.Key.SetId, set.Id, StringComparison.Ordinal))
                names.Add(kv.Value.Name);
        return names;
    }

    private void SetBlock((string SetId, EquipmentSlot Slot) key, string name,
        bool serverConfirmed, bool announce)
    {
        if (_blocked.TryGetValue(key, out BlockInfo existing)
            && string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            // Already blocked for this item — only upgrade to server-confirmed.
            if (serverConfirmed && !existing.ServerConfirmed)
                _blocked[key] = existing with { ServerConfirmed = true };
            return;
        }
        _blocked[key] = new BlockInfo(name, serverConfirmed);
        _log?.Info(LogCategory,
            $"slot {key.Slot} blocked — can't wear '{name}'{(serverConfirmed ? " (game refused)" : "")}");
        BlocksChanged?.Invoke();
        if (announce) SlotBlockedAnnounced?.Invoke(new EquipBlock(key.SetId, key.Slot, name));
    }

    private bool RemoveBlock((string SetId, EquipmentSlot Slot) key)
    {
        if (!_blocked.Remove(key)) return false;
        BlocksChanged?.Invoke();
        return true;
    }

    // Clear a slot's block — the Equipment tab calls this when the user edits the
    // slot's item ("adjust set and save to correct"). Clears both proactive and
    // server-confirmed blocks; the user has addressed it.
    public void ClearBlock(string setId, EquipmentSlot slot) => RemoveBlock((setId, slot));

    // Drop every block for a set — used when a profile unloads / the block set
    // must be rebuilt from scratch.
    public void ResetBlocks()
    {
        if (_blocked.Count > 0) { _blocked.Clear(); BlocksChanged?.Invoke(); }
        _pending.Clear();
        _blocksSeeded = false;
    }

    // Re-evaluate every physical slot of the set against the live character's
    // wearability (alignment / level / class). Blocks slots whose item the
    // character currently can't wear, and clears PROACTIVE blocks whose item is
    // wearable again (e.g. an alignment return) — a server-confirmed refusal
    // block is sticky until the user edits the slot. announce surfaces newly
    // blocked slots as a terminal notice. No-op without the restriction probe.
    public void RefreshBlocksForSet(EquipmentSet set, bool announce = true)
    {
        if (_isEquipRestricted is null) return;
        foreach (EquipmentSlotEntry e in set.Slots)
        {
            if (IsVirtual(e.Slot)) continue;
            (string SetId, EquipmentSlot Slot) key = (set.Id, e.Slot);
            string? name = e.ItemName?.Trim();
            if (string.IsNullOrEmpty(name)) { RemoveBlock(key); continue; }
            if (_isEquipRestricted(name))
                SetBlock(key, name, serverConfirmed: false, announce: announce);
            else if (_blocked.TryGetValue(key, out BlockInfo b) && !b.ServerConfirmed)
                RemoveBlock(key);   // proactive block lifted; a refusal block stays
        }
    }

    // Re-evaluate blocks across every configured set — wired to the live
    // alignment refresh so a drift re-blocks (and a realignment clears the
    // proactive ones) without needing a swap. The first pass after a profile
    // load seeds silently; later passes announce new blocks.
    public void ReevaluateAllBlocks()
    {
        bool announce = _blocksSeeded;
        foreach (EquipmentSet set in _readEquipment().Sets)
            RefreshBlocksForSet(set, announce);
        _blocksSeeded = true;
    }

    // Record the wear/eq attempts a just-built apply is about to send, so an
    // anonymous refusal line can be attributed back to the slot+item. Only the
    // set's own picks are tracked (an inventory-fallback fill isn't a set slot).
    private void RecordPending(EquipmentSet set, IReadOnlyList<string> cmds)
    {
        _pending.Clear();
        _pendingStamp = DateTimeOffset.Now;
        foreach (string c in cmds)
        {
            string? name =
                c.StartsWith("wear ", StringComparison.Ordinal) ? c["wear ".Length..] :
                c.StartsWith("eq ", StringComparison.Ordinal) ? c["eq ".Length..] : null;
            if (name is null) continue;
            EquipmentSlotEntry? entry = set.Slots.FirstOrDefault(s =>
                !IsVirtual(s.Slot)
                && string.Equals(s.ItemName?.Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (entry is null) continue;
            _pending.Add(new PendingEquip(set.Id, entry.Slot, name));
        }
    }

    private void ExpirePending()
    {
        if (_pending.Count > 0 && DateTimeOffset.Now - _pendingStamp > PendingWindow)
            _pending.Clear();
    }

    // The game confirmed a wear ("You are now wearing X") — drop it from the
    // pending attempts so a later refusal isn't misattributed to it.
    public void NoteEquipSucceeded(string itemName)
    {
        ExpirePending();
        string n = itemName.Trim();
        _pending.RemoveAll(p => string.Equals(p.ItemName, n, StringComparison.OrdinalIgnoreCase));
    }

    // The game refused an armor wear ("You may not wear that item!"). Attribute
    // it to the oldest unresolved armor attempt (weapon attempts excluded) and
    // block that slot — server-confirmed, sticky until the user edits it.
    public void NoteWearRefused() => BlockOldestPending(weapon: false);

    // The game refused a weapon wield ("You may not use that weapon." — the
    // weapon EP-zap). Attribute it to the oldest unresolved weapon attempt.
    public void NoteWeaponRefused() => BlockOldestPending(weapon: true);

    private void BlockOldestPending(bool weapon)
    {
        ExpirePending();
        int idx = _pending.FindIndex(p => (p.Slot == EquipmentSlot.Weapon) == weapon);
        if (idx < 0) return;
        PendingEquip p = _pending[idx];
        _pending.RemoveAt(idx);
        SetBlock((p.SetId, p.Slot), p.ItemName, serverConfirmed: true, announce: true);
    }

    // ----- gear-set send -------------------------------------------------

    // Stream a gear-set delta's wear/rem commands to the wire back-to-back, no
    // inter-command spacing. MajorMUD/Paradigm buffers typed-ahead input, so a
    // full-loadout swap goes out as one burst for Mega-parity speed (the old paced
    // DispatcherTimer stretched a ~13-command swap to over a second and felt laggy);
    // TelnetClient serialises the outbound writes, so command order is preserved.
    // IsApplyingSet is true only for this synchronous span, so the GearSwap movement
    // gate asserts and clears around it — a concurrent loop step still holds until
    // the swap's commands are out, then steps out already in the new loadout.
    private void SendSet(IReadOnlyList<string> cmds)
    {
        if (cmds.Count == 0) return;
        bool wasEquipping = _isEquipping;
        _isEquipping = true;
        if (!wasEquipping) ApplyingChanged?.Invoke(true);
        foreach (string c in cmds) _wire.Send(c);
        _isEquipping = false;
        if (!wasEquipping) ApplyingChanged?.Invoke(false);
    }
}
