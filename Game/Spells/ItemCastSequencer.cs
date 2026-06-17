using System;
using System.Collections.Generic;
using FujinTerm.Game.Inventory;
using FujinTerm.Services;

namespace FujinTerm.Game.Spells;

/// <summary>
/// Issues the wire sequence that casts a spell <i>from an item</i>: the item
/// must be equipped to <c>use</c> it, so the engine equips the cast item, uses
/// it, then re-equips whatever it displaced. Driven by
/// <see cref="CastingDirector"/> when a Bless slot holds an
/// <see cref="ItemCastToken"/> (<c>#&lt;item name&gt;</c>).
/// </summary>
/// <remarks>
/// <para>
/// Only <b>unlimited-use</b> cast items are eligible — a limited-charge item
/// would burn out on a recast loop, and the Spell Book only ever offers a token
/// for unlimited items. The displaced weapon (and, for a two-handed cast item,
/// the off-hand) are read from the last <c>i</c> dump's
/// <see cref="InventorySnapshot.EquippedItems"/> (<c>Weapon Hand</c> /
/// <c>Off-Hand</c> slots); whatever isn't held simply isn't restored.
/// </para>
/// <para>
/// <b>Two-handed dance.</b> A two-handed cast item needs both hands, so when an
/// off-hand is held we <c>remove</c> it first, then <c>eq</c> the item,
/// <c>use</c> it, <c>eq</c> the displaced weapon back (which frees the off-hand
/// slot), and finally <c>eq</c> the off-hand again. A one-handed cast item just
/// equips alongside the shield, so only the weapon is restored. <c>eq</c> is the
/// universal equip verb (matches <see cref="Game.Combat.CombatManager"/>).
/// </para>
/// <para>
/// The commands are sent back-to-back — MajorMUD queues typed input, and an item
/// <c>use</c> resolves the cast immediately so the restore can follow without
/// waiting. The buff <i>timer</i> (recast scheduling) is owned by
/// <see cref="CastingDirector"/>, not this sequencer; this only puts the
/// equip/use/restore lines on the wire.
/// </para>
/// </remarks>
public sealed class ItemCastSequencer
{
    /// <summary>LogService category — appears as <c>[ItemCast]</c> rows.</summary>
    public const string LogCategory = "ItemCast";

    /// <summary>Inventory slot label a wielded weapon occupies (matches
    /// <see cref="EquippedItem.Slot"/> normalization).</summary>
    private const string WeaponHandSlot = "Weapon Hand";

    /// <summary>Inventory slot label a shield / off-hand item occupies.</summary>
    private const string OffHandSlot = "Off-Hand";

    private readonly Func<IReadOnlyList<ClassCastItem>> _castItems;
    private readonly Func<InventorySnapshot> _inventory;
    private readonly WireSender _wire = new();
    private readonly LogService? _log;

    public ItemCastSequencer(
        Func<IReadOnlyList<ClassCastItem>> castItems,
        Func<InventorySnapshot> inventory,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(castItems);
        ArgumentNullException.ThrowIfNull(inventory);
        _castItems = castItems;
        _inventory = inventory;
        _log = log;
    }

    /// <summary>Bind the wire sink (the wrapped engine sender).</summary>
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    /// <summary>Test seam — every buffer sent, in order.</summary>
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    /// <summary>
    /// Run the equip → use → restore sequence for the cast item named by
    /// <paramref name="token"/>. Returns <c>true</c> only when the lines were
    /// sent: the wire must be bound, the token must resolve to an unlimited-use
    /// cast item. The weapon restore is skipped when nothing was wielded (or the
    /// held weapon IS the cast item); the off-hand is only juggled when the cast
    /// item is two-handed and a shield/off-hand is held.
    /// </summary>
    public bool Execute(string token)
    {
        if (!_wire.IsBound) return false;
        if (!ItemCastToken.TryResolve(token, _castItems(), out ClassCastItem item))
        {
            _log?.Debug(LogCategory, $"unresolved item-cast token: {token}");
            return false;
        }
        if (!item.Unlimited)
        {
            // Limited-charge items aren't safe to recast on a buff loop.
            _log?.Debug(LogCategory, $"skip limited-charge item-cast: {item.ItemName}");
            return false;
        }

        string name = item.ItemName.Trim();
        InventorySnapshot inv = _inventory();
        string? restoreWeapon = SlotItem(inv, WeaponHandSlot);
        string? restoreOffHand = SlotItem(inv, OffHandSlot);

        bool restoreWeaponDiffers = !string.IsNullOrWhiteSpace(restoreWeapon)
            && !string.Equals(restoreWeapon, name, StringComparison.OrdinalIgnoreCase);
        // A two-hander needs both hands: free the off-hand before wielding it,
        // and put it back once the displaced 1H weapon reclaims the weapon hand.
        bool juggleOffHand = item.IsTwoHanded
            && !string.IsNullOrWhiteSpace(restoreOffHand)
            && !string.Equals(restoreOffHand, name, StringComparison.OrdinalIgnoreCase);

        if (juggleOffHand) _wire.Send($"remove {restoreOffHand}");
        _wire.Send($"eq {name}");
        _wire.Send($"use {name}");
        if (restoreWeaponDiffers) _wire.Send($"eq {restoreWeapon}");
        if (juggleOffHand) _wire.Send($"eq {restoreOffHand}");

        _log?.Info(LogCategory,
            $"item-cast item=\"{name}\" 2h={item.IsTwoHanded} casts={item.SpellName} " +
            $"restore-weapon={restoreWeapon ?? "<none>"} restore-offhand={(juggleOffHand ? restoreOffHand : "<none>")}");
        return true;
    }

    /// <summary>The item name occupying <paramref name="slot"/> in the last
    /// inventory dump, or <c>null</c> when that slot is empty.</summary>
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
