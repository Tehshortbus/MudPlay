namespace MudPlay.Services;

// Fixed-capacity byte ring that retains the tail of the cleaned display stream
// from the live Net.TelnetClient. Drives the Wire Inspector window and is the
// obvious shared source for any "show me what the server just sent" diagnostic.
//
// Thread-safe: a single internal lock guards the ring. Append is safe to call
// from the Telnet read loop; Snapshot allocates a fresh array per call, so the
// UI's render tick can safely take a stable copy.
public sealed class WireBuffer
{
    // 64 KB default. Older bytes scroll out automatically.
    public const int DefaultCapacity = 64 * 1024;

    private readonly object _gate = new();
    private readonly byte[] _ring;
    private int _head;       // next write slot
    private int _count;      // bytes alive in the ring (<= Capacity)
    private long _totalBytes;

    // Ring capacity in bytes.
    public int Capacity { get; }

    // Total bytes ever Appended since construction.
    public long TotalBytes
    {
        get { lock (_gate) { return _totalBytes; } }
    }

    public WireBuffer(int capacity = DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _ring = new byte[capacity];
    }

    // Append data; oldest bytes drop off when full.
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;

        lock (_gate)
        {
            NoteArrival(_totalBytes);
            _totalBytes += data.Length;

            // If the incoming run is bigger than the ring, only the tail
            // survives. Trim and rewind so the loop below stays cheap.
            if (data.Length >= Capacity)
            {
                data = data[^Capacity..];
                _head = 0;
                _count = Capacity;
                data.CopyTo(_ring);
            }
            else
            {
                int firstRun = Math.Min(data.Length, Capacity - _head);
                data[..firstRun].CopyTo(_ring.AsSpan(_head, firstRun));
                if (firstRun < data.Length)
                {
                    data[firstRun..].CopyTo(_ring.AsSpan(0, data.Length - firstRun));
                }
                _head = (_head + data.Length) % Capacity;
                _count = Math.Min(Capacity, _count + data.Length);
            }
        }

    }

    // Copy out the live bytes oldest → newest as a fresh array.
    public byte[] Snapshot()
    {
        lock (_gate)
        {
            byte[] result = new byte[_count];
            if (_count == 0) return result;

            int start = (_head - _count + Capacity) % Capacity;
            int firstRun = Math.Min(_count, Capacity - start);
            Array.Copy(_ring, start, result, 0, firstRun);
            if (firstRun < _count)
            {
                Array.Copy(_ring, 0, result, firstRun, _count - firstRun);
            }
            return result;
        }
    }

    // Drop every byte. Does not reset TotalBytes.
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _head = 0;
            _count = 0;
            _marks.Clear();
            LastClearedAt = DateTimeOffset.Now;
        }
    }

    // When the buffer was last emptied by hand (the Wire Inspector's Clear).
    public DateTimeOffset? LastClearedAt { get; private set; }

    // ----- Arrival times --------------------------------------------------
    // The wire has no clock of its own, so nothing in it could be lined up with
    // the log or the scrollback. Each read's arrival is noted against the byte it
    // began at, at most once per MarkSpacing: enough to place any line to within
    // a fraction of a second, without a mark for every packet of a busy fight.

    public readonly record struct ArrivalMark(long Offset, DateTimeOffset At);

    private static readonly TimeSpan MarkSpacing = TimeSpan.FromMilliseconds(250);
    private const int MaxMarks = 4096;
    private readonly Queue<ArrivalMark> _marks = new();
    private DateTimeOffset _lastMarkAt = DateTimeOffset.MinValue;

    private void NoteArrival(long offset)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        if (now - _lastMarkAt < MarkSpacing) return;
        _lastMarkAt = now;
        if (_marks.Count >= MaxMarks) _marks.Dequeue();
        _marks.Enqueue(new ArrivalMark(offset, now));
    }

    // The live bytes, the offset (in TotalBytes terms) of the first of them, and
    // the arrival marks that fall inside them.
    public byte[] Snapshot(out long startOffset, out ArrivalMark[] marks)
    {
        lock (_gate)
        {
            startOffset = _totalBytes - _count;
            long start = startOffset;
            marks = _marks.Where(m => m.Offset >= start).ToArray();
            byte[] result = new byte[_count];
            if (_count == 0) return result;
            int from = (_head - _count + Capacity) % Capacity;
            int firstRun = Math.Min(_count, Capacity - from);
            Array.Copy(_ring, from, result, 0, firstRun);
            if (firstRun < _count) Array.Copy(_ring, 0, result, firstRun, _count - firstRun);
            return result;
        }
    }
}
