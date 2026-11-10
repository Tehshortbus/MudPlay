namespace MudPlay.Terminal;

// Capacity-tunable ring of rows that have scrolled off the top of the
// TerminalScreen. Each row preserves both its cells (so ANSI colours survive)
// and its write time — the instant its content was written, carried from the
// screen's per-row stamp, NOT the moment it scrolled off — so the Backscroll
// window and bug report render per-line timestamps that stay in order with the
// still-on-screen rows instead of jumping at the boundary.
//
// Single-threaded by design: the emulator drives every Append from the UI
// dispatcher's Feed path. The ring is never read off the UI thread, so no lock
// is needed.
//
// Capacity is the maximum number of scrolled-off rows the user can see and
// export. Settable at runtime via SetCapacity — shrinking drops the oldest
// rows first, growing leaves the live rows alone. The ring's array grows as rows
// arrive, up to Capacity, so a deep setting costs nothing until it fills.
//
// Memory: a row is kept without its blank tail, at 24 bytes per Cell (rough upper
// bound). A full 80-column row is ~1.9 KB; a MUD's lines run well short of the
// margin, so a typical row is half that or less. The 4000-row default holds a few
// MB; users who want a deeper history raise it via SetCapacity and pay the memory
// for it explicitly (a million rows is several hundred MB).
public sealed class ScrollbackBuffer
{
    // Default ring capacity.
    public const int DefaultCapacity = 4_000;

    // The array a ring starts with, and the least it grows by.
    private const int InitialSlots = 1_024;

    // The most rows the ring will hold, whatever it is asked for; also the Settings
    // window's ceiling. A damaged setting of two billion rows was once a 16 GB
    // allocation that failed startup.
    public const int MaxCapacity = 1_000_000;

    // One captured row. Cells is a defensive copy owned by the buffer;
    // mutating it after Append doesn't affect anything since the source row
    // was copied at append time. SoftWrapped marks a row the emulator ended by
    // wrapping a long line at the right margin (not a server LF) — LineExtractor
    // uses it to stitch the continuation back onto the logical line, and a
    // reader of the ring (the bug report's chat censor) to tell that the next
    // row is this line carrying on. Rendering doesn't care.
    public readonly record struct Row(DateTimeOffset Timestamp, Cell[] Cells, bool SoftWrapped = false);

    private Row[] _ring;
    private int _head;       // next write slot; equals _count until the array is full
    private int _count;      // live rows in the ring (≤ Capacity)

    // Capacity in rows.
    public int Capacity { get; private set; }

    // Number of rows currently held (grows up to Capacity, then plateaus).
    public int Count => _count;

    // Fired after each Append, so a transcript capture can write each row as it
    // leaves the screen.
    public event Action<Row>? RowAdded;

    public ScrollbackBuffer(int capacity = DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        capacity = Math.Min(capacity, MaxCapacity);
        Capacity = capacity;
        _ring = new Row[Math.Min(capacity, InitialSlots)];
    }

    // Capture row stamped with the instant its content was written (carried from
    // the screen's per-row write stamp), NOT the moment it scrolled off. A row's
    // scroll-off time can lag its write time by seconds when it lingered on screen
    // before newer output pushed it off; stamping the write time keeps the ring's
    // timestamps monotonic with — and continuous into — the still-on-screen rows,
    // which carry the same write time. A default timestamp (a blank row that was
    // never written) falls back to the current instant. The cells are copied; the
    // caller may overwrite the source buffer immediately on return, and only up to
    // the last cell that isn't a plain blank: the tail is what the ring's memory goes on.
    // softWrapped: the screen's record that the row was left by a wrap at the
    // right margin (Row.SoftWrapped).
    public void Append(ReadOnlySpan<Cell> row, DateTimeOffset writeTime, bool softWrapped = false)
    {
        int end = row.Length;
        while (end > 0 && row[end - 1] == Cell.Blank) end--;
        Cell[] copy = row[..end].ToArray();
        Row entry = new(writeTime == default ? DateTimeOffset.Now : writeTime, copy, softWrapped);
        if (_count == _ring.Length && _ring.Length < Capacity)
            Reallocate(Math.Min(Capacity, Math.Max(InitialSlots, _ring.Length * 2)), _count);
        _ring[_head] = entry;
        _head = (_head + 1) % _ring.Length;
        if (_count < Capacity) _count++;
        RowAdded?.Invoke(entry);
    }

    // Indexer with 0 = oldest row, Count - 1 = newest. Throws when out of
    // range so subtle off-by-one errors surface.
    public Row this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index));
            int start = (_head - _count + _ring.Length) % _ring.Length;
            return _ring[(start + index) % _ring.Length];
        }
    }

    // Iterate every live row oldest → newest. Allocation-free.
    public IEnumerable<Row> Enumerate()
    {
        int start = (_head - _count + _ring.Length) % _ring.Length;
        for (int i = 0; i < _count; i++)
        {
            yield return _ring[(start + i) % _ring.Length];
        }
    }

    // Resize the ring to newCapacity in place. Shrinking discards the oldest
    // rows first (the newest newCapacity rows survive). Growing leaves the
    // existing rows intact and reserves empty space for future appends. No-op
    // if the capacity is unchanged. A capacity past MaxCapacity gets MaxCapacity.
    public void SetCapacity(int newCapacity)
    {
        if (newCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(newCapacity));
        newCapacity = Math.Min(newCapacity, MaxCapacity);
        if (newCapacity == Capacity) return;

        int keep = Math.Min(_count, newCapacity);
        Capacity = newCapacity;
        Reallocate(Math.Min(newCapacity, Math.Max(InitialSlots, keep)), keep);
    }

    // A new array of slots holding the newest `keep` rows, oldest first, at its
    // front; older rows are dropped.
    private void Reallocate(int slots, int keep)
    {
        Row[] next = new Row[slots];
        int srcStart = (_head - keep + _ring.Length) % _ring.Length;
        for (int i = 0; i < keep; i++)
            next[i] = _ring[(srcStart + i) % _ring.Length];
        _ring = next;
        _count = keep;
        _head = keep % slots;
    }
}
