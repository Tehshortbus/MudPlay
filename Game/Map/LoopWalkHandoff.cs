using MudPlay.Services;

namespace MudPlay.Game.Map;

// A loop the user started from somewhere off it. Getting to a loop is the same thing
// as getting anywhere else, so that walk is a walk-to like any other, with every
// route card a walk-to is offered, and the walker owns it. This holds the loop
// meanwhile and hands it to the loop runner the moment the walk arrives at the loop
// room it was heading for: from there the run is the loop engine's (user, 2026-10-07).
//
// It is dropped, and no loop starts, when the walk can't get there (unless it failed
// on a link already dead: then the reconnect restarts the loop), the user stops,
// picks a walk that ends somewhere else (running to a blocked room, a shop stop), or
// starts something else first.
public sealed class LoopWalkHandoff
{
    private readonly LoopRunner _loops;
    private readonly Action<Action> _post;
    private readonly LogService? _log;

    // The loop waiting on its walk, and the loop room that walk is heading for.
    public Loop? Pending { get; private set; }
    public RoomKey? Entry { get; private set; }

    public event Action? Changed;

    // post runs the hand-over after the walker's arrival has finished dispatching:
    // the loop's first step must not go out inside the tracker update that
    // confirmed the walk's last one.
    public LoopWalkHandoff(LoopRunner loops, Action<Action> post, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(post);
        _loops = loops;
        _post = post;
        _log = log;
    }

    public void Begin(Loop loop, RoomKey entry)
    {
        ArgumentNullException.ThrowIfNull(loop);
        Pending = loop;
        Entry = entry;
        _log?.Info("LoopRunner", $"loop '{loop.Name}' starts when the walk to {entry} arrives");
        Changed?.Invoke();
    }

    public void Cancel(string why)
    {
        if (Pending is not { } loop) return;
        Pending = null;
        Entry = null;
        _log?.Info("LoopRunner", $"loop '{loop.Name}' not started: {why}");
        Changed?.Invoke();
    }

    // A user walk was committed. One to anywhere but the loop room is the user
    // going somewhere else: a new destination, or a pick that stops short of the
    // loop (the furthest reachable room, a shop to buy what the route needs).
    public void NoteWalkCommitted(RoomKey destination)
    {
        if (Entry is { } entry && !entry.Equals(destination))
            Cancel($"the walk picked goes to {destination}, not the loop");
    }

    public void OnWalkerEvent(WalkEvent e)
    {
        if (Pending is not { } loop || Entry is not { } entry) return;
        if (e.Destination is not { } walked || !walked.Equals(entry)) return;
        switch (e.Kind)
        {
            case WalkEventKind.Finished:
                Pending = null;
                Entry = null;
                Changed?.Invoke();
                _log?.Info("LoopRunner", $"the walk reached {entry}; starting loop '{loop.Name}'");
                // Still the user's Start, held for the walk there.
                _post(() => _loops.Start(loop, userStarted: true));
                break;
            case WalkEventKind.Failed:
                // A walk that failed on a link already dead still owes its loop
                // (LoopRunner restarts it after the reconnect).
                _loops.NoteWalkThereFailed(loop);
                Cancel($"the walk there failed ({e.Detail})");
                break;
        }
    }

    // Some loop began by another road (an event, a remote command, a Start from on
    // the loop): the one that was waiting is superseded.
    public void OnLoopEvent(LoopEvent e)
    {
        if (e.Kind == LoopEventKind.Started) Cancel("another loop started");
    }
}
