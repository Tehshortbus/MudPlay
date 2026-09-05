namespace MudPlay.Services;

// Live per-character diagnostic switches. Two gate in-memory generation of the
// Debug and Combat log channels; the third gates whether the on-disk
// diagnostic files (program / memory / combat trace) are written at all; the
// fourth gates the navigation hop-timing calibration trace; the fifth gates
// whether Game.MessageCandidateWatcher captures unrecognized wire lines.
// Surfaced as the toggles in the Log pane.
//
// This is the in-memory source of truth. AppServices mirrors it to the
// Char-tier LogDiagnosticsSettings section: it applies the persisted values
// on ProfileLoaded, resets to the LogDiagnosticsSettings defaults on
// ProfileClosed, and writes back on Changed. The field initializers below are
// all false, but the effective per-character defaults come from
// LogDiagnosticsSettings, which ships Debug + Combat + CaptureUnrecognizedMessages
// ON (so a fresh character's Program Log already carries the decision-trail a
// bug report needs, and silent message-recognition gaps get noticed) and
// AutoCollect + HopTiming off (the heavier on-disk / trace affordances).
//
// DebugDiagnostics gates the cross-engine Debug traces; every
// _log?.Debug(...) site emits only while it's on. CombatDiagnostics gates
// the combat-decision channel. AutoCollectLogs gates whether the on-disk
// diagnostic writers run at all: ProgramLogFile (Data/Logs/*-program.log),
// MemoryUsageLog (*-memory.log) and RoundDamageTracker's per-round trace
// (*-combat.log) only open their files while it's on.
//
// Lives under AppServices.LogDiagnostics and is wired into
// LogService.Diagnostics so the service can gate emission at the source
// without coupling to the Log pane's UI layer.
public sealed class LogDiagnosticState
{
    private bool _debugDiagnostics;
    private bool _combatDiagnostics;
    private bool _autoCollectLogs;
    private bool _hopTiming;
    private bool _captureUnrecognizedMessages;

    // Master toggle for the generation-gated Debug channel. Effectively on by
    // default (applied from LogDiagnosticsSettings on profile load); while on,
    // every _log?.Debug(...) site across the engines emits — flip it off to
    // quiet them.
    public bool DebugDiagnostics
    {
        get => _debugDiagnostics;
        set
        {
            if (_debugDiagnostics == value) return;
            _debugDiagnostics = value;
            Changed?.Invoke();
        }
    }

    // Master toggle for the in-memory combat-decision channel. Effectively on
    // by default (applied from LogDiagnosticsSettings on profile load); leave it
    // on for the combat-decision trace, flip off to quiet it.
    public bool CombatDiagnostics
    {
        get => _combatDiagnostics;
        set
        {
            if (_combatDiagnostics == value) return;
            _combatDiagnostics = value;
            Changed?.Invoke();
        }
    }

    // Master toggle for the on-disk diagnostic files. Off by default: without
    // it the program, memory and combat-trace writers never open a file, so a
    // normal session leaves nothing under Data/Logs. Flip on to have the
    // client generate all three for the session (and the reverse closes them);
    // the writers subscribe to Changed and open/close their files to match.
    public bool AutoCollectLogs
    {
        get => _autoCollectLogs;
        set
        {
            if (_autoCollectLogs == value) return;
            _autoCollectLogs = value;
            Changed?.Invoke();
        }
    }

    // Master toggle for the navigation hop-timing calibration trace. Off by
    // default; flip on to have HopTimingCalibrator emit one Info line per
    // confirmed room hop (elapsed time + encumbrance) while tuning movement
    // delays, flip off again for normal play.
    public bool HopTiming
    {
        get => _hopTiming;
        set
        {
            if (_hopTiming == value) return;
            _hopTiming = value;
            Changed?.Invoke();
        }
    }

    // Master toggle for Game.MessageCandidateWatcher. Effectively on by
    // default (applied from LogDiagnosticsSettings on profile load); while on,
    // an unrecognized wire line stages a candidate in MessageCandidates and
    // logs a Warn row — flip off to stop capturing (existing candidates stay).
    public bool CaptureUnrecognizedMessages
    {
        get => _captureUnrecognizedMessages;
        set
        {
            if (_captureUnrecognizedMessages == value) return;
            _captureUnrecognizedMessages = value;
            Changed?.Invoke();
        }
    }

    // Reveals the Death Recovery tab's "Simulate Death" button — a test-only
    // affordance. Off by default and NOT persisted (session-only: it resets to
    // off every launch), so a normal user never sees the button; a tester flips
    // it on from the Log pane when they want to exercise the recovery flow. The
    // button binds its visibility here; hidden while off.
    private bool _showSimulateDeath;
    public bool ShowSimulateDeath
    {
        get => _showSimulateDeath;
        set
        {
            if (_showSimulateDeath == value) return;
            _showSimulateDeath = value;
            Changed?.Invoke();
        }
    }

    // Reveals the Chest Offload window's "Simulate Chest" button — a test-only
    // affordance that seeds random containers so the window can be exercised
    // without real boss chests. Same contract as ShowSimulateDeath: off by
    // default, session-only (resets off every launch), never persisted.
    private bool _showSimulateChest;
    public bool ShowSimulateChest
    {
        get => _showSimulateChest;
        set
        {
            if (_showSimulateChest == value) return;
            _showSimulateChest = value;
            Changed?.Invoke();
        }
    }

    // Reveals the Game Data Browser Unrecognized Lines tab's "Simulate entry"
    // button — feeds a synthetic never-seen line through MessageCandidateWatcher
    // so the capture flow can be exercised without waiting for the game to emit
    // an unknown message. Same contract as the two above: off by default,
    // session-only (resets off every launch), never persisted.
    private bool _showSimulateUnrecognized;
    public bool ShowSimulateUnrecognized
    {
        get => _showSimulateUnrecognized;
        set
        {
            if (_showSimulateUnrecognized == value) return;
            _showSimulateUnrecognized = value;
            Changed?.Invoke();
        }
    }

    // Fires after any flag change so observers (the LogPane VM mirroring
    // state across windows; AppServices persisting the change to the active
    // character) can refresh.
    public event Action? Changed;
}
