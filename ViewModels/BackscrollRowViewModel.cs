using FujinTerm.Terminal;

namespace FujinTerm.ViewModels;

/// <summary>
/// One row in the <see cref="BackscrollViewModel"/>'s displayed list.
/// Carries the timestamp prefix string and the raw <c>Cell[]</c> the row
/// was captured at; <see cref="Controls.SelectableTranscript"/> reads
/// both to compose the inline transcript display.
/// </summary>
public sealed class BackscrollRowViewModel
{
    public ScrollbackBuffer.Row Source { get; }
    public string TimestampText { get; }
    public Cell[] Cells => Source.Cells;

    /// <summary>Plain-text projection of the row, used by search + export.</summary>
    public string PlainText { get; }

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
