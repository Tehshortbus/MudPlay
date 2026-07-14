namespace FujinTerm.Terminal;

// Capacity-tunable ring of rows that have scrolled off the top of the
// TerminalScreen. Each row preserves both its cells (so ANSI colours survive)
// and the timestamp it was captured at (so the Backscroll window can render
// per-line timestamps without inventing them).
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
    // uses it to stitch the continuation back onto the logical line. The
    // scrollback ring never sets it (rendering doesn't care); only the
    // LineCompleted event path populates it.
    public readonly record struct Row(DateTimeOffset Timestamp, Cell[] Cells, bool SoftWrapped = false);

    private Row[] _ring;
    private int _head;       // next write slot
    private int _count;      // live rows in the ring (≤ Capacity)

    // Capacity in rows.
    public int Capacity { get; private set; }

    // Number of rows currently held (grows up to Capacity, then plateaus).
    public int Count => _count;

    // Fired after each Append. Used by the Backscroll window's tail follower.
    public event Action<Row>? RowAdded;

    // Fired after SetCapacity changes Capacity. Backscroll consumers re-bind
    // their views since cached indices may now reference different rows (or
    // rows that no longer exist after a shrink).
    public event Action? CapacityChanged;

    public ScrollbackBuffer(int capacity = DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _ring = new Row[capacity];
    }

    // Capture row with the current wall-clock timestamp. The cells are copied;
    // the caller may overwrite the source buffer immediately on return.
    public void Append(ReadOnlySpan<Cell> row)
    {
        Cell[] copy = row.ToArray();
        Row entry = new(DateTimeOffset.Now, copy);
        _ring[_head] = entry;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
        RowAdded?.Invoke(entry);
    }

    // Indexer with 0 = oldest row, Count - 1 = newest. Throws when out of
    // range so subtle off-by-one errors surface in tests.
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

    // Drop every captured row. RowAdded does NOT fire.
    public void Clear()
    {
        Array.Clear(_ring);
        _head = 0;
        _count = 0;
    }

    // Resize the ring to newCapacity in place. Shrinking discards the oldest
    // rows first (the newest newCapacity rows survive). Growing leaves the
    // existing rows intact and reserves empty space for future appends. No-op
    // if the capacity is unchanged. Fires CapacityChanged on success.
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

        CapacityChanged?.Invoke();
    }
}
