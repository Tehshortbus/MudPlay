namespace FujinTerm.Services;

// Live per-character diagnostic switches. Two gate in-memory generation of the
// Debug and Combat log channels; the third gates whether the on-disk
// diagnostic files (program / memory / combat trace) are written at all.
// Surfaced as the toggles in the Log pane.
//
// This is the in-memory source of truth. AppServices mirrors it to the
// Char-tier LogDiagnosticsSettings section: it applies the persisted values
// on ProfileLoaded, resets to off on ProfileClosed, and writes back on
// Changed. All flags default off — verbose tracing burns IO and isn't an
// everyday affordance — and a fresh character (no saved section) reads off.
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

    // Master toggle for the generation-gated Debug channel. Off by default;
    // flip on to make every _log?.Debug(...) site across the engines start
    // emitting, flip off again for normal play.
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

    // Master toggle for the in-memory combat-decision channel. Off by default;
    // flip on while troubleshooting a combat or healing engine, flip off again
    // for normal play.
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

    // Fires after any flag change so observers (the LogPane VM mirroring
    // state across windows; AppServices persisting the change to the active
    // character) can refresh.
    public event Action? Changed;
}
