using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// One row in the Equipment Manager's slot grid: a slot's wanted ItemName (empty =
// {no change}, the slot is skipped on apply) and the live-filtered AvailableItems
// suggestion list for the slot. Editing the item name invokes the supplied
// callback so the section re-persists the set and refreshes the equipment-bonuses
// panel.
public sealed partial class EquipmentSlotRowViewModel : ObservableObject
{
    private readonly Action<EquipmentSlotRowViewModel> _onEdited;
    private bool _suppress;

    // The slot this row configures.
    public EquipmentSlot Slot { get; }

    // Display label, e.g. "Alt Off-Hand".
    public string Label { get; }

    // True for the two virtual (Alt Weapon / Off-Hand) rows — no wire wear on apply.
    public bool IsVirtual { get; }

    // Game-data item names that can occupy this slot — the field's suggestions,
    // filtered by the character's level / class / alignment. Updated live when
    // those change.
    [ObservableProperty] private IReadOnlyList<string> _availableItems;

    // Item the set wants here; null / empty = {no change}.
    [ObservableProperty] private string? _itemName;

    // Whether this row applies to the currently-selected set (drives its
    // visibility). The section hides the virtual Alt rows for every set except
    // Default — the alternate-weapon swap only happens during normal weapon combat
    // (backstab fires on the opening round; pre-rest sets trigger out of combat).
    // Defaults true; recomputed on each set switch.
    [ObservableProperty] private bool _applies = true;

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

    // Seed the row's item without firing the edit callback.
    public void Load(string? itemName)
    {
        _suppress = true;
        try { ItemName = itemName; }
        finally { _suppress = false; }
    }

    // Replace the suggestion list (a level/class/alignment re-filter).
    public void SetAvailableItems(IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        AvailableItems = items;
    }

    // Snapshot this row as a persistable set entry, or null when {no change} (no
    // item) — the section persists only item-bearing slots.
    public EquipmentSlotEntry? ToEntry() =>
        string.IsNullOrWhiteSpace(ItemName) ? null : new EquipmentSlotEntry(Slot, ItemName!.Trim());

    partial void OnItemNameChanged(string? value)
    {
        if (!_suppress) _onEdited(this);
    }
}
