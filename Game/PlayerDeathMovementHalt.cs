using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game;

// Full-stops every movement engine when the local player dies, so we sit in the
// graveyard we respawn into instead of a still-running loop / walk-to / Auto-Lair
// marching us straight back out before we've recovered.
//
// It reacts to OUR own death via RoomTracker.PlayerDeathObserved — the ONE signal
// that fires for both death phrasings. An earlier version keyed off
// DeathLineWatcher's "You have been slain by <killer>." line, but a miracle-save
// death ("You have been killed! / … saved. / You have N lives left.") never prints
// "slain by", so it silently missed every miracle death and the loop kept
// rerouting out of the graveyard. RoomTracker.NoteDeath fires the universal signal
// for both forms, so both now stop. A party member's death is a different room line
// handled elsewhere. The stop is unconditional because any death lands us alone in
// the graveyard regardless of prior party role: a leader's party disbands on death
// and a dead follower is dropped from the group, so after death we are always the
// one who would drive movement.
//
// Death is a clean STOP, identical to hitting the Navigation Stop button: every
// engine is stopped, every retained destination is cleared, and the shared UserGate
// is cleared so nothing is left paused. Deliberately NOT a lingering "halted until
// resume" pause — once we're stopped and empty, a manual walk-to / loop / auto-lair
// or a remote command the player issues afterward runs freely rather than being
// refused (user directive, report stock-20260731-082602). Stopping rather than
// pausing is also what clears a loop's mid-recovery state (a miracle-save restores
// HP, clears the HealthRecovery gate, and fires the loop's ResumeAfterRecovery just
// before the death registers) so the graveyard's respawn-room confirm can't drive a
// recovery-reroute straight back out.
public sealed class PlayerDeathMovementHalt : IDisposable
{
    // Surfaced in MovementCoordinator.History when the death stop clears UserGate.
    public const string AsserterName = "PlayerDeathMovementHalt";

    private readonly RoomTracker _tracker;
    private readonly MovementCoordinator _coordinator;
    private readonly LogService? _log;
    private bool _disposed;

    // Graveyard-resync fallback. On death the tracker goes PendingRespawn and waits
    // for the graveyard's room display to land as the new authoritative position.
    // But that display can be slow to arrive on its own (observed ~37s), leaving us
    // "lost" the whole time (report stock-20260730-194053). If we're still
    // un-anchored a short window after death, send a bare CR to force the graveyard
    // to re-display so PendingRespawn's candidate search can land it at once. Fired
    // as a fallback (not immediately) so it lands after the death teleport settles —
    // a CR sent mid-sequence could re-display the pre-teleport room and mis-anchor.
    private static readonly TimeSpan ResyncDelay = TimeSpan.FromSeconds(2.5);
    private IDisposable? _resyncTimer;
    // Arms one tick after the delay; disposing what it returns cancels it.
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private Action<byte[]>? _wireResync;

    // Full-stops every movement engine (walk-to, loop, auto-lair) on death.
    // AppServices wires it to Walker.Stop / LoopRunner.Stop / AutoLair.Stop. We
    // STOP rather than merely pause so no retained destination survives to
    // re-drive us back into the room we died in when the halt is later released.
    private Action? _stopEngines;

    public PlayerDeathMovementHalt(
        RoomTracker tracker,
        MovementCoordinator coordinator,
        LogService? log = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        _schedule = schedule ?? DispatcherTicks.Once;
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(coordinator);
        _tracker = tracker;
        _coordinator = coordinator;
        _log = log;


        _tracker.PlayerDeathObserved += OnPlayerDied;
        _tracker.PlayerDeathInferred += StopEngines;
    }

    // Bind the wire sender used for the post-death graveyard-resync CR. Until set,
    // the resync is a no-op (the tracker still lands the graveyard on the next
    // natural room display, just not hurried along).
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireResync = sender;
    }

    // Bind the engine full-stop invoked on death (Walker/LoopRunner/AutoLair Stop).
    // Until set, death only clears the user gate; the stop is what guarantees no
    // engine's retained destination can re-drive us afterward.
    public void SetEngineStopper(Action stopEngines)
    {
        ArgumentNullException.ThrowIfNull(stopEngines);
        _stopEngines = stopEngines;
    }

    private void OnPlayerDied()
    {
        StopEngines();

        // Arm the graveyard-resync fallback: if the respawn room display hasn't
        // landed us within ResyncDelay, force it with a CR (see the field comment).
        _resyncTimer?.Dispose();
        _resyncTimer = _schedule(ResyncDelay, FireGraveyardResync);
    }

    // Clean stop, same as the Navigation Stop button: full-stop every engine (which
    // clears each one's retained destination) and clear the user gate so nothing is
    // left paused. Clearing the gate AFTER the stop covers the case where the player
    // was manually paused when they died — we don't want a stale pause outliving the
    // death and blocking their next move. Nothing survives to re-drive us back into
    // the room we died in, and a manual or remote nav action afterward runs freely.
    //
    // A death found out on re-entry (PlayerDeathInferred) gets this and no more: a
    // loop the reconnect restarted would otherwise walk a stripped character out of
    // the temple, and the room it stands in has already been read, so there is no
    // respawn display to hurry along.
    private void StopEngines()
    {
        _stopEngines?.Invoke();
        _coordinator.ClearGate(MovementCoordinator.UserGate, AsserterName);
        _log?.Info(DeathLineWatcher.LogCategory,
            "Movement stopped after death — engines and destinations cleared.");
    }

    // The master switch (true = off).
    public Func<bool>? MasterSwitchOff { get; set; }

    // ResyncDelay after death: if the tracker still hasn't anchored the graveyard
    // (a slow / missed respawn display), send a bare CR to re-display it now so the
    // PendingRespawn candidate search lands the room. A tick where we've already
    // anchored is a no-op.
    private void FireGraveyardResync()
    {
        _resyncTimer?.Dispose();
        _resyncTimer = null;
        if (_tracker.State.Confidence is not (RoomConfidence.PendingRespawn or RoomConfidence.Lost))
            return;
        if (_wireResync is null) return;
        // The stop on death is a safety act and always runs; the redisplay it
        // would send is a position fix, which the master switch holds back.
        if (MasterSwitchOff?.Invoke() == true) return;
        _wireResync(System.Text.Encoding.Latin1.GetBytes("\r"));
        _log?.Info(DeathLineWatcher.LogCategory,
            "Post-death graveyard resync — sent CR to re-observe the respawn room.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _resyncTimer?.Dispose();
        _resyncTimer = null;
        _tracker.PlayerDeathObserved -= OnPlayerDied;
        _tracker.PlayerDeathInferred -= StopEngines;
    }
}
