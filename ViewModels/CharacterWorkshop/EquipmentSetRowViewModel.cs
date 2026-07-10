using System;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// One entry in the Equipment Manager's left-hand list: a fixed trigger-purposed
// EquipmentSet (Default / Backstab / Pre-rest HP / Pre-rest Mana). Exposes the
// set's Name and an observable Enabled mirror so the Enable / Disable buttons
// redraw the row's enabled state live (the backing EquipmentSet isn't
// observable). The section owns the toggle: it writes both this mirror and Set
// then persists, so the row stays a passive view of the model.
public sealed partial class EquipmentSetRowViewModel : ObservableObject
{
    // The trigger-purposed set this row selects.
    public EquipmentSet Set { get; }

    // Display name, e.g. "Pre-rest HP".
    public string Name { get; }

    // Whether automation may equip this set — mirrors EquipmentSet.Enabled; the
    // section keeps the two in step.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _enabled;

    // Compact enabled-state badge shown next to the name.
    public string StatusText => Enabled ? "enabled" : "disabled";

    public EquipmentSetRowViewModel(EquipmentSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        Set = set;
        Name = set.Name;
        _enabled = set.Enabled;
    }
}
