using FujinTerm.Game.Map;

namespace FujinTerm.ViewModels.Navigation;

/// <summary>
/// One row in the CURRENT NAV section's list. Has the same shape for
/// walking, looping, and auto-lair so the right rail can bind to a
/// single ItemsControl. <see cref="Status"/> drives the colour /
/// strike-through styling; <see cref="HasRemove"/> shows the X button
/// only on rows that can be cancelled inline (Auto-Lair marks).
/// </summary>
public sealed class CurrentNavRowViewModel
{
    public int Index { get; }
    public string Label { get; }
    public string? SubLabel { get; }
    public CurrentNavRowStatus Status { get; }
    public RoomKey? RemoveKey { get; }

    /// <summary>
    /// When non-null, the row shows an Edit (✎) button alongside
    /// Remove. Currently used by the Auto-Lair rows to expose a
    /// per-marker timer-override dialog; loop / walk steps don't
    /// have a per-step edit affordance so they leave this null.
    /// </summary>
    public RoomKey? EditKey { get; }

    public bool IsCompleted => Status == CurrentNavRowStatus.Completed;
    public bool IsCurrent   => Status == CurrentNavRowStatus.Current;
    public bool IsUpcoming  => Status == CurrentNavRowStatus.Upcoming;
    public bool IsReady     => Status == CurrentNavRowStatus.Ready;
    public bool HasRemove   => RemoveKey is not null;
    public bool HasEdit     => EditKey   is not null;

    public CurrentNavRowViewModel(
        int index, string label,
        CurrentNavRowStatus status,
        string? subLabel = null,
        RoomKey? removeKey = null,
        RoomKey? editKey = null)
    {
        Index = index;
        Label = label;
        Status = status;
        SubLabel = subLabel;
        RemoveKey = removeKey;
        EditKey = editKey;
    }
}

/// <summary>
/// Visual state for a <see cref="CurrentNavRowViewModel"/>. Walking /
/// looping use Completed / Current / Upcoming for the step list;
/// Auto-Lair uses Ready / Upcoming for the marked-lair list.
/// </summary>
public enum CurrentNavRowStatus
{
    Completed = 0,
    Current   = 1,
    Upcoming  = 2,
    Ready     = 3,
}
