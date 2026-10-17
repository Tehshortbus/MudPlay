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
// rows first, growing reserves space without touching live rows.
//
// Memory: at 80 columns and 24 bytes per Cell (rough upper bound), each 1000
// rows is ~1.9 MB, so the 4000-row default holds ~7.7 MB. Users who want a
// deeper history raise it via SetCapacity and pay the memory for it explicitly.
public sealed class ScrollbackBuffer
{
    // Default ring capacity.
    public const int DefaultCapacity = 4_000;

    // One captured row. Cells is a defensive copy owned by the buffer;
    // mutating it after Append doesn't affect anything since the source row
    // was copied at append time. SoftWrapped marks a row the emulator ended by
    // wrapping a long line at the right margin (not a server LF) — LineExtractor
    // uses it to stitch the continuation back onto the logical line, and a
    // reader of the ring (the bug report's chat censor) to tell that the next
    // row is this line carrying on. Rendering doesn't care.
    public readonly record struct Row(DateTimeOffset Timestamp, Cell[] Cells, bool SoftWrapped = false);

    private Row[] _ring;
    private int _head;       // next write slot
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
        Capacity = capacity;
        _ring = new Row[capacity];
    }

    // Capture row stamped with the instant its content was written (carried from
    // the screen's per-row write stamp), NOT the moment it scrolled off. A row's
    // scroll-off time can lag its write time by seconds when it lingered on screen
    // before newer output pushed it off; stamping the write time keeps the ring's
    // timestamps monotonic with — and continuous into — the still-on-screen rows,
    // which carry the same write time. A default timestamp (a blank row that was
    // never written) falls back to the current instant. The cells are copied; the
    // caller may overwrite the source buffer immediately on return.
    // softWrapped: the screen's record that the row was left by a wrap at the
    // right margin (Row.SoftWrapped).
    public void Append(ReadOnlySpan<Cell> row, DateTimeOffset writeTime, bool softWrapped = false)
    {
        Cell[] copy = row.ToArray();
        Row entry = new(writeTime == default ? DateTimeOffset.Now : writeTime, copy, softWrapped);
        _ring[_head] = entry;
        _head = (_head + 1) % Capacity;
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
            int start = (_head - _count + Capacity) % Capacity;
            return _ring[(start + index) % Capacity];
        }
    }

    // Iterate every live row oldest → newest. Allocation-free.
    public IEnumerable<Row> Enumerate()
    {
        int start = (_head - _count + Capacity) % Capacity;
        for (int i = 0; i < _count; i++)
        {
            yield return _ring[(start + i) % Capacity];
        }
    }

    // Resize the ring to newCapacity in place. Shrinking discards the oldest
    // rows first (the newest newCapacity rows survive). Growing leaves the
    // existing rows intact and reserves empty space for future appends. No-op
    // if the capacity is unchanged.
    public void SetCapacity(int newCapacity)
    {
        if (newCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(newCapacity));
        if (newCapacity == Capacity) return;

        int keep = Math.Min(_count, newCapacity);
        Row[] next = new Row[newCapacity];

        if (keep > 0)
        {
            // Copy the newest `keep` rows oldest → newest into the front of
            // the new array. Older rows beyond `keep` are dropped.
            int srcStart = (_head - keep + Capacity) % Capacity;
            for (int i = 0; i < keep; i++)
            {
                next[i] = _ring[(srcStart + i) % Capacity];
            }
        }

        _ring = next;
        Capacity = newCapacity;
        _count = keep;
        _head = keep % newCapacity;
    }
}
