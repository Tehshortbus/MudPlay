using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Stealth;

/// <summary>
/// Phase 9 PR 9.F — stealth state tracker. Owns
/// <see cref="PlayerState.IsSneaking"/> and
/// <see cref="PlayerState.IsHidden"/>. Other engines
/// (<see cref="Game.Combat.CombatManager"/>'s pre-attack
/// suppression, <see cref="Game.Spells.CastingDirector"/>'s buff
/// gate) read those flags to protect the backstab window.
/// </summary>
/// <remarks>
/// <para>
/// FSM transitions are line-driven (no timers). The four observed
/// signals are:
/// </para>
/// <list type="bullet">
/// <item><see cref="KnownPatterns.UserSneakInitiate"/>
/// (<c>Attempting to sneak...</c>) → <see cref="StealthState.AttemptingSneak"/>.</item>
/// <item><see cref="KnownPatterns.UserSneaking"/>
/// (<c>Sneaking...</c>, emitted on each room entry while sneak holds)
/// → <see cref="StealthState.Sneaking"/> + <c>IsSneaking=true</c>.
/// Confirms the in-flight attempt.</item>
/// <item><see cref="KnownPatterns.UserNotSneaking"/>
/// (<c>You make a sound as you enter the room!</c>) →
/// <see cref="StealthState.Idle"/> + <c>IsSneaking=false</c>. Loud
/// loss.</item>
/// <item><see cref="KnownPatterns.UserSneakFailed"/> /
/// <see cref="KnownPatterns.UserCantSneak"/> →
/// <see cref="StealthState.Failed"/>.</item>
/// </list>
/// <para>
/// <b>Silent-loss detection</b>: in MajorMUD, sneak silently breaks
/// when an action removes it (cast, attack, etc.) without emitting
/// the <c>You make a sound...</c> line. The watchdog flag
/// <c>_sneakConfirmedThisRoom</c> is set on every <c>Sneaking...</c>
/// observation and cleared on
/// <see cref="Game.Map.RoomTracker"/>'s
/// <see cref="RoomTracker.StateChanged"/> event. If we believed we
/// were sneaking but the new room never re-confirmed, we treat that
/// as a silent loss and drop the flag — preventing the engine from
/// thinking we're still hidden when CombatManager is about to swing.
/// </para>
/// </remarks>
public sealed class StealthManager : IDisposable
{
    /// <summary>LogService category — appears as <c>[Stealth]</c> rows
    /// per FSM transition + silent-loss detection.</summary>
    public const string LogCategory = "Stealth";

    private readonly PlayerState _state;
    private readonly LogService? _log;
    private readonly IDisposable _sneakingSub;
    private readonly IDisposable _notSneakingSub;
    private readonly IDisposable _sneakInitiateSub;
    private readonly IDisposable _sneakFailedSub;
    private readonly IDisposable _cantSneakSub;

    private StealthState _stateValue;
    private bool _sneakConfirmedThisRoom;
    private bool _disposed;

    /// <summary>Current FSM state. Backed by
    /// <see cref="PlayerState.IsSneaking"/> /
    /// <see cref="PlayerState.IsHidden"/> for observables; the FSM
    /// state itself is exposed via this property + the
    /// <see cref="StateChanged"/> event.</summary>
    public StealthState State => _stateValue;

    /// <summary>Fires after every confirmed FSM transition (including
    /// silent-loss to Idle). Args: old state, new state.</summary>
    public event Action<StealthState, StealthState>? StateChanged;

    /// <summary>Fires when silent-loss is detected on room change —
    /// we believed we were sneaking but the new room's emit didn't
    /// carry the <c>Sneaking...</c> confirmation.</summary>
    public event Action? SilentSneakLost;

    public StealthManager(
        MessageRouter router,
        PlayerState state,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _log = log;

        _sneakingSub      = router.Subscribe(KnownPatterns.UserSneaking,      OnSneaking);
        _notSneakingSub   = router.Subscribe(KnownPatterns.UserNotSneaking,   OnNotSneaking);
        _sneakInitiateSub = router.Subscribe(KnownPatterns.UserSneakInitiate, OnSneakInitiate);
        _sneakFailedSub   = router.Subscribe(KnownPatterns.UserSneakFailed,   OnSneakFailed);
        _cantSneakSub     = router.Subscribe(KnownPatterns.UserCantSneak,     OnCantSneak);
    }

    /// <summary>
    /// Called by an external observer (RoomTracker via AppServices)
    /// when the player's room changes. If we believed we were
    /// sneaking but didn't observe the <c>Sneaking...</c> line in
    /// the new room within a short window after the room display,
    /// we treat it as a silent loss. Practically: callers invoke
    /// this AFTER the new room's emit batch has been processed by
    /// the router (RoomTracker's StateChanged fires after the room
    /// display lands), so the <c>Sneaking...</c> emit (if any) has
    /// already updated <see cref="_sneakConfirmedThisRoom"/>.
    /// </summary>
    public void NoteRoomChanged()
    {
        if (_stateValue == StealthState.Sneaking && !_sneakConfirmedThisRoom)
        {
            _log?.Info(LogCategory, "silent sneak loss — new room without 'Sneaking...' confirm");
            Transition(StealthState.Idle);
            _state.IsSneaking = false;
            SilentSneakLost?.Invoke();
        }
        _sneakConfirmedThisRoom = false;
    }

    /// <summary>
    /// Mark hide as confirmed — invoked when the caller observes
    /// the server's confirmation line for a successful
    /// <c>hide</c>. Hide doesn't have the room-by-room re-confirm
    /// that sneak has, so v1 keeps the watch surface narrow:
    /// callers explicitly mark hide on/off. (Auto-hide engine
    /// follow-up will wire the actual line parse.)
    /// </summary>
    public void NoteHideConfirmed()
    {
        Transition(StealthState.Hidden);
        _state.IsHidden = true;
    }

    /// <summary>
    /// Clear hide — invoked when the caller observes a
    /// hide-breaking event (move, attack, cast).
    /// </summary>
    public void NoteHideBroken()
    {
        if (_stateValue == StealthState.Hidden)
            Transition(StealthState.Idle);
        _state.IsHidden = false;
    }

    // ----- handlers ----------------------------------------------------

    private void OnSneakInitiate(MatchResult _)
    {
        if (_stateValue == StealthState.Sneaking || _stateValue == StealthState.AttemptingSneak)
            return;     // already in flight / confirmed
        Transition(StealthState.AttemptingSneak);
    }

    private void OnSneaking(MatchResult _)
    {
        _sneakConfirmedThisRoom = true;
        if (_stateValue != StealthState.Sneaking)
        {
            Transition(StealthState.Sneaking);
            _state.IsSneaking = true;
        }
    }

    private void OnNotSneaking(MatchResult _)
    {
        if (_stateValue == StealthState.Idle && !_state.IsSneaking) return;
        Transition(StealthState.Idle);
        _state.IsSneaking = false;
    }

    private void OnSneakFailed(MatchResult _) => Transition(StealthState.Failed);

    private void OnCantSneak(MatchResult _) => Transition(StealthState.Failed);

    private void Transition(StealthState next)
    {
        if (_stateValue == next) return;
        StealthState prev = _stateValue;
        _stateValue = next;
        _log?.Info(LogCategory, $"state {prev} -> {next}");
        StateChanged?.Invoke(prev, next);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sneakingSub.Dispose();
        _notSneakingSub.Dispose();
        _sneakInitiateSub.Dispose();
        _sneakFailedSub.Dispose();
        _cantSneakSub.Dispose();
    }
}
