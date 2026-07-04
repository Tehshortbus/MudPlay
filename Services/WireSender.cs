using System;
using System.Collections.Generic;
using System.Text;

namespace FujinTerm.Services;

// Shared helper that bundles the three concerns every automation engine needs
// when issuing a raw wire command:
//   - Latin-1 encoding of the command + trailing \r (the MajorMUD line
//     terminator).
//   - A test seam — LastSentForTests records every outbound buffer so unit tests
//     can assert what hit the wire without faking a TelnetClient.
//   - Null-safe sink invocation so an engine subscribed before the wire was
//     bound silently no-ops instead of throwing.
// Each engine owns one instance and forwards its public surface (SetWireSender,
// LastSentForTests) onto it.
internal sealed class WireSender
{
    private Action<byte[]>? _sink;

    // Every buffer ever pushed through Send, in order. Internal so tests assigned
    // to the FujinTerm assembly can read it without exposing the field publicly.
    // Engines forward this via their own LastSentForTests property.
    public List<byte[]> LastSentForTests { get; } = new();

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

    // Encode command as Latin-1 + \r, append the buffer to LastSentForTests, and
    // invoke the bound sink (if any). When no sink is bound the buffer is still
    // recorded so tests can assert on what an engine would have sent.
    public void Send(string command)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(command + "\r");
        LastSentForTests.Add(bytes);
        _sink?.Invoke(bytes);
    }
}
