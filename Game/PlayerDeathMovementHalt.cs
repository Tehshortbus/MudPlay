using FujinTerm.Game.Combat;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.Game;

// Halts every movement engine when the local player dies, so we sit in the
// graveyard we respawn into instead of a still-running loop / walk-to / Auto-Lair
// marching us straight back out before we've recovered.
//
// It reacts to OUR own death via RoomTracker.PlayerDeathObserved — the ONE signal
// that fires for both death phrasings. An earlier version keyed off
// DeathLineWatcher's "You have been slain by <killer>." line, but a miracle-save
// death ("You have been killed! / … saved. / You have N lives left.") never prints
// "slain by", so the halt silently missed every miracle death and the loop kept
// rerouting out of the graveyard. RoomTracker.NoteDeath fires the universal signal
// for both forms, so both now halt. A party member's death is a different room line
// handled elsewhere. The halt is unconditional because any death lands us alone in
// the graveyard regardless of prior party role: a leader's party disbands on death
// and a dead follower is dropped from the group, so after death we are always the
// one who would drive movement. (That's why "matters when leader or solo" collapses
// to "always" — there's no post-death case where we're still a follower being
// dragged by a living leader.) When no engine was running the assert is a harmless
// no-op.
//
// The halt rides the shared UserGate rather than a bespoke gate: the requirement
// is identical to a manual pause — "stay stopped until the player resumes" — and
// UserGate already carries exactly that contract (only a user resume clears it,
// and every resume affordance in the Navigation window does). Reusing it slots the
// death-pause into the existing Pause / Resume UX for free and can never leave a
// stuck gate. HaltedForDeath is a display-only flavour so the Navigation chip can
// read "Paused — recovering" while the death pause holds; it auto-clears the
// instant the user releases the pause (observed via GatesChanged).
public sealed class PlayerDeathMovementHalt : IDisposable
{
    // Surfaced in MovementCoordinator.History when the death pause asserts UserGate.
    public const string AsserterName = "PlayerDeathMovementHalt";

    private readonly RoomTracker _tracker;
    private readonly MovementCoordinator _coordinator;
    private readonly LogService? _log;
    private bool _disposed;

    // True only while a death-induced pause is holding. Distinct from "UserGate is
    // asserted" — the user can also pause manually — so the chip can flavour just
    // the death case as "recovering". Auto-cleared when the user resumes.
    public bool HaltedForDeath { get; private set; }

    // Fires when HaltedForDeath flips, so the Navigation chip refreshes its reason.
    public event Action? HaltedForDeathChanged;

    public PlayerDeathMovementHalt(
        RoomTracker tracker,
        MovementCoordinator coordinator,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(coordinator);
        _tracker = tracker;
        _coordinator = coordinator;
        _log = log;

        _tracker.PlayerDeathObserved += OnPlayerDied;
        _coordinator.GatesChanged += OnGatesChanged;
    }

    private void OnPlayerDied()
    {
        // Assert first, THEN raise the flavour: AssertGate fires GatesChanged, and
        // OnGatesChanged must still see HaltedForDeath == false at that point so it
        // doesn't immediately clear the flavour we're about to set.
        _coordinator.AssertGate(MovementCoordinator.UserGate, AsserterName,
            "died — halted in graveyard until manual resume");
        SetHaltedForDeath(true);
        _log?.Info(DeathLineWatcher.LogCategory,
            "Movement halted after death — resume from the Navigation window when you're ready to move.");
    }

    // The death pause rides UserGate, so the moment the user resumes (clears
    // UserGate through any Navigation affordance) the "recovering" flavour drops.
    private void OnGatesChanged()
    {
        if (!HaltedForDeath) return;
        if (_coordinator.AssertedGates.Contains(MovementCoordinator.UserGate)) return;
        SetHaltedForDeath(false);
    }

    private void SetHaltedForDeath(bool value)
    {
        if (HaltedForDeath == value) return;
        HaltedForDeath = value;
        HaltedForDeathChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tracker.PlayerDeathObserved -= OnPlayerDied;
        _coordinator.GatesChanged -= OnGatesChanged;
    }
}
