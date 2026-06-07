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
    private readonly LogService? _log;

    private Action<byte[]>? _wireSender;
    private bool _hpGateAsserted;
    private bool _maGateAsserted;
    private bool _restInFlight;          // sent rest, awaiting recovery
    private bool _restConfirmedByPrompt; // observed (Resting) since the last rest emit
    private bool _fledThisCombat;        // sent flee, awaiting combat to end
    private bool _disposed;

    public HealthManager(
        PlayerState state,
        MovementCoordinator coordinator,
        Func<HealthSettings> readSettings,
        Func<bool> isEnabled,
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

        // ----- HP gate transitions ---------------------------------
        int hpRestTrigger = ResolveThreshold(s.HpThresholdMode, s.RestIfBelowHp, _state.MaxHp);
        int hpRestTarget  = ResolveThreshold(s.HpThresholdMode, s.RestMaxHp,    _state.MaxHp);

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
        int maRestTarget  = ResolveThreshold(s.MaThresholdMode, s.RestMaxMa,    _state.MaxMa);

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

        // ----- flee on critical HP/MA mid-combat -------------------
        // Single-shot per combat so a low-HP fight doesn't burn `flee`
        // on every HP-changed event. Server-side `flee` picks a random
        // direction + moves us one room. The user's RunDistance
        // setting (rooms to flee before re-evaluating) is a future
        // walker-driven follow-up — first cut just gets us out of the
        // room.
        if (!_state.InCombat)
        {
            _fledThisCombat = false;
        }
        else if (!_fledThisCombat)
        {
            int hpRunTrigger = ResolveThreshold(s.HpThresholdMode, s.RunIfBelowHp, _state.MaxHp);
            int maRunTrigger = ResolveThreshold(s.MaThresholdMode, s.RunIfBelowMa, _state.MaxMa);
            bool hpRun = _state.MaxHp > 0 && _state.Hp > 0 && _state.Hp <= hpRunTrigger;
            bool maRun = _state.MaxMa > 0 && _state.Ma <= maRunTrigger;
            if (hpRun || maRun)
            {
                string reason = hpRun
                    ? $"HP {_state.Hp}/{_state.MaxHp} <= run-trigger={hpRunTrigger}"
                    : $"MA {_state.Ma}/{_state.MaxMa} <= run-trigger={maRunTrigger}";
                SendCommand("flee");
                _log?.Info(LogCategory, $"flee {reason}");
                _fledThisCombat = true;
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

        if (anyGate && !_state.InCombat && !_restInFlight)
        {
            SendChained(s.PreRestCommand);
            SendCommand("rest");
            _log?.Info(LogCategory,
                $"rest hp={_state.Hp}/{_state.MaxHp} ma={_state.Ma}/{_state.MaxMa}");
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
    }

    /// <summary>
    /// Called by an external observer (RoomTracker via AppServices)
    /// when the player's location changes. Server-side resting state
    /// is auto-cleared on move, so our <see cref="_restInFlight"/>
    /// latch must drop too — otherwise the next recovery cycle would
    /// skip the <c>rest</c> emit because we'd still think we were
    /// sitting.
    /// </summary>
    public void NoteRoomChanged()
    {
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
