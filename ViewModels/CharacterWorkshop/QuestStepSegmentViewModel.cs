using System;
using CommunityToolkit.Mvvm.Input;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// One inline run of a quest step row: either plain prose (WalkCommand null) or a
// clickable (map/room) coordinate whose WalkCommand starts a walk-to that room. The
// view renders link runs as underlined, accent hot-text and plain runs as ordinary
// prose, so a single wrapped line can mix the two.
public sealed class QuestStepSegmentViewModel
{
    // The literal text shown for this run — prose, or the (map/room) token.
    public string Text { get; }

    // Non-null when this run is a clickable walk-to link; null for plain prose.
    public IRelayCommand? WalkCommand { get; }

    // True when this run is a clickable link (drives the view's link styling).
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
