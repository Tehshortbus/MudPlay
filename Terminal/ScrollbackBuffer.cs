namespace FujinTerm.Terminal;

/// <summary>
/// Capacity-tunable ring of rows that have scrolled off the top of the
/// <see cref="TerminalScreen"/>. Each row preserves both its cells (so
/// ANSI colours survive) and the timestamp it was captured at (so the
/// Backscroll window can render per-line timestamps without inventing
/// them).
/// </summary>
/// <remarks>
/// <para>
/// Single-threaded by design: the emulator drives every <see cref="Append"/>
/// from the UI dispatcher's <c>Feed</c> path. The ring is never read off
/// the UI thread, so no lock is needed.
/// </para>
/// <para>
/// Capacity is the maximum number of scrolled-off rows the user can see
/// and export. Settable at runtime via <see cref="SetCapacity"/> —
/// shrinking drops the oldest rows first, growing reserves space without
/// touching live rows.
/// </para>
/// <para>
/// Memory: at 80 columns and 24 bytes per <see cref="Cell"/> (rough
/// upper bound), 10 000 rows is ~19 MB. Acceptable for a desktop client.
/// </para>
/// </remarks>
public sealed class ScrollbackBuffer
{
    /// <summary>Default ring capacity.</summary>
    public const int DefaultCapacity = 10_000;

    /// <summary>
    /// One captured row. <see cref="Cells"/> is a defensive copy owned by
    /// the buffer; mutating it after <see cref="Append"/> doesn't affect
    /// anything since the source row was copied at append time.
    /// </summary>
    public readonly record struct Row(DateTimeOffset Timestamp, Cell[] Cells);

    private Row[] _ring;
    private int _head;       // next write slot
    private int _count;      // live rows in the ring (≤ Capacity)

    /// <summary>Capacity in rows.</summary>
    public int Capacity { get; private set; }

    /// <summary>Number of rows currently held (grows up to Capacity, then plateaus).</summary>
    public int Count => _count;

    /// <summary>Fired after each <see cref="Append"/>. Used by the Backscroll window's tail follower.</summary>
    public event Action<Row>? RowAdded;

    /// <summary>
    /// Fired after <see cref="SetCapacity"/> changes <see cref="Capacity"/>.
    /// Backscroll consumers re-bind their views since cached indices may
    /// now reference different rows (or rows that no longer exist after
    /// a shrink).
    /// </summary>
    public event Action? CapacityChanged;

    public ScrollbackBuffer(int capacity = DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _ring = new Row[capacity];
    }

    /// <summary>
    /// Capture <paramref name="row"/> with the current wall-clock timestamp.
    /// The cells are copied; the caller may overwrite the source buffer
    /// immediately on return.
    /// </summary>
    public void Append(ReadOnlySpan<Cell> row)
    {
        Cell[] copy = row.ToArray();
        Row entry = new(DateTimeOffset.Now, copy);
        _ring[_head] = entry;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
        RowAdded?.Invoke(entry);
    }

    /// <summary>
    /// Indexer with <c>0</c> = oldest row, <c>Count - 1</c> = newest. Throws
    /// when out of range so subtle off-by-one errors surface in tests.
    /// </summary>
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

    /// <summary>Iterate every live row oldest → newest. Allocation-free.</summary>
    public IEnumerable<Row> Enumerate()
    {
        int start = (_head - _count + Capacity) % Capacity;
        for (int i = 0; i < _count; i++)
        {
            yield return _ring[(start + i) % Capacity];
        }
    }

    /// <summary>Drop every captured row. <see cref="RowAdded"/> does NOT fire.</summary>
    public void Clear()
    {
        Array.Clear(_ring);
        _head = 0;
        _count = 0;
    }

    /// <summary>
    /// Resize the ring to <paramref name="newCapacity"/> in place.
    /// Shrinking discards the oldest rows first (newest <paramref name="newCapacity"/>
    /// rows survive). Growing leaves the existing rows intact and
    /// reserves empty space for future appends. No-op if the capacity
    /// is unchanged. Fires <see cref="CapacityChanged"/> on success.
    /// </summary>
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
