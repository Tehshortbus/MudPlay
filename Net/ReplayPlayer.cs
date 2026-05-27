using System.Buffers.Binary;

namespace FujinTerm.Net;

/// <summary>
/// Reads a timed binary capture written by <see cref="TimedCaptureWriter"/>
/// and replays its chunks via <see cref="ChunkPlayed"/> at the original
/// cadence. Intended for parser-heavy / tick-detection diagnostics: pipe
/// <see cref="ChunkPlayed"/> into <c>TerminalEmulator.Feed</c> and the
/// emulator can't tell the difference from a live session.
/// </summary>
/// <remarks>
/// Speed control: <see cref="PlayAsync(double, CancellationToken)"/> takes a
/// speed multiplier. <c>1.0</c> = original cadence; <c>2.0</c> = double speed;
/// <c>0.0</c> = as fast as the consumer can keep up (no waits). A truncated
/// trailing chunk is detected and skipped without throwing.
/// </remarks>
public sealed class ReplayPlayer
{
    private readonly string _path;

    /// <summary>Fired before the first chunk is dispatched.</summary>
    public event Action? Started;

    /// <summary>Fired once after the final chunk (or after an early <c>cancel</c>).</summary>
    public event Action? Completed;

    /// <summary>Fired once per chunk, on the caller's thread (whatever ran <c>PlayAsync</c>).</summary>
    public event Action<ReadOnlyMemory<byte>>? ChunkPlayed;

    public ReplayPlayer(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Replay at original cadence.</summary>
    public Task PlayAsync(CancellationToken ct = default) => PlayAsync(1.0, ct);

    /// <summary>
    /// Replay scaled by <paramref name="speedMultiplier"/>. <c>0</c> or
    /// negative values play as fast as possible.
    /// </summary>
    public async Task PlayAsync(double speedMultiplier, CancellationToken ct = default)
    {
        using FileStream stream = new(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ReadAndValidateHeader(stream);

        Started?.Invoke();

        long startedAt = Environment.TickCount64;
        byte[] chunkHeader = new byte[TimedCaptureFormat.ChunkHeaderSize];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(chunkHeader.AsMemory(), ct).ConfigureAwait(false);
                if (read == 0) break;                                  // Clean EOF.
                if (read < TimedCaptureFormat.ChunkHeaderSize) break;  // Truncated trailing chunk.

                uint deltaMs = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(0, 4));
                uint length  = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4, 4));
                if (length == 0) continue;

                byte[] payload = new byte[length];
                int got = await stream.ReadAsync(payload.AsMemory(), ct).ConfigureAwait(false);
                if (got < length) break;                                // Truncated payload.

                if (speedMultiplier > 0.0)
                {
                    double targetMs = deltaMs / speedMultiplier;
                    double elapsedMs = Environment.TickCount64 - startedAt;
                    int waitMs = (int)Math.Max(0, targetMs - elapsedMs);
                    if (waitMs > 0) await Task.Delay(waitMs, ct).ConfigureAwait(false);
                }

                ChunkPlayed?.Invoke(payload);
            }
        }
        finally
        {
            Completed?.Invoke();
        }
    }

    private static void ReadAndValidateHeader(FileStream stream)
    {
        Span<byte> head = stackalloc byte[TimedCaptureFormat.HeaderSize];
        int got = stream.Read(head);
        if (got != TimedCaptureFormat.HeaderSize ||
            !head[..4].SequenceEqual(TimedCaptureFormat.Magic))
        {
            throw new InvalidDataException(
                $"'{stream.Name}' is not a FujinTerm timed capture file (bad magic).");
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(head[4..6]);
        if (version != TimedCaptureFormat.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported capture format version {version}; expected {TimedCaptureFormat.CurrentVersion}.");
        }
    }
}
