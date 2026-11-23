namespace MudPlay.Terminal;

// The character grid that the emulator writes into and the renderer reads
// from. Stores cells in a flat row-major array (length = cols × rows) plus the
// cursor position and a monotonically-increasing revision counter the UI can
// use to detect "anything changed" cheaply.
public sealed class TerminalScreen
{
    private Cell[] _cells;
    // Per-row wall-clock stamp of the most recent write into each row, kept in
    // lockstep with _cells across every grid mutation (Put / scroll / clear /
    // resize). default(DateTimeOffset) means "never written" (a blank row). The
    // live screen is a mutable grid with no natural per-row time, so the
    // transcript snapshot reads these to timestamp on-screen rows instead of
    // leaving them blank — a scrolled-off row already carries its own capture
    // time in the scrollback ring, but the rows still visible did not until now.
    private DateTimeOffset[] _rowStamps;
    // Per-row: the emulator left this row by wrapping a long line at the right
    // margin, so the row below carries on the same line. Kept in lockstep with
    // _rowStamps and handed to the scrollback with the row, so a reader of the
    // transcript can tell a continuation from a new line (a full row proves
    // nothing: a line can end exactly at the margin). Set per line, never per
    // cell.
    private bool[] _rowSoftWrapped;
    private uint _revision;

    public int Cols { get; private set; }
    public int Rows { get; private set; }

    // The largest grid the screen will make, whatever it is asked for. The cells are
    // one array of cols × rows, so a damaged 100000 × 100000 setting overflowed the
    // product and asked for some 34 GB. A size past these is cut to them.
    public const int MaxCols = 1000;
    public const int MaxRows = 1000;

    // Wall-clock time of the Feed batch currently being processed. The emulator
    // sets this once per Feed so per-cell Put writes can stamp their row without
    // each calling DateTimeOffset.Now on the parse hot path; all cells in one
    // server read share the batch's instant.
    public DateTimeOffset FeedTimestamp { get; set; }

    // Fixed-capacity ring of rows that have scrolled off the top. Rows are
    // pushed in ScrollUp whenever the scroll region starts at the top of the
    // screen (the BBS-typical case — partial-region scrolls from vi-style apps
    // don't lose anything to history). Default capacity is
    // ScrollbackBuffer.DefaultCapacity; Settings → BBS + Display's Scrollback
    // (lines) resizes it live.
    public ScrollbackBuffer Scrollback { get; } = new();

    // Current cursor column (0-based).
    public int CursorX { get; set; }
    // Current cursor row (0-based).
    public int CursorY { get; set; }
    // Whether the cursor caret should be drawn.
    public bool CursorVisible { get; set; } = true;

    // Bumped on every structural change; never wraps in practice.
    public uint Revision => _revision;

    public TerminalScreen(int cols, int rows)
    {
        cols = Math.Clamp(cols, 1, MaxCols);
        rows = Math.Clamp(rows, 1, MaxRows);
        Cols = cols;
        Rows = rows;
        _cells = new Cell[cols * rows];
        Array.Fill(_cells, Cell.Blank);
        _rowStamps = new DateTimeOffset[rows];
        _rowSoftWrapped = new bool[rows];
    }

    // Whether row y was left by a wrap at the right margin (the row below
    // continues its line).
    public bool RowSoftWrapped(int y) => (uint)y < (uint)Rows && _rowSoftWrapped[y];

    // The emulator's word on how row y was left: by a wrap, or by a real line end.
    public void SetRowSoftWrapped(int y, bool wrapped)
    {
        if ((uint)y < (uint)Rows) _rowSoftWrapped[y] = wrapped;
    }

    // Read a cell at (x, y). Caller is responsible for bounds.
    public Cell this[int x, int y] => _cells[y * Cols + x];

    // Get a row as a span — handy for renderers iterating left-to-right.
    public ReadOnlySpan<Cell> Row(int y) => _cells.AsSpan(y * Cols, Cols);

    // The write time of row y, or null if the row was never written (a blank
    // row): what a reader of the transcript stamps a live-screen row with.
    public DateTimeOffset? RowTimestamp(int y) =>
        (uint)y < (uint)Rows && _rowStamps[y] != default ? _rowStamps[y] : null;

    // Resize the buffer. Growing rows adds blank lines at the bottom;
    // shrinking rows drops the top-most lines (so the newest server content
    // stays visible) and pushes them into Scrollback so the user can still
    // find them via the Backscroll window. Columns always preserve the left
    // edge. A size outside 1 to MaxCols × 1 to MaxRows is cut into it.
    public void Resize(int cols, int rows)
    {
        cols = Math.Clamp(cols, 1, MaxCols);
        rows = Math.Clamp(rows, 1, MaxRows);
        if (cols == Cols && rows == Rows) return;
        var fresh = new Cell[cols * rows];
        Array.Fill(fresh, Cell.Blank);
        var freshStamps = new DateTimeOffset[rows];
        var freshWrapped = new bool[rows];

        int copyCols = Math.Min(cols, Cols);
        int copyRows = Math.Min(rows, Rows);
        // When shrinking row count, drop the top-most (oldest) rows so the
        // bottom (most-recent) content stays anchored. When growing, this
        // is zero and the existing rows stay at y=0 with blank rows below.
        int dropFromTop = Math.Max(0, Rows - rows);

        // Push the rows we're about to lose into scrollback so the user
        // can still scrub them in the Backscroll window.
        for (int y = 0; y < dropFromTop; y++)
            Scrollback.Append(_cells.AsSpan(y * Cols, Cols), _rowStamps[y], _rowSoftWrapped[y]);

        for (int y = 0; y < copyRows; y++)
        {
            Array.Copy(_cells, (y + dropFromTop) * Cols, fresh, y * cols, copyCols);
            freshStamps[y] = _rowStamps[y + dropFromTop];
            freshWrapped[y] = _rowSoftWrapped[y + dropFromTop];
        }

        _cells = fresh;
        _rowStamps = freshStamps;
        _rowSoftWrapped = freshWrapped;
        Cols = cols;
        Rows = rows;
        CursorX = Math.Min(CursorX, cols - 1);
        // Shift cursor up by the number of dropped rows so it tracks the
        // content; clamp to the new viewport.
        CursorY = Math.Max(0, Math.Min(CursorY - dropFromTop, rows - 1));
        Bump();
    }

    // Write a cell, ignoring out-of-bounds writes.
    public void Put(int x, int y, Cell c)
    {
        if ((uint)x >= (uint)Cols || (uint)y >= (uint)Rows) return;
        _cells[y * Cols + x] = c;
        _rowStamps[y] = FeedTimestamp;
    }

    // Clear the entire screen to a blank cell with the given attributes.
    //
    // Rows are captured into Scrollback before the clear so screen redraws
    // (CSI 2J / BBS welcome banners / paged "Who's Online" lists / room
    // re-renders) survive in the backscroll export. Without this, anything
    // painted via absolute cursor positioning and wiped by ED 2 is gone
    // forever — natural LF-at-bottom scrolling is the only other path into
    // scrollback. Trailing rows below the last row of content are dropped
    // (they're unused screen, not server output).
    public void ClearAll(CellAttributes attr)
    {
        CaptureUpToLastNonBlank(0, Rows - 1);
        var blank = new Cell(' ', attr);
        Array.Fill(_cells, blank);
        Array.Clear(_rowStamps);
        Array.Clear(_rowSoftWrapped);
        Bump();
    }

    // Clear part of a single row [fromCol, toColInclusive].
    //
    // Intentionally does NOT capture into Scrollback — single-row clears are
    // dominated by cursor-positioning artefacts (user echo, statline rewrites,
    // backspace overstrike) that would over-capture noise. Multi-row clears
    // via ClearRowsInclusive and ClearAll do capture, since those are the
    // redraw-related paths.
    public void ClearRow(int y, int fromCol, int toColInclusive, CellAttributes attr)
    {
        if ((uint)y >= (uint)Rows) return;
        fromCol = Math.Clamp(fromCol, 0, Cols - 1);
        toColInclusive = Math.Clamp(toColInclusive, 0, Cols - 1);
        var blank = new Cell(' ', attr);
        for (int x = fromCol; x <= toColInclusive; x++)
            _cells[y * Cols + x] = blank;
        // Erased to the margin: whatever wrapped from here is no longer this
        // row's line. Erased from its start: this row no longer carries on the
        // line of the row above (a prompt rewritten in place does this).
        if (toColInclusive == Cols - 1) _rowSoftWrapped[y] = false;
        if (fromCol == 0 && y > 0) _rowSoftWrapped[y - 1] = false;
    }

    // Clear a contiguous block of rows [fromRow, toRow] inclusive. Captures
    // rows up to the last non-blank row into Scrollback first — see ClearAll.
    public void ClearRowsInclusive(int fromRow, int toRow, CellAttributes attr)
    {
        fromRow = Math.Clamp(fromRow, 0, Rows - 1);
        toRow = Math.Clamp(toRow, 0, Rows - 1);
        CaptureUpToLastNonBlank(fromRow, toRow);
        EraseRows(fromRow, toRow, attr);
    }

    // Blank a block of rows with nothing kept in the scrollback.
    public void EraseRows(int fromRow, int toRow, CellAttributes attr)
    {
        fromRow = Math.Clamp(fromRow, 0, Rows - 1);
        toRow = Math.Clamp(toRow, 0, Rows - 1);
        var blank = new Cell(' ', attr);
        for (int y = fromRow; y <= toRow; y++)
        {
            for (int x = 0; x < Cols; x++)
                _cells[y * Cols + x] = blank;
            _rowStamps[y] = default;
            _rowSoftWrapped[y] = false;
        }
    }

    private void CaptureUpToLastNonBlank(int fromRow, int toRow)
    {
        // Find the last non-blank row in the range; rows beyond it are
        // unused screen padding the server never touched, so they don't
        // belong in history. Blank rows mid-content (intentional spacing
        // from server output) ARE captured.
        int lastNonBlank = fromRow - 1;
        for (int y = fromRow; y <= toRow; y++)
            if (!IsRowBlank(y)) lastNonBlank = y;

        for (int y = fromRow; y <= lastNonBlank; y++)
        {
            Scrollback.Append(_cells.AsSpan(y * Cols, Cols), _rowStamps[y], _rowSoftWrapped[y]);
        }
    }

    private bool IsRowBlank(int y)
    {
        int start = y * Cols;
        for (int x = 0; x < Cols; x++)
            if (_cells[start + x].Char != ' ') return false;
        return true;
    }

    // Scroll the rectangle rows [top..bottom] up by n rows; the bottom n rows
    // are filled with blanks using attr. Used both for normal LF-at-bottom
    // scrolling and for explicit CSI S sequences.
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
        // No blank-row filter: every row that scrolled off the terminal is
        // a row the user saw, so it belongs in scrollback regardless of
        // whether the server happened to write content into it.
        if (top == 0)
        {
            for (int y = 0; y < n; y++)
            {
                Scrollback.Append(_cells.AsSpan(y * Cols, Cols), _rowStamps[y], _rowSoftWrapped[y]);
            }
        }
        // Move surviving rows up.
        for (int y = top; y + n <= bottom; y++)
        {
            Array.Copy(_cells, (y + n) * Cols, _cells, y * Cols, Cols);
            _rowStamps[y] = _rowStamps[y + n];
            _rowSoftWrapped[y] = _rowSoftWrapped[y + n];
        }
        // Blank the freshly-revealed rows at the bottom of the region.
        var blank = new Cell(' ', attr);
        for (int y = bottom - n + 1; y <= bottom; y++)
        {
            for (int x = 0; x < Cols; x++)
                _cells[y * Cols + x] = blank;
            _rowStamps[y] = default;
            _rowSoftWrapped[y] = false;
        }
    }

    // Inverse of ScrollUp — opens a gap at the top.
    public void ScrollDown(int top, int bottom, int n, CellAttributes attr)
    {
        top = Math.Clamp(top, 0, Rows - 1);
        bottom = Math.Clamp(bottom, top, Rows - 1);
        int region = bottom - top + 1;
        n = Math.Clamp(n, 0, region);
        if (n == 0) return;
        for (int y = bottom; y - n >= top; y--)
        {
            Array.Copy(_cells, (y - n) * Cols, _cells, y * Cols, Cols);
            _rowStamps[y] = _rowStamps[y - n];
            _rowSoftWrapped[y] = _rowSoftWrapped[y - n];
        }
        var blank = new Cell(' ', attr);
        for (int y = top; y < top + n; y++)
        {
            for (int x = 0; x < Cols; x++)
                _cells[y * Cols + x] = blank;
            _rowStamps[y] = default;
            _rowSoftWrapped[y] = false;
        }
    }

    // Mark the screen as dirty so observers know to redraw.
    public void Bump() => _revision++;
}
