using System;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// One entry in the Equipment Manager's left-hand set list — a fixed
/// trigger-purposed <see cref="EquipmentSet"/> (Default / Backstab / Pre-rest HP /
/// Pre-rest Mana). Exposes the set's <see cref="Name"/> and an observable
/// <see cref="Enabled"/> mirror so the Enable / Disable buttons redraw the row's
/// armed state live (the backing <see cref="EquipmentSet"/> isn't observable).
/// The section owns the toggle: it writes both this mirror and <see cref="Set"/>
/// then persists, so the row stays a passive view of the model.
/// </summary>
public sealed partial class EquipmentSetRowViewModel : ObservableObject
{
    /// <summary>The trigger-purposed set this row selects.</summary>
    public EquipmentSet Set { get; }

    /// <summary>Display name, e.g. <c>"Pre-rest HP"</c>.</summary>
    public string Name { get; }

    /// <summary>Whether automation may equip this set — mirrors
    /// <see cref="EquipmentSet.Enabled"/>; the section keeps the two in step.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _enabled;

    /// <summary>Compact armed-state badge shown next to the name.</summary>
    public string StatusText => Enabled ? "armed" : "disabled";

    public EquipmentSetRowViewModel(EquipmentSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        Set = set;
        Name = set.Name;
        _enabled = set.Enabled;
    }
}
