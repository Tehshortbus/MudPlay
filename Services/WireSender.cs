using System;
using System.Collections.Generic;
using System.Text;

namespace FujinTerm.Services;

/// <summary>
/// Shared helper that bundles the three concerns every automation
/// engine needs when issuing a raw wire command:
/// <list type="bullet">
/// <item>Latin-1 encoding of the command + trailing <c>\r</c> (the
/// MajorMUD line terminator).</item>
/// <item>A test seam — <see cref="LastSentForTests"/> records every
/// outbound buffer so unit tests can assert what hit the wire without
/// faking a TelnetClient.</item>
/// <item>Null-safe sink invocation so an engine subscribed before the
/// wire was bound silently no-ops instead of throwing.</item>
/// </list>
/// Each engine owns one instance and forwards its public surface
/// (<c>SetWireSender</c>, <c>LastSentForTests</c>) onto it. Replaces
/// the four byte-identical <c>SendWire</c> copies that lived on
/// <c>AutoPartyManager</c> / <c>TrapDisarmManager</c> /
/// <c>DoorOpenManager</c> / <c>HiddenExitRevealManager</c>.
/// </summary>
internal sealed class WireSender
{
    private Action<byte[]>? _sink;

    /// <summary>
    /// Every buffer ever pushed through <see cref="Send"/>, in order.
    /// Internal so tests assigned to the FujinTerm assembly can read
    /// it without exposing the field publicly. Engines forward this
    /// via their own <c>LastSentForTests</c> property.
    /// </summary>
    public List<byte[]> LastSentForTests { get; } = new();

    /// <summary>
    /// True once <see cref="Bind"/> has been called with a non-null
    /// sink. Engines guard mutating work behind this so a too-early
    /// trigger doesn't burn cooldowns / nag counters on wire-less
    /// attempts.
    /// </summary>
    public bool IsBound => _sink is not null;

    /// <summary>
    /// Attach the wire sink. Idempotent; later binds replace earlier
    /// ones (typical when the main VM rebinds on profile swap).
    /// </summary>
    public void Bind(Action<byte[]> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
    }

    /// <summary>
    /// Encode <paramref name="command"/> as Latin-1 + <c>\r</c>, append
    /// the buffer to <see cref="LastSentForTests"/>, and invoke the
    /// bound sink (if any). When no sink is bound the buffer is still
    /// recorded so tests can assert on what an engine *would* have sent.
    /// </summary>
    public void Send(string command)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(command + "\r");
        LastSentForTests.Add(bytes);
        _sink?.Invoke(bytes);
    }
}
