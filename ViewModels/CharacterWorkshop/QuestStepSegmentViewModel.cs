using System;
using CommunityToolkit.Mvvm.Input;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// One inline run of a quest step row: either plain prose (<see cref="WalkCommand"/>
/// null) or a clickable <c>(map/room)</c> coordinate whose <see cref="WalkCommand"/>
/// starts a walk-to that room. The view renders link runs as underlined, accent
/// hot-text and plain runs as ordinary prose, so a single wrapped line can mix the two.
/// </summary>
public sealed class QuestStepSegmentViewModel
{
    /// <summary>The literal text shown for this run — prose, or the <c>(map/room)</c> token.</summary>
    public string Text { get; }

    /// <summary>Non-null when this run is a clickable walk-to link; null for plain prose.</summary>
    public IRelayCommand? WalkCommand { get; }

    /// <summary>True when this run is a clickable link (drives the view's link styling).</summary>
    public bool IsLink => WalkCommand is not null;

    public QuestStepSegmentViewModel(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
    }

    public QuestStepSegmentViewModel(string text, IRelayCommand walkCommand)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(walkCommand);
        Text = text;
        WalkCommand = walkCommand;
    }
}
