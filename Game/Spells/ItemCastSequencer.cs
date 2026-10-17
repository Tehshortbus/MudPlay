using System;
using System.Collections.Generic;
using MudPlay.Game.Inventory;
using MudPlay.Services;

namespace MudPlay.Game.Spells;

// Issues the wire sequence that casts a spell from an item: the item must be
// equipped to use it, so the engine equips the cast item, uses it, then re-equips
// whatever it displaced. Driven by CastingDirector when a Bless slot holds an
// ItemCastToken (#<item name>).
//
// Only unlimited-use cast items are eligible — a limited-charge item would burn out
// on a recast loop, and the Spell Book only ever offers a token for unlimited items.
//
// Restore is slot-aware. A cast item displaces only what sits in ITS OWN equip slot,
// so we restore that slot from the last i dump's EquippedItems: a 1H weapon restores
// the weapon hand, an off-hand item (a shield, a warhorn) restores the off-hand, a
// worn item restores its worn slot — and it never touches the other hand. Whatever
// isn't currently held simply isn't restored.
//
// Two-handed dance: a two-handed CAST item needs both hands, so it displaces the
// weapon AND the off-hand. When an off-hand is held we remove it first, then eq the
// item, use it, eq the displaced 1H weapon back (which drops the two-hander and frees
// the off-hand slot), and finally eq the off-hand again. (Confirmed MajorMUD equip
// order; see GAME_MECHANICS.md.) eq is the universal equip verb (matches CombatManager).
//
// Some off-hand-occupying items (e.g. a worn charm/skull) don't print under the literal
// "Off-Hand" slot in the client's snapshot — the game's own 'i' listing buckets them
// under the generic "Worn" label, but they still functionally block a two-handed wield
// exactly like a shield does ("You may not ready a 2-handed weapon with your <item>
// worn!"). isOffHandItem lets the two-handed branch recognize one of these among the
// Worn-slot entries by its game-data Worn code (report paradigm-20260819-234712: a red
// skull, Worn=Off-Hand but displayed as "Worn", silently blocked every crozier recast).
//
// The commands are sent back-to-back — MajorMUD queues typed input, and an item use
// resolves the cast immediately so the restore can follow without waiting. The buff
// timer (recast scheduling) is owned by CastingDirector, not this sequencer; this
// only puts the equip/use/restore lines on the wire.
public sealed class ItemCastSequencer
{
    // LogService category — appears as [ItemCast] rows.
    public const string LogCategory = "ItemCast";

    // Inventory slot label a wielded weapon occupies (matches EquippedItem.Slot
    // normalization).
    private const string WeaponHandSlot = "Weapon Hand";

    // Inventory slot label a shield / off-hand item occupies.
    private const string OffHandSlot = "Off-Hand";

    // Inventory slot label the client files a game-displayed "(Worn)" item under —
    // the generic bucket some off-hand-blocking charms land in (see isOffHandItem).
    private const string WornSlot = "Worn";

    private readonly Func<IReadOnlyList<ClassCastItem>> _castItems;
    private readonly Func<InventorySnapshot> _inventory;
    // Given an inventory slot label, the item the equipment manager wants worn there
    // (its Default set), or null. Used as the restore fallback when the live slot
    // can't say what belongs there — see RestoreTarget. Null when unwired.
    private readonly Func<string, string?>? _desiredSlotItem;
    // True when a display-name's game data marks it an off-hand item (Items.Worn ==
    // Off-Hand), regardless of which slot label the live snapshot filed it under.
    // Lets the two-handed branch recognize a Worn-bucketed off-hand blocker. Null
    // when unwired — the Worn slot is then never scanned.
    private readonly Func<string, bool>? _isOffHandItem;
    // True when the named WIELDED weapon is two-handed in the active game data. A
    // two-hander fills both hands, so an off-hand cast item can't be equipped while
    // it's worn — the reverse of the two-handed-CAST-item case. Null when unwired
    // — the reverse dance is then never taken.
    private readonly Func<string, bool>? _isWornWeaponTwoHanded;
    // True once a full 'i' has been parsed THIS session (InventoryManager.IsLoaded).
    // The equip plan reads the worn loadout, so we defer until it's real — and unlike
    // the snapshot's LastUpdated, IsLoaded is reset (MarkStale) on death / disconnect,
    // so a reconnect's stale-but-timestamped snapshot still reads as unknown (report
    // paradigm-20260826-150242). Null when unwired — the gate is then off.
    private readonly Func<bool>? _wornLoadoutKnown;
    // Invoked when a swap is about to hit the wire, so auto-equip can stand off the
    // borrowed slot while this sequence's own restore handles it (see
    // AutoEquipCoordinator.NoteItemCastSwap). Null when unwired.
    private readonly Action? _onSwap;
    private readonly WireSender _wire = new();
    private readonly LogService? _log;

    // Set once we've asked for an 'i' because the worn loadout wasn't known yet
    // (see Execute). Cleared the moment a dump lands, so the request fires once per
    // unknown window rather than every deferred round.
    private bool _requestedInventoryRefresh;

    public ItemCastSequencer(
        Func<IReadOnlyList<ClassCastItem>> castItems,
        Func<InventorySnapshot> inventory,
        LogService? log = null,
        Func<string, string?>? desiredSlotItem = null,
        Action? onSwap = null,
        Func<string, bool>? isOffHandItem = null,
        Func<string, bool>? isWornWeaponTwoHanded = null,
        Func<bool>? wornLoadoutKnown = null)
    {
        ArgumentNullException.ThrowIfNull(castItems);
        ArgumentNullException.ThrowIfNull(inventory);
        _castItems = castItems;
        _inventory = inventory;
        _log = log;
        _desiredSlotItem = desiredSlotItem;
        _onSwap = onSwap;
        _isOffHandItem = isOffHandItem;
        _isWornWeaponTwoHanded = isWornWeaponTwoHanded;
        _wornLoadoutKnown = wornLoadoutKnown;
    }

    // Bind the wire sink (the wrapped engine sender).
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Run the equip → use → restore sequence for the cast item named by the token.
    // Returns true only when the lines were sent: the wire must be bound, the token
    // must resolve to an unlimited-use cast item. The weapon restore is skipped when
    // nothing was wielded (or the held weapon IS the cast item); the off-hand is
    // only juggled when the cast item is two-handed and a shield/off-hand is held.
    public bool Execute(string token)
    {
        if (!_wire.IsBound) return false;
        if (!ItemCastToken.TryResolve(token, _castItems(), out ClassCastItem item))
        {
            _log?.Debug(LogCategory, $"unresolved item-cast token: {token}");
            return false;
        }
        string name = item.ItemName.Trim();

        // A carried item has no equip slot: it is used straight from the pack, with
        // nothing to ready or put back (GAME_MECHANICS "Deck of cards (Gypsy)"). It is
        // kept after use, so its charge count is not a reason to hold it back.
        if (item.Carried)
        {
            // Not in the pack (dropped, or another character's setup): each use would
            // only print "You don't have …", cycle after cycle.
            if (_wornLoadoutKnown?.Invoke() != false && _inventory().LastUpdated != default && !_inventory().Has(name))
            {
                _log?.Debug(LogCategory, $"item-cast (carried) skipped: not carrying \"{name}\"");
                return false;
            }
            _log?.Info(LogCategory, $"item-cast (carried): use item=\"{name}\"");
            _wire.Send($"use {name}");
            return true;
        }

        if (!item.Unlimited)
        {
            // Limited-charge items aren't safe to recast on a buff loop.
            _log?.Debug(LogCategory, $"skip limited-charge item-cast: {item.ItemName}");
            return false;
        }

        InventorySnapshot inv = _inventory();

        // The equip/restore plan below reads the worn loadout — which weapon is
        // wielded (to run the two-handed dance) and what to put back. Until a full
        // 'i' has been parsed this session the snapshot is empty or stale, so firing
        // here would `eq` the cast item blind: on a character with a two-hander worn,
        // an off-hand cast item is rejected outright ("You may not wear an off-hand
        // item while you have a 2-handed weapon readied."), the use then fails, yet
        // CastingDirector still arms the buff timer — the false "buffed" state on
        // login (reports -144339 / -150242). Defer until the loadout is known
        // (IsLoaded, the same gate AutoEquipCoordinator uses — reset on death /
        // disconnect, unlike the snapshot timestamp), requesting one 'i' so it
        // resolves promptly.
        if (_wornLoadoutKnown?.Invoke() == false)
        {
            if (!_requestedInventoryRefresh)
            {
                _requestedInventoryRefresh = true;
                _wire.Send("i");
            }
            _log?.Debug(LogCategory,
                $"item-cast deferred: worn loadout not yet known this session — item=\"{name}\"");
            return false;
        }
        _requestedInventoryRefresh = false;

        // Tell auto-equip a slot swap is starting so it stands off the borrowed slot
        // — this sequence restores it itself, and the rest this swap breaks would
        // otherwise trip a redundant re-equip that doubles the restore.
        _onSwap?.Invoke();

        // A two-handed cast weapon needs BOTH hands, so it displaces the weapon AND
        // the off-hand: free the off-hand first, wield + use, then eq the 1H weapon
        // back (which drops the two-hander and frees the off-hand slot) and finally
        // re-wear the off-hand. Keyed on the CAST item being two-handed.
        if (item.IsTwoHanded)
        {
            // What to put BACK after the cast — mirror the 1H branch's RestoreTarget
            // fallback: prefer the live worn item, but when the snapshot has nothing
            // there (or shows the cast 2H ITSELF — a strand left by a prior recast),
            // fall back to the Default set's weapon + off-hand. Without this fallback a
            // stranded greatsword restores nothing and stays two-handed over the
            // intended 1H+shield forever, and every later recast sees it in-hand and
            // restores nothing again — self-perpetuating. The 1H branch already
            // recovers this way; the 2H branch didn't (the reported clobber).
            string? restoreWeapon = RestoreTarget(inv, WeaponHandSlot, name);
            string? restoreOffHand = OffHandRestoreTarget(inv, name);
            // The blocker to REMOVE before the wield is whatever's LIVE in "Off-Hand"
            // (or a Worn-bucketed off-hand occupant — a charm/skull the snapshot never
            // labels "Off-Hand"); never the Default-set fallback, which isn't worn yet
            // and so can't be removed.
            string? blockerToFree = SlotItem(inv, OffHandSlot) ?? OffHandBlockingWornItem(inv);
            bool freeOffHand = Differs(blockerToFree, name);
            bool restoreWeaponDiffers = Differs(restoreWeapon, name);
            bool restoreOffHandDiffers = Differs(restoreOffHand, name);

            if (freeOffHand) _wire.Send($"remove {blockerToFree}");
            _wire.Send($"eq {name}");
            _wire.Send($"use {name}");
            if (restoreWeaponDiffers) _wire.Send($"eq {restoreWeapon}");
            if (restoreOffHandDiffers) _wire.Send($"eq {restoreOffHand}");

            _log?.Info(LogCategory,
                $"item-cast item=\"{name}\" 2h=True casts={item.SpellName} " +
                $"restore-weapon={restoreWeapon ?? "<none>"} restore-offhand={(restoreOffHandDiffers ? restoreOffHand : "<none>")}");
            return true;
        }

        // A one-handed cast item displaces only whatever sits in ITS OWN wear slot —
        // the weapon hand for a 1H weapon, the off-hand for a shield / warhorn, a worn
        // slot for a charged amulet. Restore exactly that; never assume the weapon (an
        // off-hand cast item that re-equipped the weapon would strand the buff item in
        // the off-hand and never put the shield back — the reported bug).
        string slot = string.IsNullOrWhiteSpace(item.WearSlot) ? WeaponHandSlot : item.WearSlot.Trim();

        // Mirror of the two-handed-CAST dance: the cast item is a one-handed
        // OFF-HAND item (a held tome, a warhorn) but the WIELDED weapon is
        // two-handed. A two-hander fills both hands, so the game rejects
        // `eq <off-hand item>` outright while it's worn. Free the weapon first,
        // wield + use the item, then remove it to clear the off-hand and re-wield
        // the two-hander (which the game likewise blocks while the off-hand is
        // occupied). Confirmed MajorMUD equip order — see GAME_MECHANICS.md.
        bool castItemIsOffHand =
            string.Equals(slot, OffHandSlot, StringComparison.OrdinalIgnoreCase)
            || _isOffHandItem?.Invoke(name) == true;
        if (castItemIsOffHand
            && SlotItem(inv, WeaponHandSlot) is { } wornWeapon
            && _isWornWeaponTwoHanded?.Invoke(wornWeapon) == true)
        {
            _wire.Send($"remove {wornWeapon}");
            _wire.Send($"eq {name}");
            _wire.Send($"use {name}");
            _wire.Send($"remove {name}");
            _wire.Send($"eq {wornWeapon}");
            _log?.Info(LogCategory,
                $"item-cast item=\"{name}\" 2h=False slot=Off-Hand casts={item.SpellName} " +
                $"two-handed-weapon-worn={wornWeapon} (removed to free the off-hand, re-wielded after)");
            return true;
        }

        string? restore = RestoreTarget(inv, slot, name);
        // Skip a redundant re-equip when the cast item is already in its slot (left
        // there from a prior session) — the game would just answer "you do not have
        // <item> left unequipped." The use still fires, and the restore below swaps
        // the right gear back in over it.
        bool alreadyWorn = string.Equals(SlotItem(inv, slot), name, StringComparison.OrdinalIgnoreCase);

        if (!alreadyWorn) _wire.Send($"eq {name}");
        _wire.Send($"use {name}");
        if (restore is not null) _wire.Send($"eq {restore}");

        _log?.Info(LogCategory,
            $"item-cast item=\"{name}\" 2h=False slot={slot} casts={item.SpellName} " +
            $"already-worn={alreadyWorn} restore={restore ?? "<none>"}");
        return true;
    }

    // What to put back into `slot` after the cast: the live gear there, unless that
    // slot is empty or still holds the cast item itself (e.g. the buff item was left
    // equipped across a session) — then fall back to what the equipment manager wants
    // in that slot, so the buff still swaps the right gear back in. Null when there's
    // nothing worth restoring.
    private string? RestoreTarget(InventorySnapshot inv, string slot, string castName)
    {
        string? live = SlotItem(inv, slot);
        if (Differs(live, castName)) return live;
        string? desired = _desiredSlotItem?.Invoke(slot);
        return Differs(desired, castName) ? desired : null;
    }

    // The off-hand equivalent of RestoreTarget for the two-handed dance: the live
    // Off-Hand slot or a Worn-bucketed off-hand occupant (a charm/skull), else — same
    // Default-set fallback RestoreTarget uses — the set's off-hand, so a stranded 2H
    // recovers its shield instead of staying two-handed. Null when nothing to restore.
    private string? OffHandRestoreTarget(InventorySnapshot inv, string castName)
    {
        string? live = SlotItem(inv, OffHandSlot) ?? OffHandBlockingWornItem(inv);
        if (Differs(live, castName)) return live;
        string? desired = _desiredSlotItem?.Invoke(OffHandSlot);
        return Differs(desired, castName) ? desired : null;
    }

    // A worn item worth restoring: the slot held something, and it isn't the cast
    // item itself (equipping the cast item over its own copy needs no restore).
    private static bool Differs(string? worn, string castName) =>
        !string.IsNullOrWhiteSpace(worn)
        && !string.Equals(worn, castName, StringComparison.OrdinalIgnoreCase);

    // The Worn-slot item, if any, whose game data marks it an off-hand occupant —
    // the two-handed branch's fallback for a blocker the snapshot didn't label
    // "Off-Hand". Null when unwired or no Worn item resolves as off-hand.
    private string? OffHandBlockingWornItem(InventorySnapshot inv)
    {
        if (_isOffHandItem is null) return null;
        foreach (EquippedItem e in inv.EquippedItems)
        {
            if (!string.Equals(e.Slot.Trim(), WornSlot, StringComparison.OrdinalIgnoreCase)) continue;
            string n = e.Name.Trim();
            if (n.Length > 0 && _isOffHandItem(n)) return n;
        }
        return null;
    }

    // The item name occupying the given slot in the last inventory dump, or null
    // when that slot is empty.
    private string? SlotItem(InventorySnapshot inv, string slot)
    {
        foreach (EquippedItem e in inv.EquippedItems)
            if (string.Equals(e.Slot.Trim(), slot, StringComparison.OrdinalIgnoreCase))
            {
                string n = e.Name.Trim();
                return n.Length > 0 ? n : null;
            }
        return null;
    }
}
