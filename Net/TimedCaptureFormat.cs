namespace FujinTerm.Net;

/// <summary>
/// On-disk format constants for the timed capture / replay file used by
/// <see cref="TimedCaptureWriter"/> and <see cref="ReplayPlayer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Layout (little-endian throughout):
/// </para>
/// <code>
/// ┌───────────────────────────────────────┐
/// │ magic   : 4 bytes  'F' 'J' 'T' 'C'    │  Header
/// │ version : uint16   = 1                │
/// │ flags   : uint16   = 0 (reserved)     │
/// ├───────────────────────────────────────┤
/// │ Repeat (one record per captured chunk)│
/// │ ┌───────────────────────────────────┐ │
/// │ │ deltaMs : uint32                  │ │ Milliseconds since capture start
/// │ │ length  : uint32                  │ │ Byte count that follows
/// │ │ bytes   : length bytes            │ │ Cleaned (post-IAC) display bytes
/// │ └───────────────────────────────────┘ │
/// └───────────────────────────────────────┘
/// </code>
/// <para>
/// <see cref="HeaderSize"/> is fixed at 8 bytes; the chunk records repeat
/// until EOF. A truncated tail is tolerated by the player (incomplete final
/// record is skipped).
/// </para>
/// </remarks>
internal static class TimedCaptureFormat
{
    public static ReadOnlySpan<byte> Magic => "FJTC"u8;
    public const ushort CurrentVersion = 1;
    public const int HeaderSize = 8;            // magic (4) + version (2) + flags (2)
    public const int ChunkHeaderSize = 8;       // deltaMs (4) + length (4)
}
