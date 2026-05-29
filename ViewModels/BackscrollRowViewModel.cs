using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Terminal;

namespace FujinTerm.ViewModels;

/// <summary>
/// One row in the <see cref="BackscrollViewModel"/>'s displayed list.
/// Carries the timestamp prefix string and the raw <c>Cell[]</c> the row
/// was captured at (rendered live by <see cref="Controls.CellSelectableText"/>),
/// plus an <see cref="IsFindMatch"/> flag the row template binds to apply
/// a "current find hit" background tint.
/// </summary>
public sealed partial class BackscrollRowViewModel : ObservableObject
{
    public ScrollbackBuffer.Row Source { get; }
    public string TimestampText { get; }
    public Cell[] Cells => Source.Cells;

    /// <summary>Plain-text projection of the row, used by search + export.</summary>
    public string PlainText { get; }

    /// <summary>
    /// True when this row is the current "Find next" hit. The row template
    /// styles the container background when true so the user can see which
    /// line matched without losing their text selection.
    /// </summary>
    [ObservableProperty] private bool _isFindMatch;

    public BackscrollRowViewModel(ScrollbackBuffer.Row source)
    {
        Source = source;
        TimestampText = source.Timestamp.ToLocalTime().ToString("HH:mm:ss");

        char[] chars = new char[source.Cells.Length];
        for (int i = 0; i < source.Cells.Length; i++)
            chars[i] = source.Cells[i].Char;
        PlainText = new string(chars).TrimEnd();
    }
}
