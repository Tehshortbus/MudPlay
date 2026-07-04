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
    private string? _plainText;

    public ScrollbackBuffer.Row Source { get; }
    public string TimestampText { get; }
    public Cell[] Cells => Source.Cells;

    /// <summary>
    /// Plain-text projection of the row, used by search + export. Built on
    /// first access and cached — the transcript renders straight from
    /// <see cref="Cells"/>, so a row that's never searched or exported never
    /// pays for this string. Deferring it keeps a full backscroll's worth of
    /// row VMs from each allocating a duplicate copy of their text up front.
    /// </summary>
    public string PlainText => _plainText ??= BuildPlainText();

    public BackscrollRowViewModel(ScrollbackBuffer.Row source)
    {
        Source = source;
        TimestampText = source.Timestamp.ToLocalTime().ToString("HH:mm:ss");
    }

    private string BuildPlainText()
    {
        char[] chars = new char[Source.Cells.Length];
        for (int i = 0; i < Source.Cells.Length; i++)
            chars[i] = Source.Cells[i].Char;
        return new string(chars).TrimEnd();
    }
}
