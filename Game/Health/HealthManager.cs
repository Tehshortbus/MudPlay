using System.ComponentModel;
using System.Text;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Health;

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
// mid-combat AND a movement engine is active, the active engine is paused and the
// character flees CombatSettings.RunDistance rooms, optionally preceded by
// `break`. Backward mode (the default) runs BFS from the current room back to the
// active engine's JourneyOrigin and walks the first RunDistance directions of that
// path — the reverse of the trail we came in on. Anchoring on the fixed origin is
// what keeps the retreat heading away from the fight instead of bouncing back into
// it. When the reverse path can't be computed (no origin / unknown room / no graph)
// it falls back to inverting the last sent direction for a single step. Forward
// mode ("go backwards if running" off) instead keeps pressing along the engine's
// own planned route toward its destination — the next RunDistance moves it would
// have sent anyway. The engine resumes via IRecoverableEngine.ResumeAfterRecovery
// once HP climbs back above the run-trigger. Multi-step flee advances one queued
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
    private readonly Func<int>? _readDeathFloor;
    private readonly HangupSignal? _hangupSignal;
    private readonly LogService? _log;

    private Action<byte[]>? _wireSender;
    private Action<byte[]>? _hangupWireSender;  // un-wrapped: pierces EngineSendGate
    private Func<bool>? _isPartyFollower;       // in a party AND not the leader
    private Action? _requestPartyWait;          // ping leader to halt (PartyRestSync)
    private Action? _requestPartyOk;            // release leader
    private Func<bool>? _isLeaderResting;       // follower + leader is resting/meditating
    private Action? _requestPartyHeal;          // follower flee-substitute: broadcast @heal
    private bool _partyWaitSignaled;            // @wait sent, awaiting @ok
    private bool _hpGateAsserted;
    private bool _maGateAsserted;
    private bool _restInFlight;          // sent rest, awaiting recovery
    private bool _restConfirmedByPrompt; // observed (Resting) since the last rest emit
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
        Func<Map.RoomKey, Map.RoomKey, IReadOnlyList<Map.Direction>?>? findReversePath = null)
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
    // downtime — inherent behavior, gated only by the auto-heal master switch.
    // Left null preserves the old gate-only rest behavior.
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
        Action? requestPartyHeal = null)
    {
        ArgumentNullException.ThrowIfNull(isPartyFollower);
        ArgumentNullException.ThrowIfNull(requestPartyWait);
        ArgumentNullException.ThrowIfNull(requestPartyOk);
        _isPartyFollower = isPartyFollower;
        _requestPartyWait = requestPartyWait;
        _requestPartyOk = requestPartyOk;
        _isLeaderResting = isLeaderResting;
        _requestPartyHeal = requestPartyHeal;
    }

    // True while the HP gate is held.
    public bool HpGateAsserted => _hpGateAsserted;

    // True while the MA gate is held.
    public bool MaGateAsserted => _maGateAsserted;

    // True between the rest emit and the corresponding stand emit.
    public bool RestInFlight => _restInFlight;

    // True between the run-if-below reaction (a flee for leader / solo, or a
    // broadcast @heal for a party follower) and the next time
    // PlayerState.InCombat goes false. Single-shot per combat so a low-HP fight
    // can't burn the reaction on every HP-changed event.
    public bool FledThisCombat => _fledThisCombat;

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
        // latch so we don't race the (Resting) prompt arrival:
        //   1. We send `rest` and set _restInFlight=true.
        //   2. On the FIRST Evaluate tick where Position==Resting, we
        //      flip _restConfirmedByPrompt=true — the server has put
        //      us into the resting state.
        //   3. Any subsequent tick where Position!=Resting (server
        //      broke our rest because we took damage, entered combat,
        //      or moved) drops _restInFlight so the rest-out branch
        //      below re-fires.
        // Without step 2, a fast follow-up HP-changed tick that fires
        // before the (Resting) prompt arrives would spuriously clear
        // _restInFlight and double-send `rest`.
        // The re-issue is gated on !InCombat — while a hostile is
        // engaging us we let CombatManager handle the swing; the
        // moment combat clears (CombatStateTracker flips InCombat
        // false), Evaluate ticks again and the rest goes out.
        if (_restInFlight && _state.Position == PlayerPosition.Resting)
        {
            _restConfirmedByPrompt = true;
        }
        else if (_restInFlight && _restConfirmedByPrompt
                              && _state.Position != PlayerPosition.Resting)
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

        // ----- HP gate transitions ---------------------------------
        int hpRestTrigger = PoolThreshold.Resolve(s.HpThresholdMode, s.RestIfBelowHp, _state.MaxHp);
        int hpRestTarget  = follower
            ? Math.Min(hpRestTrigger + 1, _state.MaxHp)
            : PoolThreshold.Resolve(s.HpThresholdMode, s.RestMaxHp, _state.MaxHp);

        // Strictly below — "rest if below N" rests only when the pool is
        // under N, never AT N. (Equal-or-less traps a level-2 mystic: 1 max
        // KAI, trigger 0, spend the KAI → MA 0 == trigger 0 would pause for
        // mana forever.)
        if (!_hpGateAsserted && _state.MaxHp > 0 && _state.Hp < hpRestTrigger)
        {
            _hpGateAsserted = true;
            _coordinator.AssertGate(MovementCoordinator.HealthRecoveryGate,
                AsserterName,
                $"HP {_state.Hp}/{_state.MaxHp} < rest-trigger={hpRestTrigger}");
        }
        else if (_hpGateAsserted && _state.Hp >= hpRestTarget)
        {
            _hpGateAsserted = false;
            _coordinator.ClearGate(MovementCoordinator.HealthRecoveryGate,
                AsserterName,
                $"HP {_state.Hp}/{_state.MaxHp} >= rest-target={hpRestTarget}");
        }

        // ----- MA gate transitions ---------------------------------
        int maRestTrigger = PoolThreshold.Resolve(s.MaThresholdMode, s.RestIfBelowMa, _state.MaxMa);
        int maRestTarget  = follower
            ? Math.Min(maRestTrigger + 1, _state.MaxMa)
            : PoolThreshold.Resolve(s.MaThresholdMode, s.RestMaxMa, _state.MaxMa);

        // Strictly below (see HP gate above) — the mystic-at-level-2 case.
        if (!_maGateAsserted && _state.Ma < maRestTrigger && _state.MaxMa > 0)
        {
            _maGateAsserted = true;
            _coordinator.AssertGate(MovementCoordinator.ManaRecoveryGate,
                AsserterName,
                $"MA {_state.Ma}/{_state.MaxMa} < rest-trigger={maRestTrigger}");
        }
        else if (_maGateAsserted && _state.Ma >= maRestTarget)
        {
            _maGateAsserted = false;
            _coordinator.ClearGate(MovementCoordinator.ManaRecoveryGate,
                AsserterName,
                $"MA {_state.Ma}/{_state.MaxMa} >= rest-target={maRestTarget}");
        }

        // ----- party-follower @wait / @ok --------------------------
        // While a recovery gate is held we're below a floor — ping the
        // leader to halt; release once both clear. PartyRestSync self-
        // gates on membership, so these are no-ops solo or as leader;
        // the latch just avoids redundant telepaths.
        bool recovering = _hpGateAsserted || _maGateAsserted;
        if (recovering && !_partyWaitSignaled)
        {
            _partyWaitSignaled = true;
            _requestPartyWait?.Invoke();
        }
        else if (!recovering && _partyWaitSignaled)
        {
            _partyWaitSignaled = false;
            _requestPartyOk?.Invoke();
        }

        // ----- flee on critical HP/MA mid-combat -------------------
        // Run-if-below: HP-only per user direction. Fires only when a
        // movement engine is active — "if you aren't running a
        // movement engine, the flee-if-below wouldn't fire". On
        // trigger: optionally send `break` to disengage combat, then
        // begin a multi-step flee over CombatSettings.RunDistance rooms
        // (Backward = the reverse-BFS trail toward the engine's
        // JourneyOrigin; Forward = the engine's own next planned moves
        // toward its destination). Subsequent
        // steps advance one per NoteRoomChanged; the paused engine
        // auto-resumes once HP climbs back above the run-trigger
        // (recovery branch below).
        if (!_state.InCombat)
        {
            _fledThisCombat = false;
        }
        else if (!_fledThisCombat)
        {
            int hpRunTrigger = PoolThreshold.Resolve(s.HpThresholdMode, s.RunIfBelowHp, _state.MaxHp);
            bool hpRun = _state.MaxHp > 0 && _state.Hp > 0 && _state.Hp <= hpRunTrigger;
            if (hpRun)
            {
                _fledThisCombat = true;
                string reason = $"HP {_state.Hp}/{_state.MaxHp} <= run-trigger={hpRunTrigger}";
                // A party follower must NOT run off alone — that breaks party
                // formation and strands them. Instead broadcast @heal so the
                // party healer tops us up; we stay put. The leader owns the
                // party's run decision, and solo characters just flee. TryFlee
                // itself already no-ops when no movement engine is active (i.e.
                // when idle), so the leader/solo path only runs when "not idle".
                if (_requestPartyHeal is not null && (_isPartyFollower?.Invoke() ?? false))
                {
                    _log?.Combat(LogCategory,
                        $"party follower low HP — requesting heal instead of fleeing ({reason})");
                    _requestPartyHeal();
                }
                else
                {
                    TryFlee(reason);
                }
            }
        }

        // Auto-resume — when a fled engine is paused AND HP has
        // climbed back above the run-trigger AND no more flee steps
        // are queued, hand control back to the engine. Backward
        // mode retraces its path from the current room; Forward
        // continues toward the original destination.
        if (_fleeEngine is not null && _fleeQueue.Count == 0 && _state.MaxHp > 0)
        {
            int hpRunTrigger = PoolThreshold.Resolve(s.HpThresholdMode, s.RunIfBelowHp, _state.MaxHp);
            if (_state.Hp > hpRunTrigger && _lastKnownRoom is { } room)
            {
                _log?.Combat(LogCategory,
                    $"flee complete — resuming engine={_fleeEngine.Name} at {room} " +
                    $"(HP {_state.Hp}/{_state.MaxHp} > run-trigger={hpRunTrigger})");
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

        // Opportunistic follower rest: the leader has stopped to rest /
        // meditate, so we use the downtime to top off too — even above our
        // own rest-trigger floors, up to rest-max. No gate is asserted (we're
        // not below a floor, so we must NOT @wait a leader who's already
        // voluntarily halted, and we don't hold the movement gate). It only
        // engages when there's actually something to recover; once both pools
        // hit rest-max NeedsOpportunisticTopOff goes false and the post-rest
        // chain fires through the shared !shouldRest recovery branch.
        bool opportunistic = !anyGate
            && (_isLeaderResting?.Invoke() ?? false)
            && NeedsOpportunisticTopOff(s);
        bool shouldRest = anyGate || opportunistic;

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

        if (shouldRest && !_state.InCombat && !_restInFlight && !hostilesPresent)
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

            SendChained(s.PreRestCommand);
            SendCommand(command);
            _log?.Combat(LogCategory,
                $"{command}{(anyGate ? "" : " (opportunistic, leader resting)")} " +
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
            $"HANGUP — HP {_state.Hp}/{_state.MaxHp} <= hang-trigger={hangTrigger} cmd='{hangCmd}'");
        // Declare the drop intentional before it lands so MainWindowViewModel's
        // reactive-reconnect path stands down — otherwise the very disconnect we
        // just triggered gets classified as unexpected and immediately dialled back.
        _hangupSignal?.SignalHangup();
        // Route through the un-wrapped hangup sender so a low-HP hangup fires even
        // while the mortally-wounded EngineSendGate hold is up (that hold gates
        // every OTHER engine send, but the escape hangup must pierce it).
        SendHangup(hangCmd);
        return true;
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

        if (!_restInFlight) return;
        _restInFlight = false;
        _restConfirmedByPrompt = false;
        _log?.Combat(LogCategory, "rest-in-flight cleared on room change");
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
