namespace FujinTerm.Game.Map;

// Contract every "active-step engine" (walker, loop runner, auto-lair
// scheduler) implements so the shared EngineRecoveryGate can drive tier-2
// watch + tier-3 recovery uniformly across all three. The engine still
// owns its planned path, target queue, and per-step state machine; the
// gate borrows just the four capabilities below.
//
// All methods are invoked from the UI thread (the gate is driven by
// RoomTracker.StateChanged which is already Dispatcher-marshalled
// upstream). Engines need no extra locking.
public interface IRecoverableEngine
{
    // Stable name used in log lines so a paste of the log identifies which
    // engine the gate was attached to. e.g. "Walker", "LoopRunner",
    // "AutoLair".
    string Name { get; }

    // Direction the engine would send NEXT as part of its planned path, or
    // null when the engine has nothing planned (idle, or next step is a
    // non-move command). Used by the gate's tier-2 → tier-3 trigger that
    // fires when the next planned direction isn't available on the current
    // room's exits.
    Direction? PeekNextPlannedDirection();

    // Send a single direction directly — bypassing the engine's planning
    // queue — and call RoomTracker.NoteMoveSent so the tracker stays in
    // sync. Used by the gate to backtrack during tier-3 recovery. The
    // engine MUST NOT advance its own path index for these sends.
    void SendBacktrackMove(Direction direction);

    // Pause active step-sending. The gate calls this when entering tier 3
    // so the engine stops queuing planned steps while the gate's backtrack
    // loop drives the wire. Idempotent.
    void PauseForRecovery(string reason);

    // Resume planned-step sending after a successful tier-3 recovery. The
    // gate passes the room the recovery converged on so the engine can
    // re-plan from there (e.g., re-run BFS, fail the loop if it lands
    // somewhere off-path). Idempotent.
    void ResumeAfterRecovery(RoomKey recoveredAnchor);

    // Terminal failure: the gate's tier-3 backtrack exhausted without
    // uniquely identifying a room. The engine should stop everything and
    // raise its own Failed event. The gate also pops a modeless "Lost"
    // dialog to the user; the engine just needs to clean up.
    void AbortFromRecoveryFailure(string detail);
}
