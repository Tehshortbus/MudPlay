namespace FujinTerm.Services;

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

    // Fired after each Append on the producer's thread.
    public event Action? BufferChanged;

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

        BufferChanged?.Invoke();
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
        }
        BufferChanged?.Invoke();
    }
}
