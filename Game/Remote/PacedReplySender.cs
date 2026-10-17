using System;
using System.Collections.Generic;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// Paces a multi-line data reply (an @roomba sync, a @loop send) out one line at a
// time so a big payload (a gang-house log runs dozens of lines) can't monopolize the
// outbound channel and starve the engine's real-time commands (combat, heal,
// movement) — it's triggered by another player's request, so it must stay in the
// background. Even serialized
// (TelnetClient no longer interleaves writes), a tight burst still floods; this
// trickles them ~800ms apart, leaving a wide gap for other sends between each.
//
// Rate-limit aware: if the game reports a hard clobber ("You are typing too
// quickly - command ignored" on stock; "Too many messages sent …" on paradigm),
// the caller pokes NoteClobber — we then pause a few seconds and re-send the last
// line (the receivers tolerate a duplicate: a roomba merge is newest-wins, a loop
// chunk just overwrites its own slot), so nothing is lost even if the pace briefly
// clips the limit.
public sealed class PacedReplySender
{
    public static readonly TimeSpan PaceInterval = TimeSpan.FromMilliseconds(800);
    public static readonly TimeSpan ClobberBackoff = TimeSpan.FromSeconds(3);

    // Runs an action after a delay. AppServices wires a UI-thread DispatcherTimer
    // one-shot; the default runs inline.
    private readonly Action<TimeSpan, Action> _scheduleAfter;
    private readonly LogService? _log;

    private readonly LinkedList<(Action<string> Reply, string Line)> _queue = new();
    private (Action<string> Reply, string Line)? _lastSent;
    private bool _draining;
    private bool _backoffNext;

    public PacedReplySender(Action<TimeSpan, Action>? scheduleAfter = null, LogService? log = null)
    {
        _scheduleAfter = scheduleAfter ?? ((_, action) => action());
        _log = log;
    }

    // Queue one response's lines (already wrapped, e.g. "@roombadata <blob>") for
    // paced delivery through `reply`. Multiple requests share the one queue,
    // so concurrent syncs naturally serialize behind the same pace.
    public void Enqueue(Action<string> reply, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0) return;
        foreach (string line in lines) _queue.AddLast((reply, line));
        if (!_draining)
        {
            _draining = true;
            _scheduleAfter(TimeSpan.Zero, Pump);
        }
    }

    // The game just reported a rate-limit clobber — the last telepath was likely
    // dropped. Re-queue it at the FRONT and force a backoff before the next send.
    // No-op when nothing is in flight (so an unrelated rate-limit line while idle
    // does nothing).
    public void NoteClobber()
    {
        if (!_draining && _queue.Count == 0) return;
        if (_lastSent is { } last)
        {
            _queue.AddFirst(last);
            _lastSent = null;
        }
        _backoffNext = true;
        _log?.Info("RemoteCmd",
            "rate-limit clobber during a paced data reply — backing off and resending the last line");
    }

    private void Pump()
    {
        if (_queue.Count == 0)
        {
            _draining = false;
            return;
        }
        if (_backoffNext)
        {
            // Skip this beat: give the game a few seconds to recover before the
            // (re-queued) line goes back out.
            _backoffNext = false;
            _scheduleAfter(ClobberBackoff, Pump);
            return;
        }
        (Action<string> Reply, string Line) item = _queue.First!.Value;
        _queue.RemoveFirst();
        item.Reply(item.Line);
        _lastSent = item;
        _scheduleAfter(PaceInterval, Pump);
    }
}
