using FujinTerm.Services;

namespace FujinTerm.Game;

// Blankets the `train stats` / character-creation form in an EngineSendGate hold
// so NO background automation can type into it. The form is a full-screen,
// cursor-positioned box whose FIRST field is the Family Name (the character's
// last name): any stray automated send — a par HP poll, an @health nag, a spell
// emit, an auto-get, anything on the wrapped engine path — lands in that field
// and the trailing Enter advances the cursor, corrupting the name and scrambling
// the allocation. Only two things may touch the keyboard on this screen:
//
//   1. The user's own manual input — it flows TerminalControl -> LocalInputBuffer
//      -> MainWindowViewModel.SendUserInput directly, never the wrapped engine
//      path, so the hold doesn't touch it.
//   2. The auto-trainer's CP allocation — AutoTrainManager is bound to the raw,
//      un-wrapped SendUserInput (not the gate-wrapped engineSend), so its
//      `train stats` + keystroke replay pierce the hold the same way the
//      emergency low-HP hangup does. It's the one automation permitted here.
//
// Driven by TrainerMenuTracker.MenuOwnsKeyboard — the realm-independent OR of the
// command-armed `train stats` input session and the marker-confirmed full-screen
// menu (which also covers character creation, reached with no outbound command).
// Both entry paths raise the hold; the in-game prompt returning on exit releases
// it. It's a NAMED hold so it composes with the suicide-password and
// mortally-wounded holds rather than clobbering them — any subset can be up
// independently and sends stay gated until all clear.
public sealed class TrainerScreenGate : IDisposable
{
    // Surfaced in the EngineSendGate hold set / diagnostics.
    public const string HoldReason = "TrainerMenu";

    // LogService category — rides the same [TrainerMenu] rows as the tracker
    // whose transitions drive this gate.
    private const string LogCategory = "TrainerMenu";

    private readonly TrainerMenuTracker _tracker;
    private readonly EngineSendGate _gate;
    private readonly LogService? _log;
    private bool _held;
    private bool _disposed;

    // True while the hold is up because the trainer / creation form owns the
    // keyboard. Mirrors the EngineSendGate hold; exposed for diagnostics / tests.
    public bool IsHeld => _held;

    public TrainerScreenGate(TrainerMenuTracker tracker, EngineSendGate gate, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(gate);
        _tracker = tracker;
        _gate = gate;
        _log = log;

        // Both entry/exit pairs feed the same MenuOwnsKeyboard read, so any of
        // them can raise or clear the composite state.
        _tracker.InputMenuEntered += Evaluate;
        _tracker.InputMenuExited  += Evaluate;
        _tracker.MenuEntered      += Evaluate;
        _tracker.MenuExited       += Evaluate;
        // Cover a mid-form wire-up (reconnect / character swap while the screen
        // is already open).
        Evaluate();
    }

    // Recompute the hold from the tracker and flip it once per edge. Public so
    // tests can drive it deterministically without raising tracker events.
    public void Evaluate()
    {
        bool owns = _tracker.MenuOwnsKeyboard;
        if (owns == _held) return;

        if (owns)
        {
            _gate.Hold(HoldReason);
            _log?.Info(LogCategory,
                "train stats / creation form owns the keyboard — background engine sends held "
                + "(only manual input + auto-train CP allocation reach the form).");
        }
        else
        {
            _gate.Release(HoldReason);
            _log?.Info(LogCategory,
                "train stats / creation form closed — background engine sends resume.");
        }

        _held = owns;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tracker.InputMenuEntered -= Evaluate;
        _tracker.InputMenuExited  -= Evaluate;
        _tracker.MenuEntered      -= Evaluate;
        _tracker.MenuExited       -= Evaluate;
        // Don't leave a stuck hold if torn down mid-form.
        if (_held) _gate.Release(HoldReason);
    }
}
