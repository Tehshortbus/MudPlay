using System;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// One entry in the Equipment Manager's left-hand list. Normally a fixed
// trigger-purposed EquipmentSet (Default / Backstab / Pre-rest HP / Pre-rest
// Mana); exposes the set's Name and an observable Enabled mirror so the Enable /
// Disable buttons redraw the row's enabled state live (the backing EquipmentSet
// isn't observable). The section owns the toggle: it writes both this mirror and
// Set then persists, so the row stays a passive view of the model.
//
// The list also carries one synthetic IsInventory row (no backing set, no enabled
// badge) that switches the right pane to the carried-item view instead of a slot
// grid — constructed via the string overload.
public sealed partial class EquipmentSetRowViewModel : ObservableObject
{
    // The trigger-purposed set this row selects, or null for the synthetic
    // Inventory row.
    public EquipmentSet? Set { get; }

    // Display name, e.g. "Pre-rest HP" or "Inventory".
    public string Name { get; }

    // True for the synthetic Inventory row — it selects the carried-item view
    // rather than a gear set, so it has no Set and no badge.
    public bool IsInventory { get; }

    // Whether automation may equip this set — mirrors EquipmentSet.Enabled; the
    // section keeps the two in step. Always false / unused for the Inventory row.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _enabled;

    // Compact enabled-state badge shown next to the name; blank for the Inventory row.
    public string StatusText => IsInventory ? string.Empty : (Enabled ? "enabled" : "disabled");

    public EquipmentSetRowViewModel(EquipmentSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        Set = set;
        Name = set.Name;
        _enabled = set.Enabled;
    }

    // Construct the synthetic Inventory row (no backing set).
    public EquipmentSetRowViewModel(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        IsInventory = true;
    }
}
