using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// One row in a quest's followable checklist parsed from its step markdown. A
/// <see cref="IsCheckable"/> row is a tickable step (a <c>[]</c>-marked line); a
/// non-checkable row is a plain label line shown for context only. The section
/// hydrates <see cref="IsChecked"/> from the character's persisted progress at
/// build time (field init, so no callback fires); a user tick raises the supplied
/// callback so the section can persist the change and, when every checkable step
/// is ticked, flip the parent card complete.
/// </summary>
public sealed partial class QuestStepRowViewModel : ObservableObject
{
    private readonly Action<QuestStepRowViewModel> _onToggled;

    /// <summary>Checkbox order this row maps to — the persisted progress key; <c>-1</c> for a non-checkable label row.</summary>
    public int Order { get; }

    /// <summary>The step's display text split into render runs — plain prose plus any clickable map/room walk-to links.</summary>
    public IReadOnlyList<QuestStepSegmentViewModel> Segments { get; }

    /// <summary>True when this row is a tickable step; false when it's a plain context label.</summary>
    public bool IsCheckable { get; }

    /// <summary>Whether the user has ticked this step. Drives one-way completion.</summary>
    [ObservableProperty] private bool _isChecked;

    public QuestStepRowViewModel(int order, IReadOnlyList<QuestStepSegmentViewModel> segments, bool isChecked, bool isCheckable,
                                 Action<QuestStepRowViewModel> onToggled)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(onToggled);
        Order = order;
        Segments = segments;
        IsCheckable = isCheckable;
        _isChecked = isChecked;
        _onToggled = onToggled;
    }

    partial void OnIsCheckedChanged(bool value) => _onToggled(this);
}
