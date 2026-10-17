using System;
using System.Text;

namespace MudPlay.Services;

// Shared helper that bundles the two concerns every automation engine needs
// when issuing a raw wire command:
//   - Latin-1 encoding of the command + trailing \r (the MajorMUD line
//     terminator).
//   - Null-safe sink invocation so an engine subscribed before the wire was
//     bound silently no-ops instead of throwing.
// Each engine owns one instance and forwards SetWireSender onto it. It keeps no
// copy of what it sends, so a long session's traffic isn't held in memory.
internal sealed class WireSender
{
    private Action<byte[]>? _sink;

    // True once Bind has been called with a non-null sink. Engines guard mutating
    // work behind this so a too-early trigger doesn't burn cooldowns / nag
    // counters on wire-less attempts.
    public bool IsBound => _sink is not null;

    // Attach the wire sink. Idempotent; later binds replace earlier ones (typical
    // when the main VM rebinds on profile swap).
    public void Bind(Action<byte[]> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
    }

    // Encode command as Latin-1 + \r and invoke the bound sink, if any.
    public void Send(string command)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(command + "\r");
        _sink?.Invoke(bytes);
    }
}
