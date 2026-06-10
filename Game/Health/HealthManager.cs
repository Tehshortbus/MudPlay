using System.ComponentModel;
using System.Text;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Health;

/// <summary>
/// Phase 9 PR 9.B — passive HP/MA threshold behavior. Asserts and
/// clears <see cref="MovementCoordinator.HealthRecoveryGate"/> +
/// <see cref="MovementCoordinator.ManaRecoveryGate"/> on configured
/// thresholds and drives the rest / stand cycle with pre- / post-rest
/// command sequencing. Does NOT decide spell casts — those route
/// through <c>CastingDirector</c> (PR 9.D).
/// </summary>
/// <remarks>
/// <para>
/// State model — three transitions per pool (HP and MA each track
/// independently):
/// </para>
/// <list type="bullet">
/// <item><b>Threshold breach</b>: HP / MA drops to or below the
/// configured rest-trigger. Asserts the corresponding recovery gate.
/// Walker (and any other gate consumer) pauses immediately.</item>
/// <item><b>Rest-out</b>: when either gate is held AND the player is
/// out of combat (<see cref="PlayerState.InCombat"/> false), send any
/// configured pre-rest command(s) and then <c>rest</c>. Idempotent —
/// won't re-send rest while one is already in flight.</item>
/// <item><b>Recovery complete</b>: both pools have climbed to or past
/// their configured rest-target. Clears both gates, sends
/// <c>stand</c>, and emits any post-rest command(s). Walker resumes
/// when the last gate clears.</item>
/// </list>
/// <para>
/// In-combat semantics: the HP/MA gates can assert mid-fight (so the
/// walker doesn't try to leave the room when a fight is going badly),
/// but <c>rest</c> is NEVER sent while <see cref="PlayerState.InCombat"/>
/// is true. As soon as <c>CombatStateTracker</c> clears the
/// <see cref="MovementCoordinator.CombatGate"/> and InCombat flips
/// false, the next <see cref="Evaluate"/> tick fires the rest command.
/// </para>
/// <para>
/// Pre/post-rest commands honour the <c>^M</c>-or-<c>;</c> chaining
/// convention documented on
/// <see cref="HealthSettings.PreRestCommand"/>: split the string on
/// either marker, trim each fragment, send each as its own wire line.
/// </para>
/// <para>
/// Run-if-below and hang-if-below thresholds are <b>deferred</b> from
/// this first cut — flee behaviour needs walker integration + room-
/// adjacency picking, and emergency hangup needs the telnet
/// disconnect path. Both land in a follow-up commit on this branch
/// before user smoke testing.
/// </para>
/// </remarks>
public sealed class HealthManager : IDisposable
{
    /// <summary>LogService category — appears as <c>[Health]</c> rows
    /// per assert / clear / rest / stand decision.</summary>
    public const string LogCategory = "Health";

    /// <summary>Identifier the HealthManager uses when flipping the
    /// HealthRecovery / ManaRecovery gates. Surfaces in
    /// <see cref="MovementCoordinator.History"/>.</summary>
    public const string AsserterName = "HealthManager";

    private static readonly char[] CommandChainSplit = new[] { ';', '\n' };

    private readonly PlayerState _state;
    private readonly MovementCoordinator _coordinator;
    private readonly Func<HealthSettings> _readSettings;
    private readonly Func<bool> _isEnabled;
    private readonly Func<string>? _readHangupCommand;
    private readonly Func<Map.IRecoverableEngine?>? _getActiveMovementEngine;
    private readonly Func<Map.Direction?>? _getLastSentDirection;
    private readonly Func<Models.Profile.OtherSettings>? _readOtherSettings;
    private readonly Func<Models.Profile.CombatSettings>? _readCombatSettings;
    private readonly Func<bool>? _hasEngageableHostiles;
    private readonly LogService? _log;

    private Action<byte[]>? _wireSender;
    private Func<bool>? _isPartyFollower;       // in a party AND not the leader
    private Action? _requestPartyWait;          // ping leader to halt (PartyRestSync)
    private Action? _requestPartyOk;            // release leader
    private bool _partyWaitSignaled;            // @wait sent, awaiting @ok
    private bool _hpGateAsserted;
    private bool _maGateAsserted;
    private bool _restInFlight;          // sent rest, awaiting recovery
    private bool _restConfirmedByPrompt; // observed (Resting) since the last rest emit
    private bool _fledThisCombat;        // sent flee, awaiting combat to end
    private bool _hangFired;             // disconnect-on-emergency single-shot per session
    private Map.IRecoverableEngine? _fleeEngine;     // engine we paused mid-flee
    private Map.Direction _fleeDirection;             // sustained direction for multi-step flee
    private int _fleeStepsRemaining;                 // moves left before flee completes
    private Map.RoomKey? _lastKnownRoom;             // updated on every NoteRoomChanged
    private bool _disposed;

    public HealthManager(
        PlayerState state,
        MovementCoordinator coordinator,
        Func<HealthSettings> readSettings,
        Func<bool> isEnabled,
        LogService? log = null)
        : this(state, coordinator, readSettings, isEnabled, readHangupCommand: null, log) { }

    /// <summary>
    /// Constructor with a <c>readHangupCommand</c> selector so the
    /// hangup-on-emergency path uses the user's configured exit
    /// command (typically <c>=x</c> or <c>;o</c>, set in Settings →
    /// Other → Game Exit). Without it, the hangup path no-ops with a
    /// log warning. AppServices wires
    /// <c>() =&gt; GameCommands.ExitCommand</c>.
    /// </summary>
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
               readOtherSettings: null,
               readCombatSettings: null,
               hasEngageableHostiles: null,
               log) { }

    /// <summary>
    /// Full constructor. The three additional selectors wire the
    /// flee path:
    /// <list type="bullet">
    /// <item><c>getActiveMovementEngine</c> — returns the
    /// <see cref="Map.IRecoverableEngine"/> that's currently running
    /// (Walker / Loop / AutoLair are exclusive). Returns <c>null</c>
    /// when no engine is active — flee then no-ops since "if you
    /// aren't running a movement engine, the flee-if-below wouldn't
    /// fire" per user direction.</item>
    /// <item><c>getLastSentDirection</c> — most recent outbound
    /// direction, inverted for the Backward flee mode. Typically wired
    /// to the last entry on <c>EngineRecoveryGate.ExecutedSinceAnchor</c>.</item>
    /// <item><c>readOtherSettings</c> — for
    /// <see cref="Models.Profile.OtherSettings.RunDirection"/> and
    /// <see cref="Models.Profile.OtherSettings.BreakBeforeFleeing"/>.</item>
    /// <item><c>hasEngageableHostiles</c> — returns true while the room
    /// contains at least one engageable monster. Gates the rest-out
    /// branch so we don't spam <c>rest</c> every tick while a hostile
    /// keeps breaking it (per user direction: "if a room has hostiles
    /// it will break resting every combat round preventing you from
    /// resting, so you need to clear the room and then rest"). Typically
    /// wired to <see cref="CombatStateTracker.HasEngageableHostiles"/>.</item>
    /// </list>
    /// </summary>
    public HealthManager(
        PlayerState state,
        MovementCoordinator coordinator,
        Func<HealthSettings> readSettings,
        Func<bool> isEnabled,
        Func<string>? readHangupCommand,
        Func<Map.IRecoverableEngine?>? getActiveMovementEngine,
        Func<Map.Direction?>? getLastSentDirection,
        Func<Models.Profile.OtherSettings>? readOtherSettings,
        Func<Models.Profile.CombatSettings>? readCombatSettings,
        Func<bool>? hasEngageableHostiles,
        LogService? log = null)
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
        _readOtherSettings = readOtherSettings;
        _readCombatSettings = readCombatSettings;
        _hasEngageableHostiles = hasEngageableHostiles;
        _log = log;
        _state.PropertyChanged += OnStateChanged;
    }

    /// <summary>Bind the wire sender. Until set, the engine logs
    /// decisions but doesn't actually send <c>rest</c> / <c>stand</c>
    /// / pre- / post-rest commands.</summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    /// <summary>
    /// Wire party-role-aware recovery. <paramref name="isPartyFollower"/>
    /// returns true when the local character is following a party leader
    /// (in a party AND not the leader). While following:
    /// <list type="bullet">
    /// <item>The recovery gates clear as soon as a pool climbs just past
    /// the rest-trigger floor (target = trigger + 1) rather than the full
    /// rest-max — a follower tops off to safety, not to full, so it
    /// doesn't hold the party for a routine heal. The party healer / leader
    /// owns full topoff.</item>
    /// <item><paramref name="requestPartyWait"/> fires when a recovery gate
    /// first asserts (dropped below the floor → ping the leader to halt);
    /// <paramref name="requestPartyOk"/> fires when the last gate clears
    /// (back above the floor → release the leader).</item>
    /// </list>
    /// Until wired — or when not following — recovery targets rest-max and
    /// no party signals are emitted (solo / leader behavior). The callbacks
    /// (typically <see cref="PartyRestSync.RequestWait"/> /
    /// <see cref="PartyRestSync.RequestOk"/>) self-gate on party membership,
    /// so invoking them solo is a safe no-op.
    /// </summary>
    public void SetPartyRoleSync(
        Func<bool> isPartyFollower,
        Action requestPartyWait,
        Action requestPartyOk)
    {
        ArgumentNullException.ThrowIfNull(isPartyFollower);
        ArgumentNullException.ThrowIfNull(requestPartyWait);
        ArgumentNullException.ThrowIfNull(requestPartyOk);
        _isPartyFollower = isPartyFollower;
        _requestPartyWait = requestPartyWait;
        _requestPartyOk = requestPartyOk;
    }

    /// <summary>True while the HP gate is held.</summary>
    public bool HpGateAsserted => _hpGateAsserted;

    /// <summary>True while the MA gate is held.</summary>
    public bool MaGateAsserted => _maGateAsserted;

    /// <summary>True between the <c>rest</c> emit and the corresponding
    /// <c>stand</c> emit.</summary>
    public bool RestInFlight => _restInFlight;

    /// <summary>True between the <c>flee</c> emit and the next time
    /// <see cref="PlayerState.InCombat"/> goes false. Single-shot per
    /// combat so a low-HP fight can't burn a flee command on every
    /// HP-changed event.</summary>
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

    /// <summary>
    /// Re-evaluate gate state + rest/stand pacing against the current
    /// player state. Public so tests can drive it deterministically
    /// without needing a real PropertyChanged firing.
    /// </summary>
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
            return;
        }
        if (!_state.HasPromptData) return;
        // Defensive: in real use PromptParser writes Hp + MaxHp before
        // flipping HasPromptData, so Hp == 0 here means either the
        // character is genuinely dead OR a producer races the
        // ordering. Either way a zero-on-zero comparison would assert
        // spuriously. Skip until the first real value lands —
        // DeathLineWatcher (PR 9.0d) handles the actual dead case via
        // PlayerDied + recovery routing, which doesn't need a rest
        // gate.
        if (_state.Hp <= 0 && _state.Ma <= 0) return;
        HealthSettings s = _readSettings();

        // Rest-interruption recovery (mirrors MudProxy's
        // OnRestingStateChanged). Two-step latch so we don't race the
        // (Resting) prompt arrival:
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
            _log?.Info(LogCategory,
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
        int hpRestTrigger = ResolveThreshold(s.HpThresholdMode, s.RestIfBelowHp, _state.MaxHp);
        int hpRestTarget  = follower
            ? Math.Min(hpRestTrigger + 1, _state.MaxHp)
            : ResolveThreshold(s.HpThresholdMode, s.RestMaxHp, _state.MaxHp);

        if (!_hpGateAsserted && _state.MaxHp > 0 && _state.Hp <= hpRestTrigger)
        {
            _hpGateAsserted = true;
            _coordinator.AssertGate(MovementCoordinator.HealthRecoveryGate,
                AsserterName,
                $"HP {_state.Hp}/{_state.MaxHp} <= rest-trigger={hpRestTrigger}");
        }
        else if (_hpGateAsserted && _state.Hp >= hpRestTarget)
        {
            _hpGateAsserted = false;
            _coordinator.ClearGate(MovementCoordinator.HealthRecoveryGate,
                AsserterName,
                $"HP {_state.Hp}/{_state.MaxHp} >= rest-target={hpRestTarget}");
        }

        // ----- MA gate transitions ---------------------------------
        int maRestTrigger = ResolveThreshold(s.MaThresholdMode, s.RestIfBelowMa, _state.MaxMa);
        int maRestTarget  = follower
            ? Math.Min(maRestTrigger + 1, _state.MaxMa)
            : ResolveThreshold(s.MaThresholdMode, s.RestMaxMa, _state.MaxMa);

        if (!_maGateAsserted && _state.Ma <= maRestTrigger && _state.MaxMa > 0)
        {
            _maGateAsserted = true;
            _coordinator.AssertGate(MovementCoordinator.ManaRecoveryGate,
                AsserterName,
                $"MA {_state.Ma}/{_state.MaxMa} <= rest-trigger={maRestTrigger}");
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
        // trigger: optionally send `break` to disengage combat,
        // then send one directional move (Backward = inverse of
        // last sent direction; Forward = engine's next planned).
        // Multi-step over CombatSettings.RunDistance + auto-resume
        // on HP recovery defer to a follow-up — v1 ships the
        // one-step + pause foundation.
        if (!_state.InCombat)
        {
            _fledThisCombat = false;
        }
        else if (!_fledThisCombat)
        {
            int hpRunTrigger = ResolveThreshold(s.HpThresholdMode, s.RunIfBelowHp, _state.MaxHp);
            bool hpRun = _state.MaxHp > 0 && _state.Hp > 0 && _state.Hp <= hpRunTrigger;
            if (hpRun)
            {
                _fledThisCombat = true;
                TryFlee($"HP {_state.Hp}/{_state.MaxHp} <= run-trigger={hpRunTrigger}");
            }
        }

        // Auto-resume — when a fled engine is paused AND HP has
        // climbed back above the run-trigger AND no more flee steps
        // are queued, hand control back to the engine. Backward
        // mode retraces its path from the current room; Forward
        // continues toward the original destination.
        if (_fleeEngine is not null && _fleeStepsRemaining <= 0 && _state.MaxHp > 0)
        {
            int hpRunTrigger = ResolveThreshold(s.HpThresholdMode, s.RunIfBelowHp, _state.MaxHp);
            if (_state.Hp > hpRunTrigger && _lastKnownRoom is { } room)
            {
                _log?.Info(LogCategory,
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

        if (anyGate && !_state.InCombat && !_restInFlight && !hostilesPresent)
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
            string command = ChooseRestCommand(s);

            SendChained(s.PreRestCommand);
            SendCommand(command);
            _log?.Info(LogCategory,
                $"{command} hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa}");
            _restInFlight = true;
        }
        else if (!anyGate && _restInFlight)
        {
            SendChained(s.PostRestCommand);
            _log?.Info(LogCategory,
                $"recovered hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa}");
            _restInFlight = false;
            _restConfirmedByPrompt = false;
        }

        // Hangup-on-emergency: HP below HangIfBelowHp triggers a hard
        // disconnect. Single-shot — the disconnect command goes once
        // per session and the engine_log captures it for postmortem.
        // Defaults: HangIfBelowHp=5 (%). Setting it to 0 disables the
        // check entirely (no false positives on dead/respawned chars).
        if (!_hangFired && s.HangIfBelowHp > 0 && _state.MaxHp > 0)
        {
            int hangTrigger = ResolveThreshold(s.HpThresholdMode, s.HangIfBelowHp, _state.MaxHp);
            if (_state.Hp > 0 && _state.Hp <= hangTrigger)
            {
                _hangFired = true;
                string? hangCmd = _readHangupCommand?.Invoke();
                if (string.IsNullOrWhiteSpace(hangCmd))
                {
                    _log?.Warn(LogCategory,
                        $"HANGUP threshold crossed (HP {_state.Hp}/{_state.MaxHp} <= {hangTrigger}) " +
                        $"but no hangup command configured — set Settings → Other → Game Exit.");
                }
                else
                {
                    _log?.Warn(LogCategory,
                        $"HANGUP — HP {_state.Hp}/{_state.MaxHp} <= hang-trigger={hangTrigger} cmd='{hangCmd}'");
                    SendCommand(hangCmd);
                }
            }
        }
    }

    /// <summary>
    /// Try to dispatch a single flee step. No-ops (with a log line)
    /// when no movement engine is active, when the configured
    /// direction can't be resolved, or when the flee selectors
    /// weren't wired by the consumer.
    /// </summary>
    private void TryFlee(string reason)
    {
        Map.IRecoverableEngine? engine = _getActiveMovementEngine?.Invoke();
        if (engine is null)
        {
            _log?.Info(LogCategory,
                $"flee skipped (no active movement engine) — {reason}");
            return;
        }

        Models.Profile.OtherSettings other = _readOtherSettings?.Invoke()
            ?? new Models.Profile.OtherSettings();

        Map.Direction? step = other.RunDirection switch
        {
            Models.Profile.RunDirection.Forward  => engine.PeekNextPlannedDirection(),
            Models.Profile.RunDirection.Backward => Reverse(_getLastSentDirection?.Invoke()),
            _ => null,
        };
        if (step is not { } direction)
        {
            _log?.Warn(LogCategory,
                $"flee skipped (couldn't resolve {other.RunDirection} direction) — {reason}");
            return;
        }

        // Pause the engine first so it doesn't queue planned steps
        // on top of our flee moves. Engine resumes via
        // ResumeAfterRecovery when HP climbs back above the
        // run-trigger (handled in Evaluate's recovery branch).
        engine.PauseForRecovery($"flee — {reason}");

        int distance = _readCombatSettings?.Invoke().RunDistance ?? 1;
        if (distance < 1) distance = 1;

        // Capture sustained flee state — same direction across all
        // remaining steps. The walker's corridor + room layout will
        // tell us if a turn is required (path-aware multi-direction
        // flee is a follow-up).
        _fleeEngine = engine;
        _fleeDirection = direction;
        _fleeStepsRemaining = distance;

        if (other.BreakBeforeFleeing)
            SendCommand("break");

        _log?.Info(LogCategory,
            $"flee start engine={engine.Name} mode={other.RunDirection} dir={direction} " +
            $"distance={distance} ({reason})");
        engine.SendBacktrackMove(direction);
        _fleeStepsRemaining--;
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

    /// <summary>
    /// Called by an external observer (RoomTracker via AppServices)
    /// when the player's location changes. Server-side resting state
    /// is auto-cleared on move, so our <see cref="_restInFlight"/>
    /// latch must drop too — otherwise the next recovery cycle would
    /// skip the <c>rest</c> emit because we'd still think we were
    /// sitting.
    /// </summary>
    public void NoteRoomChanged() => NoteRoomChanged(newRoom: null);

    /// <summary>
    /// Overload that captures the new room key so the flee path can
    /// (a) step its multi-move queue on every arrival and (b) call
    /// <see cref="Map.IRecoverableEngine.ResumeAfterRecovery"/> with
    /// the correct anchor once HP recovers.
    /// </summary>
    public void NoteRoomChanged(Map.RoomKey? newRoom)
    {
        if (newRoom is { } r) _lastKnownRoom = r;

        // Flee step continuation — fire BEFORE the rest-latch reset
        // so the engine's pause flag doesn't get cleared by a
        // racing post-flee rest cycle.
        if (_fleeEngine is not null && _fleeStepsRemaining > 0)
        {
            _fleeEngine.SendBacktrackMove(_fleeDirection);
            _fleeStepsRemaining--;
            _log?.Info(LogCategory,
                $"flee step engine={_fleeEngine.Name} dir={_fleeDirection} " +
                $"remaining={_fleeStepsRemaining}");
        }

        if (!_restInFlight) return;
        _restInFlight = false;
        _restConfirmedByPrompt = false;
        _log?.Info(LogCategory, "rest-in-flight cleared on room change");
    }

    /// <summary>
    /// Percentage mode: treat <paramref name="value"/> as 0..100 of
    /// <paramref name="max"/>. Absolute mode: pass through as-is.
    /// Defensive against <paramref name="max"/> being zero or negative
    /// (returns 0 — no false-positive gate fire when prompt data isn't
    /// loaded yet).
    /// </summary>
    private static int ResolveThreshold(ThresholdMode mode, int value, int max)
    {
        if (mode == ThresholdMode.Percentage)
        {
            if (max <= 0) return 0;
            return (int)Math.Round(max * (value / 100.0));
        }
        return value;
    }

    /// <summary>
    /// Send pre-/post-rest chain — split on <c>;</c> or <c>^M</c> /
    /// newline (the documented HealthSettings convention), trim each
    /// fragment, send each as its own wire line. Empty / whitespace-
    /// only input is a no-op so leaving the field blank just skips the
    /// pre/post phase.
    /// </summary>
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.PropertyChanged -= OnStateChanged;
    }
}
