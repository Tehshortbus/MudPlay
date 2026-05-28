namespace FujinTerm.Terminal;

/// <summary>
/// Fixed-capacity ring of rows that have scrolled off the top of the
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
/// Size: defaults to 10 000 rows. Phase 4 Settings.Display will surface
/// the knob; the size is constructor-injected so a future swap just
/// constructs a fresh buffer and copies the live rows across.
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

    private readonly Row[] _ring;
    private int _head;       // next write slot
    private int _count;      // live rows in the ring (≤ Capacity)

    /// <summary>Capacity in rows.</summary>
    public int Capacity { get; }

    /// <summary>Number of rows currently held (grows up to Capacity, then plateaus).</summary>
    public int Count => _count;

    /// <summary>Fired after each <see cref="Append"/>. Used by the Backscroll window's tail follower.</summary>
    public event Action<Row>? RowAdded;

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
}
