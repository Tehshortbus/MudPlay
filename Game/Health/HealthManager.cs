using System.ComponentModel;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Health;

// Passive HP/MA threshold behavior. Asserts and clears
// MovementCoordinator.HealthRecoveryGate + ManaRecoveryGate on configured
// thresholds and drives the rest / stand cycle with pre- / post-rest command
// sequencing. Does NOT decide spell casts — those route through CastingDirector.
//
// State model — three transitions per pool (HP and MA each track independently):
//   Threshold breach: HP / MA drops to or below the configured rest-trigger.
//     Asserts the corresponding recovery gate. Walker (and any other gate
//     consumer) pauses immediately.
//   Rest-out: when either gate is held AND the player is out of combat
//     (PlayerState.InCombat false), send any configured pre-rest command(s) and
//     then `rest`. Idempotent — won't re-send rest while one is already in flight.
//   Recovery complete: both pools have climbed to or past their configured
//     rest-target. Clears both gates, sends `stand`, and emits any post-rest
//     command(s). Walker resumes when the last gate clears.
//
// In-combat semantics: the HP/MA gates can assert mid-fight (so the walker
// doesn't try to leave the room when a fight is going badly), but `rest` is NEVER
// sent while PlayerState.InCombat is true. As soon as CombatStateTracker clears
// the CombatGate and InCombat flips false, the next Evaluate tick fires the rest
// command.
//
// Pre/post-rest commands honour the ^M-or-; chaining convention documented on
// HealthSettings.PreRestCommand: split the string on either marker, trim each
// fragment, send each as its own wire line.
//
// Run-if-below: when PlayerState.Hp drops to or below HealthSettings.RunIfBelowHp
// OR the caster pool drops to or below HealthSettings.RunIfBelowMa mid-combat AND
// a movement engine is active, the active engine is paused and the character flees
// CombatSettings.RunDistance rooms, optionally preceded by `break`. Backward mode
// (the default) runs BFS from the current room back to the active engine's
// JourneyOrigin and walks the first RunDistance directions of that path — the
// reverse of the trail we came in on. Anchoring on the fixed origin is what keeps
// the retreat heading away from the fight instead of bouncing back into it. When
// the reverse path can't be computed (no origin / unknown room / no graph) it falls
// back to inverting the last sent direction for a single step. Forward mode ("go
// backwards if running" off) instead keeps pressing along the engine's own planned
// route toward its destination — the next RunDistance moves it would have sent
// anyway. The engine resumes via IRecoverableEngine.ResumeAfterRecovery once BOTH
// pools climb back above their run-triggers. Multi-step flee advances one queued
// direction per NoteRoomChanged.
//
// Hang-if-below: PlayerState.Hp at or below HealthSettings.HangIfBelowHp fires a
// single-shot hard disconnect via the configured exit command. Setting the
// threshold to 0 disables the check. The trigger stays live all the way through
// the bleeding-out window: a MajorMUD character at 0 HP or below hasn't died yet
// (death happens at the per-realm negative floor, BbsProfile.PlayerDiesAtHp) and
// can still hang up, so the disconnect keeps firing down to — but not past — that
// floor, giving a dropped-but-not-yet-dead character a last chance to escape.
public sealed class HealthManager : IDisposable
{
    // LogService category — appears as [Health] rows per assert / clear / rest /
    // stand decision.
    public const string LogCategory = "Health";

    // Identifier the HealthManager uses when flipping the HealthRecovery /
    // ManaRecovery gates. Surfaces in MovementCoordinator.History.
    public const string AsserterName = "HealthManager";

    private static readonly char[] CommandChainSplit = new[] { ';', '\n' };

    private readonly PlayerState _state;
    private readonly MovementCoordinator _coordinator;
    private readonly Func<HealthSettings> _readSettings;
    private readonly Func<bool> _isEnabled;
    private readonly Func<string>? _readHangupCommand;
    private readonly Func<Map.IRecoverableEngine?>? _getActiveMovementEngine;
    private readonly Func<Map.Direction?>? _getLastSentDirection;
    private readonly Func<Map.RoomKey, Map.RoomKey, IReadOnlyList<Map.Direction>?>? _findReversePath;
    private readonly Func<Models.Profile.CombatSettings>? _readCombatSettings;
    private readonly Func<Models.Profile.GeneralSettings>? _readGeneralSettings;
    private readonly Func<bool>? _hasEngageableHostiles;
    private readonly Func<bool>? _hasHostileInRoom;
    // Defers the flee firing one dispatch tick so the round's death line (parsed
    // AFTER the end-of-round prompt in the same wire read) can settle before we
    // commit — see the flee branch in Evaluate. Synchronous (a => a()) when
    // unwired, so tests stay deterministic.
    private readonly Action<Action> _post;
    private readonly Func<int>? _readDeathFloor;
    private readonly HangupSignal? _hangupSignal;
    private readonly LogService? _log;

    private Action<byte[]>? _wireSender;
    private Action<byte[]>? _hangupWireSender;  // un-wrapped: pierces EngineSendGate
    private Action? _requestHangupDisconnect;   // hard-close the socket after the exit command
    private Func<bool>? _isPartyFollower;       // in a party AND not the leader
    private Action? _requestPartyWait;          // ping leader to halt (PartyRestSync)
    private Action? _requestPartyOk;            // release leader
    private Func<bool>? _isLeaderResting;       // follower + leader is resting/meditating
    private Func<bool>? _isLeaderWaited;        // WE lead + a member has @wait-held us
    private Func<bool>? _isSelfPoisoned;        // local character is currently poisoned
    private Action? _requestPartyHeal;          // follower flee-substitute: broadcast @heal
    private Func<bool>? _shadowRestClass;       // class has the ShadowRest ability (code 1103)
    private Func<bool>? _shadowRestStealthed;   // currently hidden or sneaking
    private Func<bool>? _shadowRestSolo;        // not in a party (ShadowRest is a solo behavior)
    private Action? _onShadowRestRecovered;     // recovery hit rest-max — resume combat
    private bool _shadowRestWasHolding;         // falling-edge latch for the resume callback
    private Action? _onRecoveryComplete;        // any rest gate topped off — resume a held neutral engage
    private bool _wasRecovering;                // falling-edge latch for _onRecoveryComplete
    private Func<bool>? _shouldSkipRestHere;    // running loop's current room is a "do not rest" waypoint
    private bool _skipRestDeferredRecovery;     // a do-not-rest room made us skip a needed rest; re-arm on the next room change
    private bool _partyWaitSignaled;            // @wait sent, awaiting @ok
    private bool _hpGateAsserted;
    private bool _maGateAsserted;
    private bool _restInFlight;          // sent rest, awaiting recovery
    private bool _restConfirmedByPrompt; // observed (Resting) since the last rest emit
    private bool _wasPoisoned;           // poison state last Evaluate — for the poison-cleared re-rest edge
    // The idle-stall watchdog force-clears combat OPTIMISTICALLY and sends a resync
    // CR; the re-display that re-confirms a still-present monster lands a beat later.
    // Resting the instant InCombat flips false fires in that gap — a blinded / slow
    // monster still in the room got a `rest` sent at it (paradigm-20260814-225055).
    // Hold the rest-out branch until the next room observation re-confirms presence:
    // a lingering hostile re-asserts the hostiles guard, an empty room lets the held
    // rest through. Set on force-clear, cleared on the next observation / room change.
    private bool _restHeldPendingReconfirm;
    // When the reconfirm hold was set. An empty, static room never emits the
    // "Also here:" line that clears the hold, and a stationary character never
    // triggers a room change, so the hold is released after RestReconfirmTimeout
    // as a backstop — by then the resync re-display has had time to re-assert any
    // real hostile (which the hostiles guard then blocks). Kept short.
    private DateTimeOffset _restHoldSetAt;
    private static readonly TimeSpan RestReconfirmTimeout = TimeSpan.FromSeconds(3);
    private readonly Func<DateTimeOffset> _now;
    private bool _fledThisCombat;        // reacted to run-trigger (flee OR @heal), awaiting combat end
    private bool _hangFired;             // emergency-hangup latch; re-arms when danger passes
    private Map.IRecoverableEngine? _fleeEngine;     // engine we paused mid-flee
    private readonly Queue<Map.Direction> _fleeQueue = new(); // remaining flee steps, one per room arrival
    private Map.RoomKey? _lastKnownRoom;             // updated on every NoteRoomChanged
    private bool _disposed;

    public HealthManager(
        PlayerState state,
        MovementCoordinator coordinator,
        Func<HealthSettings> readSettings,
        Func<bool> isEnabled,
        LogService? log = null)
        : this(state, coordinator, readSettings, isEnabled, readHangupCommand: null, log) { }

    // Constructor with a readHangupCommand selector so the hangup-on-emergency
    // path uses the user's configured exit command (typically =x or ;o, set in
    // Settings → Other → Game Exit). Without it, the hangup path no-ops with a
    // log warning. AppServices wires () => GameCommands.ExitCommand.
    public HealthManager(
        PlayerState state,
        MovementCoordinator coordinator,
        Func<HealthSettings> readSettings,
        Func<bool> isEnabled,
        Func<string>? readHangupCommand,
        LogService? log = null)
        : this(state, coordinator, readSettings, isEnabled,
               readHangupCommand,
               getActiveMovementEngine: null,
               getLastSentDirection: null,
               readCombatSettings: null,
               readGeneralSettings: null,
               hasEngageableHostiles: null,
               readDeathFloor: null,
               log) { }

    // Full constructor. The additional selectors wire the flee path:
    //   getActiveMovementEngine — returns the IRecoverableEngine that's currently
    //     running (Walker / Loop / AutoLair are exclusive). Returns null when no
    //     engine is active — flee then no-ops, since flee-if-below only fires
    //     while a movement engine is running.
    //   getLastSentDirection — most recent outbound direction, inverted for the
    //     Backward flee fallback when no reverse path can be computed. Typically
    //     wired to the last entry on EngineRecoveryGate.ExecutedSinceAnchor.
    //   findReversePath — (from, to) → the BFS direction list from one room to
    //     another, or null when unreachable. The Backward flee calls this with
    //     (current room, engine JourneyOrigin) to lay the reverse trail. Wired to
    //     BfsMapper.FindPath; left null in tests that exercise the fallback.
    //   readCombatSettings — for the flee knobs CombatSettings.RunDirection,
    //     BreakBeforeFleeing and RunDistance.
    //   readGeneralSettings — for GeneralSettings.AllowHangupInAllOffMode, the
    //     emergency-hangup carve-out.
    //   hasEngageableHostiles — returns true while the room contains at least one
    //     engageable monster. Gates the rest-out branch so we don't spam `rest`
    //     every tick while a hostile keeps breaking it (a room with hostiles
    //     breaks resting every combat round, so the room must be cleared first).
    //     Typically wired to CombatStateTracker.HasEngageableHostiles.
    //   hasHostileInRoom — returns true while a hostile monster is in the room,
    //     independent of the auto-attack master switch (unlike
    //     hasEngageableHostiles, which reports false whenever auto-attack is off).
    //     Gates the emergency hangup: a low-HP disconnect is an escape from a
    //     fight, so with no hostile present there's nothing to flee and dropping
    //     the carrier would only strand a safe-but-wounded character in a
    //     reconnect loop. Wired to CombatStateTracker.HasHostileMonster.
    //   readDeathFloor — the realm's negative-HP death floor (BbsProfile.
    //     PlayerDiesAtHp, e.g. -25). The emergency-hangup path fires anywhere in
    //     the bleeding-out window (hang-trigger down to this floor) but bails once
    //     HP has fallen past it — a character at or below the floor is already
    //     dead, so there's nothing left to disconnect. Null defaults to -25.
    //   hangupSignal — flags an intentional disconnect so the reactive-reconnect
    //     path stands down. The emergency hangup drops the carrier on purpose;
    //     without signalling it, MainWindowViewModel would classify the drop as
    //     unexpected and dial straight back in — exactly what a low-HP hangup is
    //     meant to prevent. Wired to AppServices.HangupSignal.
    public HealthManager(
        PlayerState state,
        MovementCoordinator coordinator,
        Func<HealthSettings> readSettings,
        Func<bool> isEnabled,
        Func<string>? readHangupCommand,
        Func<Map.IRecoverableEngine?>? getActiveMovementEngine,
        Func<Map.Direction?>? getLastSentDirection,
        Func<Models.Profile.CombatSettings>? readCombatSettings,
        Func<Models.Profile.GeneralSettings>? readGeneralSettings,
        Func<bool>? hasEngageableHostiles,
        Func<int>? readDeathFloor = null,
        LogService? log = null,
        HangupSignal? hangupSignal = null,
        Func<bool>? hasHostileInRoom = null,
        Func<Map.RoomKey, Map.RoomKey, IReadOnlyList<Map.Direction>?>? findReversePath = null,
        Action<Action>? post = null,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(readSettings);
        ArgumentNullException.ThrowIfNull(isEnabled);
        _state = state;
        _coordinator = coordinator;
        _readSettings = readSettings;
        _isEnabled = isEnabled;
        _readHangupCommand = readHangupCommand;
        _getActiveMovementEngine = getActiveMovementEngine;
        _getLastSentDirection = getLastSentDirection;
        _findReversePath = findReversePath;
        _readCombatSettings = readCombatSettings;
        _readGeneralSettings = readGeneralSettings;
        _hasEngageableHostiles = hasEngageableHostiles;
        _hasHostileInRoom = hasHostileInRoom;
        _readDeathFloor = readDeathFloor;
        _log = log;
        _hangupSignal = hangupSignal;
        _post = post ?? (a => a());
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        _state.PropertyChanged += OnStateChanged;
    }

    // Bind the wire sender. Until set, the engine logs decisions but doesn't
    // actually send rest / stand / pre- / post-rest commands.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Bind a SEPARATE, un-wrapped wire sender for the emergency low-HP hangup.
    // Every other HealthManager send flows through _wireSender, which the app
    // wraps through EngineSendGate — so when a hold is up (e.g. the dropped /
    // mortally-wounded hold) those sends silently drop. That's correct for rest
    // / stand / flee (a dropped character can't do them anyway), but the hangup
    // MUST survive the very hold that a drop raises: hanging up is still allowed
    // at or below 0 HP, and it's the dropped character's last escape. Wiring
    // this to the raw un-wrapped SendUserInput lets the hangup pierce the gate.
    // Falls back to _wireSender when unset (tests / pre-wire).
    public void SetHangupWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _hangupWireSender = sender;
    }

    // Wire the client-side socket close for the emergency hangup. The exit
    // command alone leaves the drop to the server (which may not act, or acts
    // slowly, leaving a mortally-wounded character sitting connected); this lets
    // us also close the carrier ourselves. The callback owns the flush timing —
    // it must let the just-sent exit command reach the wire before disposing the
    // socket (see MainWindowViewModel.RequestHangupDisconnect). Unset (tests /
    // pre-wire) means the pre-existing "send exit, wait for server" behaviour.
    public void SetHangupDisconnect(Action requestDisconnect)
    {
        ArgumentNullException.ThrowIfNull(requestDisconnect);
        _requestHangupDisconnect = requestDisconnect;
    }

    // Wire party-role-aware recovery. isPartyFollower returns true when the local
    // character is following a party leader (in a party AND not the leader). While
    // following:
    //   The recovery gates clear as soon as a pool climbs just past the
    //     rest-trigger floor (target = trigger + 1) rather than the full rest-max
    //     — a follower tops off to safety, not to full, so it doesn't hold the
    //     party for a routine heal. The party healer / leader owns full topoff.
    //   requestPartyWait fires when a recovery gate first asserts (dropped below
    //     the floor → ping the leader to halt); requestPartyOk fires when the last
    //     gate clears (back above the floor → release the leader).
    // Until wired — or when not following — recovery targets rest-max and no party
    // signals are emitted (solo / leader behavior). The callbacks (typically
    // PartyRestSync.RequestWait / RequestOk) self-gate on party membership, so
    // invoking them solo is a safe no-op.
    //
    // isLeaderResting (optional) reports whether we're a follower and the party
    // leader is currently resting / meditating. When true and no recovery gate is
    // held, Evaluate opportunistically tops off to rest-max during the leader's
    // downtime — a follower mirrors the leader's rest, gated on the auto-heal
    // master switch AND not being poisoned (isSelfPoisoned). Left null preserves
    // the old gate-only rest behavior.
    //
    // isLeaderWaited (optional) reports whether WE lead the party and a member has
    // telepathed @wait, so our movement is held. When true and not poisoned, the
    // leader uses the forced downtime to top off to rest-max — same "rest to use
    // the wait" behavior as the follower's opportunistic path, just triggered by
    // our own held state instead of the leader's posture. Left null: leaders never
    // rest just because they're waited.
    //
    // isSelfPoisoned (optional) reports whether the local character is poisoned.
    // Gates BOTH downtime-rest paths (leader-waited and follower-mirrors-leader):
    // a poisoned character skips the opportunistic rest (poison ticks break rest
    // and waste the downtime). Does NOT gate the normal threshold-driven rest —
    // a poisoned character below its floor still needs to recover. Left null
    // treats us as never poisoned (the pre-gate behavior).
    //
    // requestPartyHeal (optional) is the follower's flee-substitute: when the
    // run-if-below HP trigger fires AND we're a follower, Evaluate invokes this
    // instead of TryFlee — a follower must not run off alone (it breaks party
    // formation), so it broadcasts @heal and stays put while the party healer tops
    // it up. Leader / solo still flee. Left null preserves the flee-for-everyone
    // behavior. Typically wired to PartyRestSync.RequestHeal.
    public void SetPartyRoleSync(
        Func<bool> isPartyFollower,
        Action requestPartyWait,
        Action requestPartyOk,
        Func<bool>? isLeaderResting = null,
        Action? requestPartyHeal = null,
        Func<bool>? isLeaderWaited = null,
        Func<bool>? isSelfPoisoned = null)
    {
        ArgumentNullException.ThrowIfNull(isPartyFollower);
        ArgumentNullException.ThrowIfNull(requestPartyWait);
        ArgumentNullException.ThrowIfNull(requestPartyOk);
        _isPartyFollower = isPartyFollower;
        _requestPartyWait = requestPartyWait;
        _requestPartyOk = requestPartyOk;
        _isLeaderResting = isLeaderResting;
        _requestPartyHeal = requestPartyHeal;
        _isLeaderWaited = isLeaderWaited;
        _isSelfPoisoned = isSelfPoisoned;
    }

    // Wire the loop's per-waypoint "do not rest here" check: returns true when a
    // loop is running and the room we're standing in is a waypoint flagged
    // DoNotRest, so the rest hold is suppressed and the loop advances out of it.
    // Left unwired, resting is unaffected.
    public void SetDoNotRestSelector(Func<bool> shouldSkipRestHere)
    {
        ArgumentNullException.ThrowIfNull(shouldSkipRestHere);
        _shouldSkipRestHere = shouldSkipRestHere;
    }

    // Wire ShadowRest (Paradigm): classes with the ability can rest while
    // hidden/sneaking in a room with monsters without being attacked (see
    // GAME_MECHANICS "ShadowRest"). All three predicates plus the
    // HealthSettings.UtilizeShadowRest toggle must hold for the behavior to engage:
    //   shadowRestClass — the character's class carries ability code 1103.
    //   isStealthed     — currently hidden OR sneaking (StealthManager.IsStealthed).
    //   isSolo          — not in a party (resting hidden un-targets party heals).
    // onRecovered fires once when recovery reaches rest-max (a held rest gate
    // clears) while ShadowRest was holding — the resume signal that lets
    // CombatManager re-open with a backstab now that we're topped off and still
    // stealthed. Left unwired, ShadowRest simply never engages.
    public void SetShadowRest(
        Func<bool> shadowRestClass,
        Func<bool> isStealthed,
        Func<bool> isSolo,
        Action onRecovered)
    {
        ArgumentNullException.ThrowIfNull(shadowRestClass);
        ArgumentNullException.ThrowIfNull(isStealthed);
        ArgumentNullException.ThrowIfNull(isSolo);
        ArgumentNullException.ThrowIfNull(onRecovered);
        _shadowRestClass = shadowRestClass;
        _shadowRestStealthed = isStealthed;
        _shadowRestSolo = isSolo;
        _onShadowRestRecovered = onRecovered;
    }

    // Fires once each time a rest gate tops off to rest-max (falling edge of
    // IsRecoveringRest). Wired to CombatManager.ResumeAfterRecovery so a combat hold
    // that deferred a passive-neutral engage for this rest re-engages when we're
    // topped off. Left unwired, recovery still works — nothing re-engages.
    public void SetRecoveryCompleteCallback(Action onRecoveryComplete)
    {
        ArgumentNullException.ThrowIfNull(onRecoveryComplete);
        _onRecoveryComplete = onRecoveryComplete;
    }

    // True when every ShadowRest precondition holds: the user opted in, the class
    // has the ability, and we're solo and currently stealthed. This is the "can
    // rest safely with a monster in the room" condition — it relaxes the rest-out
    // hostiles guard.
    private bool ShadowRestActive() =>
        _readSettings().UtilizeShadowRest
        && _shadowRestClass?.Invoke() == true
        && _shadowRestStealthed?.Invoke() == true
        && _shadowRestSolo?.Invoke() == true;

    // True while a ShadowRest recovery is in progress — ShadowRest is active AND a
    // rest gate is held (HP/MA below its floor, climbing toward rest-max). Combat
    // reads this to stand down so the character stays hidden and rests; it drops to
    // false the moment recovery tops off (the gate clears), which is the falling
    // edge that fires the resume callback.
    public bool ShadowRestHolding =>
        ShadowRestActive() && (_hpGateAsserted || _maGateAsserted);

    // True while the HP gate is held.
    public bool HpGateAsserted => _hpGateAsserted;

    // True while the MA gate is held.
    public bool MaGateAsserted => _maGateAsserted;

    // True while an auto-rest recovery is in flight — HP or MA fell below its
    // rest-if-below trigger and we're resting back up to rest-max. This is the
    // "triggered rest" the auto-bless engine holds during (as opposed to idle /
    // standing / idly resting), so blessing defers to recovery unless the user
    // opts into "bless while resting."
    public bool IsRecoveringRest => _hpGateAsserted || _maGateAsserted;

    // True between the rest emit and the corresponding stand emit.
    public bool RestInFlight => _restInFlight;

    // True between the run-if-below reaction (a flee for leader / solo, or a
    // broadcast @heal for a party follower) and the next time
    // PlayerState.InCombat goes false. Single-shot per combat so a low-HP fight
    // can't burn the reaction on every HP-changed event.
    public bool FledThisCombat => _fledThisCombat;

    // True while an HP-triggered flee retreat is actively in progress — the
    // engine is paused and we're walking (or parked awaiting HP recovery on) the
    // flee route. Drops back to false once HP climbs above the run-trigger and
    // the engine resumes. The room-entity classifier reads this so a monster that
    // pursues us mid-flee does NOT re-arm the combat gate: we keep running instead
    // of turning to fight the thing we're fleeing. Distinct from FledThisCombat,
    // which stays true for the rest of the combat even after the retreat ends.
    public bool IsFleeing => _fleeEngine is not null;

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerState.Hp):
            case nameof(PlayerState.Ma):
            case nameof(PlayerState.InCombat):
            case nameof(PlayerState.HasPromptData):
            case nameof(PlayerState.MaxHp):
            case nameof(PlayerState.MaxMa):
            case nameof(PlayerState.Position):
                Evaluate();
                break;
        }
    }

    // Re-evaluate gate state + rest/stand pacing against the current player
    // state. Public so tests can drive it deterministically without needing a
    // real PropertyChanged firing.
    public void Evaluate()
    {
        if (!_isEnabled())
        {
            // Engine off via Settings → General → Auto-Heal / Rest.
            // Defensive clear in case it was asserted just before the
            // user toggled off.
            if (_hpGateAsserted)
            {
                _hpGateAsserted = false;
                _coordinator.ClearGate(MovementCoordinator.HealthRecoveryGate,
                    AsserterName, "auto-heal disabled");
            }
            if (_maGateAsserted)
            {
                _maGateAsserted = false;
                _coordinator.ClearGate(MovementCoordinator.ManaRecoveryGate,
                    AsserterName, "auto-heal disabled");
            }
            // Don't leave a follower's leader hanging on a stale @wait when
            // the engine toggles off mid-recovery.
            if (_partyWaitSignaled)
            {
                _partyWaitSignaled = false;
                _requestPartyOk?.Invoke();
            }
            _restInFlight = false;
            _restConfirmedByPrompt = false;
            _wasPoisoned = false;
            _fledThisCombat = false;

            // All-off carve-out: even with the engine disabled, honour the
            // emergency hangup when the user opted in. An AFK character
            // shouldn't be left dying just because auto-heal is off — but
            // it stays opt-in (default off) since hanging up is a last
            // resort. Only the hangup branch runs; everything else above
            // already cleared. TryEmergencyHangup self-guards on MaxHp and the
            // trigger/death-floor window, so we just need a prompt — the hangup
            // stays live all the way through the bleeding-out zone.
            if (_readGeneralSettings?.Invoke() is { AllowHangupInAllOffMode: true }
                && _state.HasPromptData)
            {
                TryEmergencyHangup(_readSettings());
            }
            return;
        }
        if (!_state.HasPromptData) return;
        HealthSettings s = _readSettings();

        // Emergency hangup evaluates first and runs through the whole
        // bleeding-out window: a dropped character (Hp <= 0 but not yet at the
        // realm death floor) can still hang up, so this must precede the
        // dead/dropped early-return below — otherwise a bleeding-out non-caster
        // (Ma also 0) would skip the disconnect entirely. When it actually
        // fires there's nothing left to rest / flee for, so we're done.
        if (TryEmergencyHangup(s)) return;

        // At or below 0 HP the character is dropped / mortally wounded (or dead)
        // and can't rest / stand / flee — the game rejects every action command.
        // The emergency hangup already ran above (it's the one send allowed while
        // dropped), so there's nothing left for this tick to do. Bailing on Hp
        // alone (not Hp && Ma) also skips the zero-on-zero prompt-race assert:
        // PromptParser writes Hp + MaxHp before flipping HasPromptData, so a real
        // live character is never at Hp <= 0 here. PlayerDroppedGate holds the
        // engine + movement gates for the whole dropped window; recovery routing
        // for an actual death runs through DeathLineWatcher.
        if (_state.Hp <= 0) return;

        // Rest-interruption recovery on a resting-state change. Two-step
        // latch so we don't race the (Resting)/(Meditating) prompt arrival:
        //   1. We send `rest` or `meditate` and set _restInFlight=true.
        //   2. On the FIRST Evaluate tick where Position is Resting OR
        //      Meditating, we flip _restConfirmedByPrompt=true — the
        //      server has put us into one of the two resting-family
        //      positions (ChooseRestCommand picks whichever command the
        //      moment calls for; either lands here).
        //   3. Any subsequent tick where Position is neither (server
        //      broke our rest because we took damage, entered combat, cast
        //      a bless, or moved) drops _restInFlight so the rest-out
        //      branch below re-fires.
        // Without step 2, a fast follow-up HP-changed tick that fires
        // before the prompt arrives would spuriously clear _restInFlight
        // and double-send the command. Checking only Resting here (report:
        // meditate never re-engaged after a bless interrupted it) left
        // step 2 permanently unreached for Meditating — _restConfirmedByPrompt
        // never flipped true, so step 3's guard never tripped either, and
        // _restInFlight stuck true until the next room move (NoteRoomChanged
        // clears it unconditionally) masked the bug for movers but not for a
        // party member sitting still recovering mana.
        bool restingFamily = _state.Position is PlayerPosition.Resting or PlayerPosition.Meditating;
        if (_restInFlight && restingFamily)
        {
            _restConfirmedByPrompt = true;
        }
        else if (_restInFlight && _restConfirmedByPrompt && !restingFamily)
        {
            _restInFlight = false;
            _restConfirmedByPrompt = false;
            _log?.Combat(LogCategory,
                $"rest interrupted — position now {_state.Position} " +
                $"(hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa} " +
                $"inCombat={_state.InCombat})");
        }

        // Role-aware recovery target: a follower clears the gate just
        // above the rest floor (target = trigger + 1) so it doesn't make
        // the party wait for a full topoff; solo / leader recover to
        // rest-max. Defaults to leader/solo when no role selector is wired.
        bool follower = _isPartyFollower?.Invoke() ?? false;

        // A loop can flag the room it's standing in as "do not rest here" (too
        // dangerous to sit still). While in such a room we never raise the rest
        // hold — the loop stays running and advances out of it — and we release
        // the hold if it was already up. Only THIS room is protected; the moment
        // the loop steps into another room this re-evaluates and rests normally.
        bool skipRest = _shouldSkipRestHere?.Invoke() ?? false;

        // ----- HP gate transitions ---------------------------------
        int hpRestTrigger = PoolThreshold.Resolve(s.HpThresholdMode, s.RestIfBelowHp, _state.MaxHp);
        int hpRestMax     = PoolThreshold.Resolve(s.HpThresholdMode, s.RestMaxHp, _state.MaxHp);
        int hpRestTarget  = follower
            ? Math.Min(hpRestTrigger + 1, _state.MaxHp)
            : hpRestMax;

        // Strictly below — "rest if below N" rests only when the pool is
        // under N, never AT N. (Equal-or-less traps a level-2 mystic: 1 max
        // KAI, trigger 0, spend the KAI → MA 0 == trigger 0 would pause for
        // mana forever.)
        if (!skipRest && !_hpGateAsserted && _state.MaxHp > 0 && _state.Hp < hpRestTrigger)
        {
            _hpGateAsserted = true;
            _coordinator.AssertGate(MovementCoordinator.HealthRecoveryGate,
                AsserterName,
                $"HP {_state.Hp}/{_state.MaxHp} < rest-trigger={hpRestTrigger}");
        }
        else if (_hpGateAsserted && (skipRest || _state.Hp >= hpRestTarget))
        {
            _hpGateAsserted = false;
            _coordinator.ClearGate(MovementCoordinator.HealthRecoveryGate,
                AsserterName,
                skipRest
                    ? "do-not-rest room — advancing instead of resting"
                    : $"HP {_state.Hp}/{_state.MaxHp} >= rest-target={hpRestTarget}");
        }

        // ----- MA gate transitions ---------------------------------
        int maRestTrigger = PoolThreshold.Resolve(s.MaThresholdMode, s.RestIfBelowMa, _state.MaxMa);
        int maRestMax     = PoolThreshold.Resolve(s.MaThresholdMode, s.RestMaxMa, _state.MaxMa);
        int maRestTarget  = follower
            ? Math.Min(maRestTrigger + 1, _state.MaxMa)
            : maRestMax;

        // Strictly below (see HP gate above) — the mystic-at-level-2 case.
        if (!skipRest && !_maGateAsserted && _state.Ma < maRestTrigger && _state.MaxMa > 0)
        {
            _maGateAsserted = true;
            _coordinator.AssertGate(MovementCoordinator.ManaRecoveryGate,
                AsserterName,
                $"MA {_state.Ma}/{_state.MaxMa} < rest-trigger={maRestTrigger}");
        }
        else if (_maGateAsserted && (skipRest || _state.Ma >= maRestTarget))
        {
            _maGateAsserted = false;
            _coordinator.ClearGate(MovementCoordinator.ManaRecoveryGate,
                AsserterName,
                skipRest
                    ? "do-not-rest room — advancing instead of resting"
                    : $"MA {_state.Ma}/{_state.MaxMa} >= rest-target={maRestTarget}");
        }

        // A do-not-rest room can't raise (and clears) the recovery gate even when
        // the pool is below a rest trigger. A plain Standing hop OUT of that room
        // raises no prompt change, so Evaluate wouldn't re-run to re-arm the gate
        // in the next restable room — the deficit would ride untended until some
        // later prompt happens to change (typically back at the loop's circle
        // start, where do-not-rest eats it again: the reported "whole loop won't
        // rest"). Latch the deferred deficit here so NoteRoomChanged re-arms on the
        // next room change.
        if (skipRest && (_state.Hp < hpRestTrigger || _state.Ma < maRestTrigger))
            _skipRestDeferredRecovery = true;

        // ----- party-follower @wait / @ok --------------------------
        // @wait fires when a recovery gate first asserts (we dropped below a
        // rest floor). But @ok must NOT ride the same gate: a follower's
        // movement gate releases at trigger+1 (so it keeps pace without a
        // full topoff), and releasing @ok there tells the leader to resume
        // while we're one point above the floor — the party lurches forward,
        // we re-drop, and @wait/@ok flap (report 222618). So hold the signal
        // until BOTH pools reach the full rest-max ceiling — the level the
        // user considers "rested" — decoupled from the movement floor.
        // PartyRestSync self-gates on membership, so these no-op solo/as leader.
        bool droppedBelowFloor = _hpGateAsserted || _maGateAsserted;
        if (droppedBelowFloor && !_partyWaitSignaled)
        {
            _partyWaitSignaled = true;
            _requestPartyWait?.Invoke();
        }
        else if (_partyWaitSignaled)
        {
            bool hpRested = _state.MaxHp <= 0 || _state.Hp >= hpRestMax;
            bool maRested = _state.MaxMa <= 0 || _state.Ma >= maRestMax;
            if (hpRested && maRested)
            {
                _partyWaitSignaled = false;
                _requestPartyOk?.Invoke();
            }
        }

        // ----- flee on critical HP/MA mid-combat -------------------
        // Run-if-below: either pool triggers — HP at/below RunIfBelowHp OR the
        // caster pool at/below RunIfBelowMa (an out-of-mana caster is as stuck
        // as a low-HP fighter). Fires only when a movement engine is active —
        // "if you aren't running a movement engine, the flee-if-below wouldn't
        // fire". On trigger: optionally send `break` to disengage combat, then
        // begin a multi-step flee over CombatSettings.RunDistance rooms
        // (Backward = the reverse-BFS trail toward the engine's JourneyOrigin;
        // Forward = the engine's own next planned moves toward its destination).
        // Subsequent steps advance one per NoteRoomChanged; the paused engine
        // auto-resumes once BOTH pools climb back above their run-triggers
        // (recovery branch below).
        if (!_state.InCombat)
        {
            _fledThisCombat = false;
        }
        else if (!_fledThisCombat)
        {
            int hpRunTrigger = PoolThreshold.Resolve(s.HpThresholdMode, s.RunIfBelowHp, _state.MaxHp);
            int maRunTrigger = PoolThreshold.Resolve(s.MaThresholdMode, s.RunIfBelowMa, _state.MaxMa);
            // A run-trigger of 0 means "never flee on this pool" — the pool's
            // flee is off. Gate on the RAW setting so "off" is mode-agnostic
            // (0% and absolute-0 both resolve to a 0 trigger, and without this
            // the MA branch would flee every time mana bottoms out at 0).
            bool hpFleeEnabled = s.RunIfBelowHp > 0;
            bool maFleeEnabled = s.RunIfBelowMa > 0;
            bool hpRun = hpFleeEnabled && _state.MaxHp > 0 && _state.Hp > 0 && _state.Hp <= hpRunTrigger;
            bool maRun = maFleeEnabled && _state.MaxMa > 0 && _state.Ma <= maRunTrigger;
            if (hpRun || maRun)
            {
                _fledThisCombat = true;
                string reason = hpRun
                    ? $"HP {_state.Hp}/{_state.MaxHp} <= run-trigger={hpRunTrigger}"
                    : $"MA {_state.Ma}/{_state.MaxMa} <= run-trigger={maRunTrigger}";
                bool fleeAsFollower = follower && _requestPartyHeal is not null;
                // Defer the flee one dispatch tick, then re-verify a live hostile
                // before committing. The end-of-round prompt that dropped us into
                // flee territory is parsed BEFORE the round's death line in the same
                // wire read (PromptScanner.Append runs ahead of Emulator.Feed), so
                // right now a monster we killed this round still reads as present
                // (InCombat true, hostile present). Posting lets the death line
                // process first: a killing blow that empties the room then falls
                // through to rest instead of running from nothing, while a fresh
                // hostile that survived still fires the flee (report
                // stock-20260730-160706). The single-shot latch is set now so a
                // burst of HP updates doesn't queue several flees.
                _post(() => CommitFleeReaction(hpRun, fleeAsFollower, reason));
            }
        }

        // Auto-resume — when a fled engine is paused AND BOTH pools have
        // climbed back above their run-triggers AND no more flee steps
        // are queued, hand control back to the engine. Requiring mana too
        // (when the character has a caster pool) stops us resuming straight
        // into another mana-triggered flee. Backward mode retraces its path
        // from the current room; Forward continues toward the destination.
        if (_fleeEngine is not null && _fleeQueue.Count == 0 && _state.MaxHp > 0)
        {
            int hpRunTrigger = PoolThreshold.Resolve(s.HpThresholdMode, s.RunIfBelowHp, _state.MaxHp);
            int maRunTrigger = PoolThreshold.Resolve(s.MaThresholdMode, s.RunIfBelowMa, _state.MaxMa);
            // A disabled pool (run-trigger 0) never blocks resume — otherwise a
            // caster with MA flee off could never climb "above" a 0 trigger with
            // 0 mana and would stay paused forever.
            bool hpRecovered = s.RunIfBelowHp <= 0 || _state.Hp > hpRunTrigger;
            bool maRecovered = s.RunIfBelowMa <= 0 || _state.MaxMa <= 0 || _state.Ma > maRunTrigger;
            if (hpRecovered && maRecovered && _lastKnownRoom is { } room)
            {
                _log?.Combat(LogCategory,
                    $"flee complete — resuming engine={_fleeEngine.Name} at {room} " +
                    $"(HP {_state.Hp}/{_state.MaxHp} > {hpRunTrigger}, MA {_state.Ma}/{_state.MaxMa} > {maRunTrigger})");
                _fleeEngine.ResumeAfterRecovery(room);
                _fleeEngine = null;
            }
        }

        // ----- rest pacing ------------------------------------------
        // On recovery we send the user's configured post-rest chain
        // (if any) and clear _restInFlight. No "stand" — that's not a
        // valid MajorMUD command; the server auto-stands the player
        // when they next move or act, and the walker's next move
        // (which the resumed nav engine fires once both gates clear)
        // is what actually exits the (resting) state.
        bool anyGate = _hpGateAsserted || _maGateAsserted;

        // A poisoned character skips the downtime-rest paths below: poison ticks
        // keep breaking rest, so sitting during the leader's / our own wait just
        // burns wire round-trips without recovering. This gate applies ONLY to the
        // opportunistic paths — a poisoned character below its own rest floor still
        // rests through the anyGate branch, since it needs the recovery to survive.
        bool selfPoisoned = _isSelfPoisoned?.Invoke() ?? false;

        // Poison-cleared re-rest. A rest sent while poisoned never reaches the (Resting)
        // state (poison refuses / breaks it), so it latches _restInFlight but the two-step
        // interruption latch above never clears it (that path needs _restConfirmedByPrompt,
        // which only flips once we're actually Resting). The stale _restInFlight then blocks
        // the re-send once poison wears off — the reported "sat standing below the rest floor
        // after the poison cleared" bug. On the poison falling edge, drop an unconfirmed rest
        // latch so THIS tick's rest-out branch re-sends a fresh rest now that resting will take.
        if (_wasPoisoned && !selfPoisoned && _restInFlight && !_restConfirmedByPrompt)
        {
            _restInFlight = false;
            _log?.Combat(LogCategory,
                $"poison cleared — dropping unconfirmed rest latch to re-rest " +
                $"(hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa})");
        }
        _wasPoisoned = selfPoisoned;

        // Opportunistic follower rest: the leader has stopped to rest /
        // meditate, so we use the downtime to top off too — even above our
        // own rest-trigger floors, up to rest-max. No gate is asserted (we're
        // not below a floor, so we must NOT @wait a leader who's already
        // voluntarily halted, and we don't hold the movement gate). It only
        // engages when there's actually something to recover; once both pools
        // hit rest-max NeedsOpportunisticTopOff goes false and the post-rest
        // chain fires through the shared !shouldRest recovery branch.
        bool opportunistic = !anyGate
            && !selfPoisoned
            && (_isLeaderResting?.Invoke() ?? false)
            && NeedsOpportunisticTopOff(s);

        // Leader-waited rest: WE lead and a member has @wait-held us, so we're
        // stuck in this room anyway — use the forced downtime to top off. Same
        // rest-max target and poison gate as the follower's opportunistic path;
        // the only difference is the trigger (our own held state, not a leader's
        // posture). A single `rest` emit is enough: the movement gate keeps us
        // sitting until the @wait clears, at which point the resumed engine's next
        // move stands us back up.
        bool leaderWaitedRest = !anyGate
            && !selfPoisoned
            && (_isLeaderWaited?.Invoke() ?? false)
            && NeedsOpportunisticTopOff(s);

        bool shouldRest = anyGate || opportunistic || leaderWaitedRest;

        // Don't even try to rest while the room contains an engageable
        // hostile — every combat round breaks rest, so spamming `rest`
        // burns a wire round-trip per swing and we still don't recover.
        // Wait for CombatManager to clear the room (CombatStateTracker
        // flips HasEngageableHostiles false on the next Also-Here),
        // then this same Evaluate tick re-enters here with a clean
        // gate and the rest goes out. If a fresh mob arrives during
        // rest, NoteRoomChanged + a new EntitiesObserved will set
        // HasEngageableHostiles true again and the next breach repeats
        // the cycle (kill → rest → kill → rest), as per user direction.
        bool hostilesPresent = _hasEngageableHostiles?.Invoke() ?? false;

        // ShadowRest relaxes the hostiles guard: a solo, stealthed ShadowRest
        // character rests in place even with a monster in the room — the game
        // keeps it un-attacked while stealthed, and combat stands down (reading
        // ShadowRestHolding) so the rest isn't broken by our own swing. Recovery
        // runs to rest-max, then the gate clears and the resume callback re-opens
        // combat with a backstab (we're still stealthed, opener unspent).
        bool shadowRest = ShadowRestActive();
        if (shadowRest && hostilesPresent && shouldRest && !_state.InCombat && !_restInFlight)
            _log?.Combat(LogCategory, "shadowrest — resting with hostile in room (staying stealthed)");

        // Reconfirm-hold backstop: an empty, static room never emits the "Also here:"
        // line NoteRoomEntitiesReconfirmed waits on, and a stationary character never
        // triggers NoteRoomChanged — so a post-force-clear hold could sit forever,
        // silently blocking auto-rest (reports paradigm-20260818-050950 / -092532:
        // below the rest threshold, out of combat, yet never resting). Release the hold
        // after a short window: by then the watchdog's resync re-display has had ample
        // time to re-assert any real hostile — which the hostiles guard below still
        // blocks — so a room that's genuinely clear is safe to rest in.
        if (_restHeldPendingReconfirm && _now() - _restHoldSetAt > RestReconfirmTimeout)
        {
            _restHeldPendingReconfirm = false;
            _log?.Combat(LogCategory,
                "rest reconfirm-hold timed out — no room re-display re-asserted a hostile, releasing to rest");
        }

        if (shouldRest && !_state.InCombat && !_restInFlight && _restHeldPendingReconfirm
            && (!hostilesPresent || shadowRest))
        {
            // A watchdog force-clear dropped InCombat optimistically; wait for the
            // resync re-display to re-confirm the room is actually empty before
            // resting, so we don't rest at a monster the re-display re-asserts.
            _log?.Combat(LogCategory,
                $"rest held — combat force-cleared, awaiting room re-confirm " +
                $"(hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa})");
        }
        else if (shouldRest && !_state.InCombat && !_restInFlight && (!hostilesPresent || shadowRest))
        {
            // Pick rest vs meditate based on user settings + which
            // pool is the proximate trigger.
            //
            // - UseMeditateAbility is the master toggle (defaults true;
            //   non-Kai classes should turn it off).
            // - MeditateBeforeResting flips the order when BOTH pools
            //   are gated: meditate fills MA first, then rest fills
            //   HP. Without this, rest is sent regardless.
            // - With only MA gated (HP at max), prefer meditate when
            //   UseMeditateAbility is on — rest doesn't recover MA on
            //   most classes.
            // The opportunistic path has no gate to read, so it picks on
            // live pool percentages instead (ChooseOpportunisticRestCommand).
            string command = anyGate
                ? ChooseRestCommand(s)
                : ChooseOpportunisticRestCommand(s);

            string restReason = anyGate ? ""
                : leaderWaitedRest ? " (waited — resting to use the downtime)"
                : " (opportunistic, leader resting)";
            SendChained(s.PreRestCommand);
            SendCommand(command);
            _log?.Combat(LogCategory,
                $"{command}{restReason} " +
                $"hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa}");
            _restInFlight = true;
        }
        else if (!shouldRest && _restInFlight)
        {
            SendChained(s.PostRestCommand);
            _log?.Combat(LogCategory,
                $"recovered hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa}");
            _restInFlight = false;
            _restConfirmedByPrompt = false;
        }

        // ShadowRest resume: recovery topped off to rest-max (the held gate just
        // cleared) while ShadowRest was holding. Fire once on the falling edge so
        // CombatManager re-runs the room and opens with a backstab — we're still
        // stealthed and the opener is unspent because combat stayed suppressed.
        bool shadowRestHolding = ShadowRestHolding;
        if (_shadowRestWasHolding && !shadowRestHolding)
        {
            _log?.Combat(LogCategory,
                $"shadowrest recovered to rest-max — resuming combat " +
                $"hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa}");
            _onShadowRestRecovered?.Invoke();
        }
        _shadowRestWasHolding = shadowRestHolding;

        // General recovery resume: a rest gate topped off to rest-max (falling edge of
        // IsRecoveringRest). CombatManager may have been holding engagement of a
        // passive KillOnSight neutral to let this rest happen — poke it to re-engage
        // now that we're topped off. Gated on its own hold flag, so an ordinary
        // recovery with nothing held is a no-op.
        bool recovering = IsRecoveringRest;
        if (_wasRecovering && !recovering) _onRecoveryComplete?.Invoke();
        _wasRecovering = recovering;
    }

    // Re-check ONLY the emergency-hangup gate — wired to room-entity observations
    // so a hostile that wanders in or spawns while we're already below the trigger
    // fires the disconnect, even though nothing about our own PlayerState changed
    // to drive the normal Evaluate. Deliberately narrow: it must not run the
    // rest / run / flee machinery, which a room change would otherwise re-trigger
    // (e.g. spuriously re-issuing `rest`). Honours the same engine-off carve-out
    // as Evaluate — the hangup evaluates while auto-heal is off only when the user
    // opted into AllowHangupInAllOffMode.
    public void ReevaluateEmergencyHangup()
    {
        if (!_state.HasPromptData) return;
        if (!_isEnabled()
            && _readGeneralSettings?.Invoke() is not { AllowHangupInAllOffMode: true })
            return;
        TryEmergencyHangup(_readSettings());
    }

    // Hangup-on-emergency: HP at or below HealthSettings.HangIfBelowHp WITH a
    // hostile in the room triggers a hard disconnect via the configured Game-Exit
    // command. Latched so the command goes once per danger episode (not every tick
    // while HP stays low), and the log captures it for postmortem. The latch
    // re-arms as soon as the danger passes — HP back above the trigger, or the
    // room clear of hostiles — so a later low-HP-with-hostile crossing (e.g. after
    // reconnecting into a safe room, then a monster wanders in) fires afresh.
    // Defaults: HangIfBelowHp=5 (%). Called from the normal evaluate path, the
    // room-observation re-check (ReevaluateEmergencyHangup), and — when
    // GeneralSettings.AllowHangupInAllOffMode is set — the engine-disabled carve-out.
    //
    // The trigger is a point on one continuous HP scale — 100 %/max down through
    // 0 into the negatives (HP% goes negative while bleeding out, exactly as the
    // game's par display shows). So the trigger has no zero sentinel: 0 is a live
    // "hang the moment I drop" value, and negatives let the user hang up deep in
    // the bleeding-out band, closer to death. Turning the feature off is the
    // GeneralSettings.DisableHangups master switch's job, not a magic threshold.
    //
    // The fire window is (deathFloor, hangTrigger]: it stays live through the
    // bleeding-out zone below 0 HP because a dropped character can still hang up,
    // but bails once HP has fallen to or past the realm death floor — at that
    // point the character is already dead and there's nothing to disconnect
    // (this also guards against dead/respawned chars reading garbage HP). The
    // floor is clamped to <= 0: a misconfigured positive value collapses to 0.
    // A trigger resolved at or below the floor yields an empty window (never
    // fires) — the natural "never hang up" position at the bottom of the scale.
    //
    // Returns true only when it actually sent the disconnect this call, so the
    // Evaluate caller can short-circuit the rest of the recovery machinery. A
    // couldn't-send (no exit command configured) still latches _hangFired but
    // returns false, letting normal rest / flee run as a fallback.
    private bool TryEmergencyHangup(HealthSettings s)
    {
        // Master kill-switch: the user has declared only an explicit local
        // action may drop the carrier. Hard-overrides AllowHangupInAllOffMode —
        // an opted-out character won't auto-disconnect even at low HP.
        if (_readGeneralSettings?.Invoke() is { DisableHangups: true }) return false;
        if (_state.MaxHp <= 0) return false;

        int hangTrigger = PoolThreshold.Resolve(s.HpThresholdMode, s.HangIfBelowHp, _state.MaxHp);
        int deathFloor = Math.Min(0, _readDeathFloor?.Invoke() ?? -25);
        bool inWindow = _state.Hp > deathFloor && _state.Hp <= hangTrigger;

        // The disconnect is an escape from a fight that's killing us. With no
        // hostile in the room there's nothing to flee, so a low-HP character is
        // safe to stay connected and rest — dropping the carrier would only
        // strand it in a reconnect loop it can't heal out of (log back in still
        // below the trigger, hang up again, repeat). Gate on hostile presence and
        // re-arm the single-shot the moment the danger passes (HP recovered above
        // the trigger, or the room went clear) so a fresh hostile that wanders in
        // or spawns while we're still low fires a new disconnect. Selector unwired
        // (tests / minimal ctor) fails open — behaves as the pre-gate hangup did.
        bool hostile = _hasHostileInRoom?.Invoke() ?? true;
        if (!inWindow || !hostile)
        {
            _hangFired = false;
            return false;
        }
        if (_hangFired) return false;

        _hangFired = true;
        string? hangCmd = _readHangupCommand?.Invoke();
        if (string.IsNullOrWhiteSpace(hangCmd))
        {
            _log?.Warn(LogCategory,
                $"HANGUP threshold crossed (HP {_state.Hp}/{_state.MaxHp} <= {hangTrigger}) " +
                $"but no hangup command configured — set Settings → Other → Game Exit.");
            return false;
        }

        _log?.Warn(LogCategory,
            $"HANGUP — HP {_state.Hp}/{_state.MaxHp} <= hang-trigger={hangTrigger} cmd='{hangCmd}' " +
            "(sending exit, then closing carrier)");
        // Declare the drop intentional before it lands so MainWindowViewModel's
        // reactive-reconnect path stands down — otherwise the very disconnect we
        // just triggered gets classified as unexpected and immediately dialled back.
        _hangupSignal?.SignalHangup();
        // Route through the un-wrapped hangup sender so a low-HP hangup fires even
        // while the mortally-wounded EngineSendGate hold is up (that hold gates
        // every OTHER engine send, but the escape hangup must pierce it).
        SendHangup(hangCmd);
        // Don't wait for the server to notice the exit command — close the socket
        // ourselves so a stuck / slow drop can't leave the character connected.
        // The callback flushes the just-sent exit command before disposing.
        _requestHangupDisconnect?.Invoke();
        return true;
    }

    // Deferred flee reaction — runs one dispatch tick after the run-trigger
    // tripped, once the round's death line has settled. Stands down when the room
    // emptied in the meantime (the flee-triggering round also killed the last
    // monster): with no live hostile there's nothing to flee, so we stay and rest.
    // Otherwise dispatches the same action the synchronous path would have — a
    // party follower's @heal / hold, or a solo/leader TryFlee. `_fledThisCombat`
    // was latched at decision time; a room-clear resets it via the InCombat→false
    // Evaluate, so a fresh hostile that wanders in re-triggers the flee.
    private void CommitFleeReaction(bool hpRun, bool follower, string reason)
    {
        if (!_state.InCombat || !(_hasHostileInRoom?.Invoke() ?? true))
        {
            _log?.Combat(LogCategory,
                $"flee stood down — no live hostile once the round settled; resting ({reason})");
            return;
        }
        if (follower)
        {
            if (hpRun)
            {
                _log?.Combat(LogCategory,
                    $"party follower low HP — requesting heal instead of fleeing ({reason})");
                _requestPartyHeal!();
            }
            else
            {
                _log?.Combat(LogCategory,
                    $"party follower low MA — holding (no solo flee; heal can't restore mana) ({reason})");
            }
            return;
        }
        TryFlee(reason);
    }

    // Public entry for CombatManager's backstab-failure flee (wired via
    // Combat.SetBackstabFailureFlee). Routes through the shared TryFlee, which
    // requires an active movement engine and honors BreakBeforeFleeing /
    // RunDirection / RunDistance — so a hand-walked failure just logs and no-ops.
    public void RunFromBackstabFailure() => TryFlee("backstab failed");

    // Try to begin a flee. No-ops (with a log line) when no movement engine is
    // active or when no flee direction can be resolved. On success it pauses the
    // engine, queues the full flee route, optionally sends `break`, and dispatches
    // the first step; the remaining steps advance one per NoteRoomChanged.
    private void TryFlee(string reason)
    {
        Map.IRecoverableEngine? engine = _getActiveMovementEngine?.Invoke();
        if (engine is null)
        {
            _log?.Combat(LogCategory,
                $"flee skipped (no active movement engine) — {reason}");
            return;
        }

        Models.Profile.CombatSettings combat = _readCombatSettings?.Invoke()
            ?? new Models.Profile.CombatSettings();

        List<Map.Direction> steps = BuildFleeSteps(engine, combat);
        if (steps.Count == 0)
        {
            _log?.Warn(LogCategory,
                $"flee skipped (couldn't resolve {combat.RunDirection} route) — {reason}");
            return;
        }

        // Pause the engine first so it doesn't queue planned steps
        // on top of our flee moves. Engine resumes via
        // ResumeAfterRecovery when HP climbs back above the
        // run-trigger (handled in Evaluate's recovery branch).
        engine.PauseForRecovery($"flee — {reason}");

        _fleeEngine = engine;
        _fleeQueue.Clear();
        foreach (Map.Direction d in steps) _fleeQueue.Enqueue(d);

        if (combat.BreakBeforeFleeing)
            SendCommand("break");

        Map.Direction first = _fleeQueue.Dequeue();
        _log?.Combat(LogCategory,
            $"flee start engine={engine.Name} mode={combat.RunDirection} " +
            $"route=[{string.Join(",", steps)}] first={first} ({reason})");
        engine.SendBacktrackMove(first);
    }

    // Resolve the ordered list of directions the flee will walk. Backward mode
    // (the default) runs BFS from the current room back to the engine's fixed
    // JourneyOrigin and takes the first RunDistance directions — the reverse of
    // the trail we came in on, which always heads away from the fight. It falls
    // back to a single inverted last-move when the reverse path can't be computed
    // (no origin, unknown current room, or no reverse-path selector / graph).
    // Forward mode walks the engine's own next RunDistance planned moves — it
    // keeps heading toward the destination instead of retreating.
    private List<Map.Direction> BuildFleeSteps(
        Map.IRecoverableEngine engine, Models.Profile.CombatSettings combat)
    {
        int distance = combat.RunDistance;
        if (distance < 1) distance = 1;

        var steps = new List<Map.Direction>();
        switch (combat.RunDirection)
        {
            case Models.Profile.RunDirection.Backward:
                if (_findReversePath is not null
                    && _lastKnownRoom is { } from
                    && engine.JourneyOrigin is { } origin
                    && !from.Equals(origin)
                    && _findReversePath(from, origin) is { Count: > 0 } path)
                {
                    for (int i = 0; i < path.Count && i < distance; i++)
                        steps.Add(path[i]);
                }
                else if (Reverse(_getLastSentDirection?.Invoke()) is { } back)
                {
                    // No map to plan a multi-room retreat — step back into the
                    // room we just left (known to exist) and stop there rather
                    // than blindly repeating one direction into a wall.
                    steps.Add(back);
                }
                break;
            case Models.Profile.RunDirection.Forward:
                // "Go backwards if running" is OFF — keep pressing along the
                // engine's own planned route toward its destination. Walk the
                // next RunDistance moves it would have sent anyway rather than
                // repeating a single direction into a wall on the first turn.
                steps.AddRange(engine.PeekPlannedDirections(distance));
                break;
        }
        return steps;
    }

    private static Map.Direction? Reverse(Map.Direction? d) => d switch
    {
        Map.Direction.N  => Map.Direction.S,
        Map.Direction.S  => Map.Direction.N,
        Map.Direction.E  => Map.Direction.W,
        Map.Direction.W  => Map.Direction.E,
        Map.Direction.NE => Map.Direction.SW,
        Map.Direction.SW => Map.Direction.NE,
        Map.Direction.NW => Map.Direction.SE,
        Map.Direction.SE => Map.Direction.NW,
        Map.Direction.U  => Map.Direction.D,
        Map.Direction.D  => Map.Direction.U,
        _ => null,
    };

    private string ChooseRestCommand(HealthSettings s)
    {
        // No meditate ability → always rest.
        if (!s.UseMeditateAbility) return "rest";

        bool needsHp = _hpGateAsserted;
        bool needsMa = _maGateAsserted;

        if (needsMa && !needsHp) return "meditate";
        if (needsHp && needsMa && s.MeditateBeforeResting) return "meditate";
        // Default: rest covers both pools for most classes; user can
        // flip MeditateBeforeResting for casters where mana recovery
        // matters more than HP catchup.
        return "rest";
    }

    // True when a follower riding the leader's rest downtime still has something
    // to top off — either pool sitting below its rest-max. Goes false once both
    // pools reach rest-max, which trips the shared recovery branch (post-rest
    // chain + latch clear). Guards each pool on Max > 0 so a class with no mana
    // pool never reports a phantom MA deficit before prompt data loads.
    private bool NeedsOpportunisticTopOff(HealthSettings s)
    {
        int hpTarget = PoolThreshold.Resolve(s.HpThresholdMode, s.RestMaxHp, _state.MaxHp);
        int maTarget = PoolThreshold.Resolve(s.MaThresholdMode, s.RestMaxMa, _state.MaxMa);
        bool needHp = _state.MaxHp > 0 && _state.Hp < hpTarget;
        bool needMa = _state.MaxMa > 0 && _state.Ma < maTarget;
        return needHp || needMa;
    }

    // Rest-vs-meditate pick for the opportunistic (leader-resting) path: with no
    // meditate ability it's always rest; otherwise meditate when "meditate before
    // resting" is set and we're short any mana, else meditate when our mana% is
    // below our hp% (recover the more-depleted pool first), else rest. Distinct
    // from ChooseRestCommand, which reads the asserted gates — here no gate is
    // held, so the choice is driven by live pool fill.
    private string ChooseOpportunisticRestCommand(HealthSettings s)
    {
        if (!s.UseMeditateAbility) return "rest";

        bool missingMana = _state.MaxMa > 0 && _state.Ma < _state.MaxMa;
        if (s.MeditateBeforeResting && missingMana) return "meditate";

        double hpPct = _state.MaxHp > 0 ? _state.Hp * 100.0 / _state.MaxHp : 100.0;
        double maPct = _state.MaxMa > 0 ? _state.Ma * 100.0 / _state.MaxMa : 100.0;
        return maPct < hpPct ? "meditate" : "rest";
    }

    // Called by an external observer (RoomTracker via AppServices) when the
    // player's location changes. Server-side resting state is auto-cleared on
    // move, so our _restInFlight latch must drop too — otherwise the next
    // recovery cycle would skip the rest emit because we'd still think we were
    // sitting.
    // The idle-stall watchdog force-cleared combat optimistically (it sent a resync
    // CR and is waiting on the re-display to self-heal). Hold the rest-out branch
    // until that re-display re-confirms the room, so we don't rest in the flicker
    // before a still-present monster re-asserts.
    public void NoteCombatForceCleared()
    {
        _restHeldPendingReconfirm = true;
        _restHoldSetAt = _now();
    }

    // A room observation arrived after a force-clear — the room model is now
    // authoritative. Release the hold and re-evaluate: a hostile that re-appeared
    // is now reflected in HasEngageableHostiles (this is wired after the combat
    // tracker's own EntitiesObserved handler), so the hostiles guard blocks the
    // rest; a genuinely empty room lets the held rest through.
    public void NoteRoomEntitiesReconfirmed()
    {
        if (!_restHeldPendingReconfirm) return;
        _restHeldPendingReconfirm = false;
        Evaluate();
    }

    public void NoteRoomChanged() => NoteRoomChanged(newRoom: null);

    // Overload that captures the new room key so the flee path can (a) step its
    // multi-move queue on every arrival and (b) call
    // IRecoverableEngine.ResumeAfterRecovery with the correct anchor once HP
    // recovers.
    public void NoteRoomChanged(Map.RoomKey? newRoom)
    {
        if (newRoom is { } r) _lastKnownRoom = r;

        // Flee step continuation — fire BEFORE the rest-latch reset
        // so the engine's pause flag doesn't get cleared by a
        // racing post-flee rest cycle.
        if (_fleeEngine is not null && _fleeQueue.Count > 0)
        {
            Map.Direction next = _fleeQueue.Dequeue();
            _fleeEngine.SendBacktrackMove(next);
            _log?.Combat(LogCategory,
                $"flee step engine={_fleeEngine.Name} dir={next} " +
                $"remaining={_fleeQueue.Count}");
        }

        if (_restInFlight)
        {
            _restInFlight = false;
            _restConfirmedByPrompt = false;
            _log?.Combat(LogCategory, "rest-in-flight cleared on room change");
        }

        // A move re-observes the room, so any post-force-clear rest hold is resolved
        // by the new room's observation — drop it here too (covers the dark-room
        // force-clear that sends no resync CR).
        _restHeldPendingReconfirm = false;

        // Re-arm a rest that a do-not-rest room forced us to skip: the hop out of
        // that room carries no prompt change to re-run Evaluate on its own, so do
        // it once here. Skipped mid-flee — the flee queue above drives its own
        // arrivals and a rest re-arm would fight it. Evaluate re-sets the latch if
        // the new room is ALSO a do-not-rest room with a deficit.
        if (_skipRestDeferredRecovery && _fleeEngine is null)
        {
            _skipRestDeferredRecovery = false;
            Evaluate();
        }
    }

    // Send pre-/post-rest chain — split on ; or ^M / newline (the documented
    // HealthSettings convention), trim each fragment, send each as its own wire
    // line. Empty / whitespace-only input is a no-op so leaving the field blank
    // just skips the pre/post phase.
    private void SendChained(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        // Normalise `^M` to a newline so the single split below handles
        // both chaining markers.
        string normalised = raw.Replace("^M", "\n", StringComparison.OrdinalIgnoreCase);
        foreach (string part in normalised.Split(CommandChainSplit,
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            SendCommand(part);
        }
    }

    private void SendCommand(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_wireSender is null) return;
        byte[] bytes = Encoding.Latin1.GetBytes(text + "\r");
        _wireSender(bytes);
    }

    // Emergency-hangup send. Prefers the un-wrapped hangup sender (which bypasses
    // EngineSendGate) so it fires even while a hold is up; falls back to the
    // ordinary wrapped sender when no hangup sender was bound (tests / pre-wire).
    private void SendHangup(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Action<byte[]>? sender = _hangupWireSender ?? _wireSender;
        if (sender is null) return;
        byte[] bytes = Encoding.Latin1.GetBytes(text + "\r");
        sender(bytes);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.PropertyChanged -= OnStateChanged;
    }
}
