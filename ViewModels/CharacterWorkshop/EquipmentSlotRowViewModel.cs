using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// One row in the Equipment Manager's slot grid: a slot's
/// <see cref="Controlled"/> checkbox, its wanted <see cref="ItemName"/>, the
/// game-data suggestion list for the slot's item field, and a computed one-line
/// <see cref="StatPreview"/> the section writes back. Editing the checkbox or
/// item name invokes the supplied callback so the section re-persists the set
/// and refreshes the totals.
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

    /// <summary>Game-data item names that can occupy this slot — the field's suggestions.</summary>
    public IReadOnlyList<string> AvailableItems { get; }

    /// <summary>Does the owning set govern this slot? Unchecked = untouched on apply.</summary>
    [ObservableProperty] private bool _controlled;

    /// <summary>Item the set wants here; null / empty = no item.</summary>
    [ObservableProperty] private string? _itemName;

    /// <summary>Compact stat readout for the current item (section-computed).</summary>
    [ObservableProperty] private string _statPreview = string.Empty;

    public EquipmentSlotRowViewModel(
        EquipmentSlot slot, string label, bool isVirtual,
        IReadOnlyList<string> availableItems, Action<EquipmentSlotRowViewModel> onEdited)
    {
        ArgumentNullException.ThrowIfNull(availableItems);
        ArgumentNullException.ThrowIfNull(onEdited);
        Slot = slot;
        Label = label;
        IsVirtual = isVirtual;
        AvailableItems = availableItems;
        _onEdited = onEdited;
    }

    /// <summary>Seed the row from a set entry without firing the edit callback.</summary>
    public void Load(bool controlled, string? itemName)
    {
        _suppress = true;
        try
        {
            Controlled = controlled;
            ItemName = itemName;
        }
        finally { _suppress = false; }
    }

    /// <summary>Snapshot this row as a persistable set entry.</summary>
    public EquipmentSlotEntry ToEntry() => new(Slot, Controlled, string.IsNullOrWhiteSpace(ItemName) ? null : ItemName.Trim());

    partial void OnControlledChanged(bool value)
    {
        if (!_suppress) _onEdited(this);
    }

    partial void OnItemNameChanged(string? value)
    {
        if (!_suppress) _onEdited(this);
    }
}
