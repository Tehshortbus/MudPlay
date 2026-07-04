using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Stealth;

// Stealth state tracker. Owns PlayerState.IsSneaking and PlayerState.IsHidden.
// Other engines (CombatManager's pre-attack suppression, CastingDirector's buff
// gate) read those flags to protect the backstab window.
//
// FSM transitions are line-driven (no timers). The observed signals and their
// meanings, per the live MajorMUD sneak sequence:
//   * UserSneakInitiate (clean "Attempting to sneak..." with no failure suffix)
//     is the server ACK that the sneak attempt took — the character is armed and
//     may now move. Establishes StealthState.Sneaking + IsSneaking=true.
//   * UserSneakFailed ("Attempting to sneak...You don't think you're sneaking.")
//     is a soft rejection: the attempt didn't take and must be retried by
//     resending sn. When auto-sneak owns the loop we resend (capped at
//     MaxSneakRetries); otherwise we settle on StealthState.Failed for the caller
//     to retry.
//   * UserSneaking ("Sneaking...", emitted on each room entry while sneak holds)
//     is the post-move confirmation that we entered the new room unseen.
//     Re-establishes StealthState.Sneaking and re-arms the silent-loss watchdog
//     for the room.
//   * UserNotSneaking ("You make a sound as you enter the room!") →
//     StealthState.Idle + IsSneaking=false. Loud loss.
//   * UserCantSneak ("You may not sneak right now!") → StealthState.Failed. Hard
//     block — no auto-retry.
//
// Silent-loss detection: in MajorMUD, sneak silently breaks when a move is
// observed (or a stealth-breaking action fires) without the "Sneaking..." line.
// The watchdog flag _sneakConfirmedThisRoom is set on every positive signal
// (clean initiate OR "Sneaking...") and cleared on NoteRoomChanged. If we
// believed we were sneaking but the new room never re-confirmed, we treat that as
// a silent loss and drop the flag — preventing the engine from thinking we're
// still hidden when CombatManager is about to swing.
public sealed class StealthManager : IDisposable
{
    // LogService category — appears as [Stealth] rows per FSM transition +
    // silent-loss detection.
    public const string LogCategory = "Stealth";

    // Max consecutive sn resends after a soft sneak rejection ("You don't think
    // you're sneaking.") before the auto-sneak loop gives up for this room. Reset
    // on every positive sneak signal + room change.
    private const int MaxSneakRetries = 10;

    private readonly PlayerState _state;
    private readonly LogService? _log;
    private readonly IDisposable _sneakingSub;
    private readonly IDisposable _notSneakingSub;
    private readonly IDisposable _sneakInitiateSub;
    private readonly IDisposable _sneakFailedSub;
    private readonly IDisposable _cantSneakSub;

    private Action<byte[]>? _wireSender;
    private Func<bool>? _isAutoSneakEnabled;
    private Func<bool>? _isAutoHideEnabled;
    private Func<bool>? _isSneakBlockedByRoom;

    private StealthState _stateValue;
    private bool _sneakConfirmedThisRoom;
    private int _sneakRetries;
    private bool _disposed;

    // Current FSM state. Backed by PlayerState.IsSneaking / PlayerState.IsHidden
    // for observables; the FSM state itself is exposed via this property + the
    // StateChanged event.
    public StealthState State => _stateValue;

    // Fires after every confirmed FSM transition (including silent-loss to Idle).
    // Args: old state, new state.
    public event Action<StealthState, StealthState>? StateChanged;

    // Fires when silent-loss is detected on room change — we believed we were
    // sneaking but the new room's emit didn't carry the "Sneaking..."
    // confirmation.
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

    // Bind the wire sender for the auto-sneak / auto-hide engines. Until set, the
    // auto-engines log decisions but don't send commands.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Wire the AutoSneak / AutoHide master switches — typically
    // GeneralSettings.AutoMode.AutoSneak / AutoHide. When the funcs are null the
    // auto-engines stay dormant (passive tracker behavior).
    public void SetAutoToggles(Func<bool> isAutoSneakEnabled, Func<bool> isAutoHideEnabled)
    {
        _isAutoSneakEnabled = isAutoSneakEnabled;
        _isAutoHideEnabled = isAutoHideEnabled;
    }

    // Wire the "sneak blocked by a room occupant" predicate — true when any NPC is
    // in the current room (any NPC at all prevents sneak in MajorMUD). AppServices
    // binds this to CombatStateTracker.HasRoomNpc. When the predicate returns
    // true, auto-sneak suppresses the doomed sn rather than firing it into a
    // guaranteed server rejection. When null, no suppression occurs (sneak is
    // attempted unconditionally).
    public void SetSneakBlockCheck(Func<bool> isSneakBlockedByRoom)
    {
        ArgumentNullException.ThrowIfNull(isSneakBlockedByRoom);
        _isSneakBlockedByRoom = isSneakBlockedByRoom;
    }

    // True while we hold any active stealth (Sneaking or Hidden). Read by
    // CastingDirector to suppress buff casts that would break stealth, and
    // (future) by CombatManager to pick backstab over normal attack.
    public bool IsStealthed =>
        _stateValue == StealthState.Sneaking || _stateValue == StealthState.Hidden;

    // True only while actively StealthState.Sneaking (not Hidden). Backstab
    // requires the sneaking state specifically — you approach an unseen target
    // while moving silently — so CombatManager gates its opening bs on this rather
    // than IsStealthed.
    public bool IsSneaking => _stateValue == StealthState.Sneaking;

    // Called by an external observer (RoomTracker via AppServices) when the
    // player's room changes. If we believed we were sneaking but didn't observe
    // the "Sneaking..." line in the new room within a short window after the room
    // display, we treat it as a silent loss. Practically: callers invoke this
    // AFTER the new room's emit batch has been processed by the router
    // (RoomTracker's StateChanged fires after the room display lands), so the
    // "Sneaking..." emit (if any) has already updated _sneakConfirmedThisRoom.
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

        // Auto-sneak: fires after the silent-loss check so a just-lost
        // sneak immediately re-attempts. This is the reactive path —
        // covers room changes from manual movement the user types at the
        // terminal. Engine-driven moves get the proactive pre-move path
        // (RequestPreMoveStealth) instead, so the move itself is sneaked
        // rather than the room after it.
        TryBeginAutoSneak("room change + idle/failed + !combat");
    }

    // Movement-engine pre-move hook — called by the walker / loop runner
    // immediately before a move's bytes go out (after any door / trap / hidden /
    // multi-action pre-steps) so the move itself is performed under sneak.
    // Non-blocking: fires sn and returns at once; the move is NOT held waiting for
    // the ACK (sneak carries through the move and the new room's "Sneaking..."
    // line confirms it). No-op when auto-sneak is off, we're already sneaking /
    // hidden or mid-attempt, or we're in combat (the walker is gated out of moving
    // while a hostile holds the Combat gate anyway).
    public void RequestPreMoveStealth() => TryBeginAutoSneak("pre-move");

    // Shared auto-sneak entry point. Sends sn exactly once from a settled
    // non-stealth state (StealthState.Idle / StealthState.Failed) when auto-sneak
    // is on and we're not in combat, transitioning to
    // StealthState.AttemptingSneak. No-op when auto-sneak is off, a sneak / hide
    // is already established or in flight, or we're in combat. The settled-state
    // guard prevents a double-send when the reactive room-change path and the
    // pre-move path both fire for the same move.
    private bool TryBeginAutoSneak(string reason)
    {
        if (_isAutoSneakEnabled?.Invoke() != true) return false;
        if (_state.InCombat) return false;
        if (_stateValue != StealthState.Idle && _stateValue != StealthState.Failed) return false;

        // Any NPC in the room prevents sneak from taking — don't burn an
        // `sn` the server will reject. The move (if engine-driven)
        // proceeds regardless; sneak re-attempts once the room is clear.
        if (_isSneakBlockedByRoom?.Invoke() == true)
        {
            _log?.Info(LogCategory, $"auto-sneak suppressed ({reason}): NPC present");
            return false;
        }

        // `sn` is the live MajorMUD command; the clean
        // `Attempting to sneak...` ACK then establishes the armed sneak
        // (OnSneakInitiate) and the move's `Sneaking...` confirms it.
        _log?.Info(LogCategory, $"auto-sneak triggered ({reason})");
        _sneakRetries = 0;
        Transition(StealthState.AttemptingSneak);
        Send("sn");
        return true;
    }

    // Called when the player goes idle (e.g. enters a safe room and the walker
    // stops). Drives the auto-hide engine. Wire from AppServices on whatever
    // "idle" signal is appropriate — for v1, fire it from the same
    // RoomTracker.StateChanged hook as auto-sneak; hide is silent on failure and
    // the server rate-limits attempts.
    public void NoteIdleOpportunity()
    {
        if (_isAutoHideEnabled?.Invoke() != true) return;
        if (_state.InCombat) return;
        if (_stateValue == StealthState.Hidden) return;
        _log?.Info(LogCategory, "auto-hide triggered");
        Send("hid");
    }

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(System.Text.Encoding.Latin1.GetBytes(text + "\r"));
    }

    // Mark hide as confirmed — invoked when the caller observes the server's
    // confirmation line for a successful hide. Hide doesn't have the room-by-room
    // re-confirm that sneak has, so v1 keeps the watch surface narrow: callers
    // explicitly mark hide on/off. (Auto-hide engine follow-up will wire the
    // actual line parse.)
    public void NoteHideConfirmed()
    {
        Transition(StealthState.Hidden);
        _state.IsHidden = true;
    }

    // Clear hide — invoked when the caller observes a hide-breaking event (move,
    // attack, cast).
    public void NoteHideBroken()
    {
        if (_stateValue == StealthState.Hidden)
            Transition(StealthState.Idle);
        _state.IsHidden = false;
    }

    // ----- handlers ----------------------------------------------------

    private void OnSneakInitiate(MatchResult _)
    {
        // Clean `Attempting to sneak...` (no failure suffix — the
        // anchored UserSneakInitiate pattern guarantees that) is the
        // server ACK: the sneak took and we're armed to move.
        EstablishSneaking();
    }

    private void OnSneaking(MatchResult _)
    {
        // `Sneaking...` on room entry — post-move confirmation we
        // arrived unseen. Re-arms the silent-loss watchdog.
        EstablishSneaking();
    }

    // Shared positive-signal handler: marks sneak established (clean initiate ACK
    // or post-move Sneaking...), arms the per-room watchdog, and clears the resend
    // counter.
    private void EstablishSneaking()
    {
        _sneakConfirmedThisRoom = true;
        _sneakRetries = 0;
        if (_stateValue == StealthState.Sneaking) return;
        Transition(StealthState.Sneaking);
        _state.IsSneaking = true;
    }

    private void OnNotSneaking(MatchResult _)
    {
        if (_stateValue == StealthState.Idle && !_state.IsSneaking) return;
        Transition(StealthState.Idle);
        _state.IsSneaking = false;
    }

    private void OnSneakFailed(MatchResult _)
    {
        // `Attempting to sneak...You don't think you're sneaking.` — the
        // attempt was rejected. Resend `sn` when auto-sneak owns the loop
        // (capped at MaxSneakRetries); otherwise settle on Failed and let
        // the caller decide whether to retry or move without sneaking.
        Transition(StealthState.Failed);
        if (_isAutoSneakEnabled?.Invoke() == true
         && !_state.InCombat
         && _sneakRetries < MaxSneakRetries)
        {
            _sneakRetries++;
            _log?.Info(LogCategory, $"sneak rejected — resending sn (retry {_sneakRetries}/{MaxSneakRetries})");
            Transition(StealthState.AttemptingSneak);
            Send("sn");
        }
    }

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
