using System.Buffers.Binary;

namespace FujinTerm.Net;

/// <summary>
/// Append-only writer for the timed binary capture format
/// (<see cref="TimedCaptureFormat"/>). Each <see cref="Append"/> call emits
/// one chunk record stamped with the milliseconds elapsed since the writer
/// was opened, so a later <see cref="ReplayPlayer"/> can replay at the
/// original cadence.
/// </summary>
/// <remarks>
/// Thread-safe: a single internal lock guards the file handle. Bytes are
/// flushed on every <see cref="Append"/> so a crash mid-session loses at
/// most the in-flight chunk.
/// </remarks>
public sealed class TimedCaptureWriter : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly long _startTicks = Environment.TickCount64;
    private FileStream? _stream;

    /// <summary>Full path of the capture file.</summary>
    public string Path { get; }

    /// <summary>True until <see cref="Dispose"/> closes the file.</summary>
    public bool IsOpen
    {
        get { lock (_gate) { return _stream is not null; } }
    }

    /// <summary>
    /// Open / overwrite the capture file at <paramref name="path"/> and write
    /// the magic header.
    /// </summary>
    public TimedCaptureWriter(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        WriteHeader();
    }

    /// <summary>
    /// Append a chunk of received bytes. Delta-since-start is computed at
    /// call time. No-op once disposed.
    /// </summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;

        uint deltaMs = (uint)Math.Max(0, Environment.TickCount64 - _startTicks);
        Span<byte> header = stackalloc byte[TimedCaptureFormat.ChunkHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header[..4], deltaMs);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)data.Length);

        lock (_gate)
        {
            if (_stream is null) return;
            _stream.Write(header);
            _stream.Write(data);
            _stream.Flush();
        }
    }

    private void WriteHeader()
    {
        Span<byte> head = stackalloc byte[TimedCaptureFormat.HeaderSize];
        TimedCaptureFormat.Magic.CopyTo(head);
        BinaryPrimitives.WriteUInt16LittleEndian(head[4..6], TimedCaptureFormat.CurrentVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(head[6..8], 0);
        // _stream is non-null here — constructor just created it.
        _stream!.Write(head);
        _stream.Flush();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
