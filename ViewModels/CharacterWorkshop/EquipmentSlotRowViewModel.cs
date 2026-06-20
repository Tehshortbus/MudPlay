using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// One row in the Equipment Manager's slot grid: a slot's wanted
/// <see cref="ItemName"/> (empty = <c>{no change}</c>, the slot is skipped on
/// apply) and the live-filtered <see cref="AvailableItems"/> suggestion list for
/// the slot. Editing the item name invokes the supplied callback so the section
/// re-persists the set and refreshes the equipment-bonuses panel.
/// </summary>
public sealed partial class EquipmentSlotRowViewModel : ObservableObject
{
    private readonly Action<EquipmentSlotRowViewModel> _onEdited;
    private bool _suppress;

    /// <summary>The slot this row configures.</summary>
    public EquipmentSlot Slot { get; }

    /// <summary>Display label, e.g. <c>"Alt Off-Hand"</c>.</summary>
    public string Label { get; }

    /// <summary>True for the two virtual (Alt Weapon / Off-Hand) rows — no wire wear on apply.</summary>
    public bool IsVirtual { get; }

    /// <summary>Game-data item names that can occupy this slot — the field's
    /// suggestions, filtered by the character's level / class / alignment. Updated
    /// live when those change.</summary>
    [ObservableProperty] private IReadOnlyList<string> _availableItems;

    /// <summary>Item the set wants here; null / empty = <c>{no change}</c>.</summary>
    [ObservableProperty] private string? _itemName;

    public EquipmentSlotRowViewModel(
        EquipmentSlot slot, string label, bool isVirtual,
        IReadOnlyList<string> availableItems, Action<EquipmentSlotRowViewModel> onEdited)
    {
        ArgumentNullException.ThrowIfNull(availableItems);
        ArgumentNullException.ThrowIfNull(onEdited);
        Slot = slot;
        Label = label;
        IsVirtual = isVirtual;
        _availableItems = availableItems;
        _onEdited = onEdited;
    }

    /// <summary>Seed the row's item without firing the edit callback.</summary>
    public void Load(string? itemName)
    {
        _suppress = true;
        try { ItemName = itemName; }
        finally { _suppress = false; }
    }

    /// <summary>Replace the suggestion list (a level/class/alignment re-filter).</summary>
    public void SetAvailableItems(IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        AvailableItems = items;
    }

    /// <summary>Snapshot this row as a persistable set entry, or null when
    /// <c>{no change}</c> (no item) — the section persists only item-bearing slots.</summary>
    public EquipmentSlotEntry? ToEntry() =>
        string.IsNullOrWhiteSpace(ItemName) ? null : new EquipmentSlotEntry(Slot, ItemName!.Trim());

    partial void OnItemNameChanged(string? value)
    {
        if (!_suppress) _onEdited(this);
    }
}
