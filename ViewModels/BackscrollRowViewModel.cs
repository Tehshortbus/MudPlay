using FujinTerm.Terminal;

namespace FujinTerm.ViewModels;

// One row in the BackscrollViewModel's displayed list. Carries the
// timestamp prefix string and the raw Cell[] the row was captured at;
// the window feeds the cells to TranscriptInlineBuilder to compose the
// coloured inline display and renders the timestamp in the sibling gutter
// column.
public sealed class BackscrollRowViewModel
{
    private string? _plainText;

    public ScrollbackBuffer.Row Source { get; }
    public string TimestampText { get; }
    public Cell[] Cells => Source.Cells;

    // Plain-text projection of the row, used by search + export. Built on
    // first access and cached — the transcript renders straight from Cells,
    // so a row that's never searched or exported never pays for this string.
    // Deferring it keeps a full backscroll's worth of row VMs from each
    // allocating a duplicate copy of their text up front.
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
