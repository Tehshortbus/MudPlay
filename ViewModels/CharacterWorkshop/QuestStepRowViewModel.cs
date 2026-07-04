using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// One row in a quest's followable checklist parsed from its step markdown. An
// IsCheckable row is a tickable step (a []-marked line); a non-checkable row is a
// plain label line shown for context only. The section hydrates IsChecked from the
// character's persisted progress at build time (field init, so no callback fires);
// a user tick raises the supplied callback so the section can persist the change
// and, when every checkable step is ticked, flip the parent card complete.
public sealed partial class QuestStepRowViewModel : ObservableObject
{
    private readonly Action<QuestStepRowViewModel> _onToggled;

    // Checkbox order this row maps to — the persisted progress key; -1 for a non-checkable label row.
    public int Order { get; }

    // The step's display text split into render runs — plain prose plus any clickable map/room walk-to links.
    public IReadOnlyList<QuestStepSegmentViewModel> Segments { get; }

    // True when this row is a tickable step; false when it's a plain context label.
    public bool IsCheckable { get; }

    // Whether the user has ticked this step. Drives one-way completion.
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
