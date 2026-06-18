using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// One ticked-or-not step in a single-part quest's followable checklist. The
/// section hydrates <see cref="IsChecked"/> from the character's persisted
/// progress at build time (field init, so no callback fires); a user tick raises
/// the supplied callback so the section can persist the change and, when every
/// step is ticked, flip the parent card complete.
/// </summary>
public sealed partial class QuestStepRowViewModel : ObservableObject
{
    private readonly Action<QuestStepRowViewModel> _onToggled;

    /// <summary>Give-step order this row maps to — the persisted progress key.</summary>
    public int Order { get; }

    /// <summary>Human-readable step text (command / location / items), pre-formatted.</summary>
    public string Display { get; }

    /// <summary>Whether the user has ticked this step. Drives one-way completion.</summary>
    [ObservableProperty] private bool _isChecked;

    public QuestStepRowViewModel(int order, string display, bool isChecked, Action<QuestStepRowViewModel> onToggled)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(onToggled);
        Order = order;
        Display = display;
        _isChecked = isChecked;
        _onToggled = onToggled;
    }

    partial void OnIsCheckedChanged(bool value) => _onToggled(this);
}
