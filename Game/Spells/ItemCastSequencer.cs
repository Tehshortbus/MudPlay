using System;
using System.Collections.Generic;
using FujinTerm.Game.Inventory;
using FujinTerm.Services;

namespace FujinTerm.Game.Spells;

/// <summary>
/// Issues the wire sequence that casts a spell <i>from an item</i>: the item
/// must be equipped to <c>use</c> it, so the engine wields the cast item, uses
/// it, then re-wields whatever weapon it displaced. Driven by
/// <see cref="CastingDirector"/> when a Bless slot holds an
/// <see cref="ItemCastToken"/> (<c>#&lt;item name&gt;</c>).
/// </summary>
/// <remarks>
/// <para>
/// Only <b>unlimited-use</b> cast items are eligible — a limited-charge item
/// would burn out on a recast loop, and the Spell Book only ever offers a token
/// for unlimited items. The displaced weapon is read from the last <c>i</c>
/// dump's <see cref="InventorySnapshot.EquippedItems"/> (the <c>Weapon Hand</c>
/// slot); when nothing is wielded, no re-wield is sent.
/// </para>
/// <para>
/// The three commands are sent back-to-back — MajorMUD queues typed input, and
/// an item <c>use</c> resolves the cast immediately so the re-wield can follow
/// without waiting. The buff <i>timer</i> (recast scheduling) is owned by
/// <see cref="CastingDirector"/>, not this sequencer; this only puts the
/// equip/use/re-equip lines on the wire.
/// </para>
/// </remarks>
public sealed class ItemCastSequencer
{
    /// <summary>LogService category — appears as <c>[ItemCast]</c> rows.</summary>
    public const string LogCategory = "ItemCast";

    /// <summary>Inventory slot label a wielded weapon occupies (matches
    /// <see cref="EquippedItem.Slot"/> normalization).</summary>
    private const string WeaponHandSlot = "Weapon Hand";

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
    /// Run the equip → use → re-equip sequence for the cast item named by
    /// <paramref name="token"/>. Returns <c>true</c> only when the lines were
    /// sent: the wire must be bound, the token must resolve to an unlimited-use
    /// cast item. The re-wield is skipped when nothing was wielded (or the held
    /// weapon IS the cast item).
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
        string? restore = CurrentWeapon();

        _wire.Send($"wield {name}");
        _wire.Send($"use {name}");
        if (!string.IsNullOrWhiteSpace(restore)
            && !string.Equals(restore, name, StringComparison.OrdinalIgnoreCase))
            _wire.Send($"wield {restore}");

        _log?.Info(LogCategory,
            $"item-cast item=\"{name}\" casts={item.SpellName} restore={restore ?? "<none>"}");
        return true;
    }

    /// <summary>The currently wielded weapon name from the last inventory dump,
    /// or <c>null</c> when nothing is in the weapon hand.</summary>
    private string? CurrentWeapon()
    {
        foreach (EquippedItem e in _inventory().EquippedItems)
            if (string.Equals(e.Slot.Trim(), WeaponHandSlot, StringComparison.OrdinalIgnoreCase))
            {
                string n = e.Name.Trim();
                return n.Length > 0 ? n : null;
            }
        return null;
    }
}
