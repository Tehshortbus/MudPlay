namespace FujinTerm.Terminal;

/// <summary>
/// The character grid that the emulator writes into and the renderer reads
/// from. Stores cells in a flat row-major array (length = cols × rows) plus
/// the cursor position and a monotonically-increasing revision counter the
/// UI can use to detect "anything changed" cheaply.
/// </summary>
public sealed class TerminalScreen
{
    private Cell[] _cells;
    private uint _revision;

    public int Cols { get; private set; }
    public int Rows { get; private set; }

    /// <summary>
    /// Fixed-capacity ring of rows that have scrolled off the top. Rows are
    /// pushed in <see cref="ScrollUp"/> whenever the scroll region starts at
    /// the top of the screen (the BBS-typical case — partial-region scrolls
    /// from vi-style apps don't lose anything to history). Default capacity
    /// is <see cref="ScrollbackBuffer.DefaultCapacity"/>; Phase 4
    /// Settings.Display will surface the knob.
    /// </summary>
    public ScrollbackBuffer Scrollback { get; } = new();

    /// <summary>Current cursor column (0-based).</summary>
    public int CursorX { get; set; }
    /// <summary>Current cursor row (0-based).</summary>
    public int CursorY { get; set; }
    /// <summary>Whether the cursor caret should be drawn.</summary>
    public bool CursorVisible { get; set; } = true;

    /// <summary>Bumped on every structural change; never wraps in practice.</summary>
    public uint Revision => _revision;

    public TerminalScreen(int cols, int rows)
    {
        Cols = cols;
        Rows = rows;
        _cells = new Cell[cols * rows];
        Array.Fill(_cells, Cell.Blank);
    }

    /// <summary>Read a cell at (x, y). Caller is responsible for bounds.</summary>
    public Cell this[int x, int y] => _cells[y * Cols + x];

    /// <summary>Get a row as a span — handy for renderers iterating left-to-right.</summary>
    public ReadOnlySpan<Cell> Row(int y) => _cells.AsSpan(y * Cols, Cols);

    /// <summary>
    /// Resize the buffer, preserving the top-left overlap and clamping the
    /// cursor inside the new dimensions.
    /// </summary>
    public void Resize(int cols, int rows)
    {
        if (cols == Cols && rows == Rows) return;
        var fresh = new Cell[cols * rows];
        Array.Fill(fresh, Cell.Blank);
        int copyCols = Math.Min(cols, Cols);
        int copyRows = Math.Min(rows, Rows);
        for (int y = 0; y < copyRows; y++)
            Array.Copy(_cells, y * Cols, fresh, y * cols, copyCols);
        _cells = fresh;
        Cols = cols;
        Rows = rows;
        CursorX = Math.Min(CursorX, cols - 1);
        CursorY = Math.Min(CursorY, rows - 1);
        Bump();
    }

    /// <summary>Write a cell, ignoring out-of-bounds writes.</summary>
    public void Put(int x, int y, Cell c)
    {
        if ((uint)x >= (uint)Cols || (uint)y >= (uint)Rows) return;
        _cells[y * Cols + x] = c;
    }

    /// <summary>Clear the entire screen to a blank cell with the given attributes.</summary>
    /// <remarks>
    /// Non-blank rows are captured into <see cref="Scrollback"/> before the
    /// clear so screen redraws (CSI 2J / BBS welcome banners / paged "Who's
    /// Online" lists / room re-renders) survive in the backscroll export.
    /// Without this, anything painted via absolute cursor positioning and
    /// wiped by ED 2 is gone forever — natural LF-at-bottom scrolling is
    /// the only other path into scrollback.
    /// </remarks>
    public void ClearAll(CellAttributes attr)
    {
        CaptureNonBlankRows(0, Rows - 1);
        var blank = new Cell(' ', attr);
        Array.Fill(_cells, blank);
        Bump();
    }

    /// <summary>Clear part of a single row [fromCol, toColInclusive].</summary>
    /// <remarks>
    /// Intentionally does NOT capture into <see cref="Scrollback"/> — single-row
    /// clears are dominated by cursor-positioning artefacts (user echo,
    /// statline rewrites, backspace overstrike) that would over-capture noise.
    /// Multi-row clears via <see cref="ClearRowsInclusive"/> and <see cref="ClearAll"/>
    /// do capture, since those are the redraw-related paths.
    /// </remarks>
    public void ClearRow(int y, int fromCol, int toColInclusive, CellAttributes attr)
    {
        if ((uint)y >= (uint)Rows) return;
        fromCol = Math.Clamp(fromCol, 0, Cols - 1);
        toColInclusive = Math.Clamp(toColInclusive, 0, Cols - 1);
        var blank = new Cell(' ', attr);
        for (int x = fromCol; x <= toColInclusive; x++)
            _cells[y * Cols + x] = blank;
    }

    /// <summary>Clear a contiguous block of rows [fromRow, toRow] inclusive.</summary>
    /// <remarks>Captures non-blank rows into <see cref="Scrollback"/> first — see <see cref="ClearAll"/>.</remarks>
    public void ClearRowsInclusive(int fromRow, int toRow, CellAttributes attr)
    {
        fromRow = Math.Clamp(fromRow, 0, Rows - 1);
        toRow = Math.Clamp(toRow, 0, Rows - 1);
        CaptureNonBlankRows(fromRow, toRow);
        var blank = new Cell(' ', attr);
        for (int y = fromRow; y <= toRow; y++)
            for (int x = 0; x < Cols; x++)
                _cells[y * Cols + x] = blank;
    }

    private void CaptureNonBlankRows(int fromRow, int toRow)
    {
        for (int y = fromRow; y <= toRow; y++)
        {
            if (!IsRowBlank(y))
                Scrollback.Append(_cells.AsSpan(y * Cols, Cols));
        }
    }

    private bool IsRowBlank(int y)
    {
        int start = y * Cols;
        for (int x = 0; x < Cols; x++)
            if (_cells[start + x].Char != ' ') return false;
        return true;
    }

    /// <summary>
    /// Scroll the rectangle rows [top..bottom] up by <paramref name="n"/>
    /// rows; the bottom <paramref name="n"/> rows are filled with blanks
    /// using <paramref name="attr"/>. Used both for normal LF-at-bottom
    /// scrolling and for explicit CSI S sequences.
    /// </summary>
    public void ScrollUp(int top, int bottom, int n, CellAttributes attr)
    {
        top = Math.Clamp(top, 0, Rows - 1);
        bottom = Math.Clamp(bottom, top, Rows - 1);
        int region = bottom - top + 1;
        n = Math.Clamp(n, 0, region);
        if (n == 0) return;
        // When the scroll region starts at row 0, the top n rows are about
        // to disappear — capture them in the scrollback ring before the
        // copy overwrites them. Partial-region scrolls (top > 0) don't
        // discard anything visible above the region, so they don't capture.
        if (top == 0)
        {
            for (int y = 0; y < n; y++)
            {
                Scrollback.Append(_cells.AsSpan(y * Cols, Cols));
            }
        }
        // Move surviving rows up.
        for (int y = top; y + n <= bottom; y++)
            Array.Copy(_cells, (y + n) * Cols, _cells, y * Cols, Cols);
        // Blank the freshly-revealed rows at the bottom of the region.
        var blank = new Cell(' ', attr);
        for (int y = bottom - n + 1; y <= bottom; y++)
            for (int x = 0; x < Cols; x++)
                _cells[y * Cols + x] = blank;
    }

    /// <summary>Inverse of <see cref="ScrollUp"/> — opens a gap at the top.</summary>
    public void ScrollDown(int top, int bottom, int n, CellAttributes attr)
    {
        top = Math.Clamp(top, 0, Rows - 1);
        bottom = Math.Clamp(bottom, top, Rows - 1);
        int region = bottom - top + 1;
        n = Math.Clamp(n, 0, region);
        if (n == 0) return;
        for (int y = bottom; y - n >= top; y--)
            Array.Copy(_cells, (y - n) * Cols, _cells, y * Cols, Cols);
        var blank = new Cell(' ', attr);
        for (int y = top; y < top + n; y++)
            for (int x = 0; x < Cols; x++)
                _cells[y * Cols + x] = blank;
    }

    /// <summary>Mark the screen as dirty so observers know to redraw.</summary>
    public void Bump() => _revision++;
}
