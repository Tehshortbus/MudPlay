using System;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// One entry in the Equipment Manager's left-hand list. Normally a fixed
/// trigger-purposed <see cref="EquipmentSet"/> (Default / Backstab / Pre-rest HP /
/// Pre-rest Mana); exposes the set's <see cref="Name"/> and an observable
/// <see cref="Enabled"/> mirror so the Enable / Disable buttons redraw the row's
/// enabled state live (the backing <see cref="EquipmentSet"/> isn't observable).
/// The section owns the toggle: it writes both this mirror and <see cref="Set"/>
/// then persists, so the row stays a passive view of the model.
/// </summary>
/// <remarks>
/// The list also carries one synthetic <see cref="IsInventory"/> row (no backing
/// set, no enabled badge) that switches the right pane to the carried-item view
/// instead of a slot grid — constructed via the string overload.
/// </remarks>
public sealed partial class EquipmentSetRowViewModel : ObservableObject
{
    /// <summary>The trigger-purposed set this row selects, or <c>null</c> for the
    /// synthetic Inventory row.</summary>
    public EquipmentSet? Set { get; }

    /// <summary>Display name, e.g. <c>"Pre-rest HP"</c> or <c>"Inventory"</c>.</summary>
    public string Name { get; }

    /// <summary>True for the synthetic Inventory row — it selects the carried-item
    /// view rather than a gear set, so it has no <see cref="Set"/> and no badge.</summary>
    public bool IsInventory { get; }

    /// <summary>Whether automation may equip this set — mirrors
    /// <see cref="EquipmentSet.Enabled"/>; the section keeps the two in step.
    /// Always false / unused for the Inventory row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _enabled;

    /// <summary>Compact enabled-state badge shown next to the name; blank for the
    /// Inventory row.</summary>
    public string StatusText => IsInventory ? string.Empty : (Enabled ? "enabled" : "disabled");

    public EquipmentSetRowViewModel(EquipmentSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        Set = set;
        Name = set.Name;
        _enabled = set.Enabled;
    }

    /// <summary>Construct the synthetic Inventory row (no backing set).</summary>
    public EquipmentSetRowViewModel(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        IsInventory = true;
    }
}
