using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;
using MudPlay.ViewModels.Guides;

namespace MudPlay.ViewModels.Navigation;

// Small modeless dialog for editing a single loop waypoint's per-row action
// — a free-form Command to send at the waypoint and a Delay (stored in
// milliseconds, edited as minutes and seconds) before the loop resumes walking,
// the room flags, and a lap wait.
// Spawned from a loop-builder row and from a running-loop rail row.
//
// The dialog doesn't write to the row directly — it returns the committed values
// so the caller can decide how to apply them (Command empty = "no command",
// Delay 0 = "move on once the command is answered").
public sealed partial class WaypointActionEditDialogViewModel : ObservableObject, IDialogViewModel<WaypointActionEditResult?>
{
    public event Action<WaypointActionEditResult?>? CloseRequested;

    // Header label so the dialog can name which waypoint is being edited.
    public string WaypointLabel { get; }

    [ObservableProperty] private string? _command;
    [ObservableProperty] private long _delayMs;

    // Never rest in this room — the loop advances even below the rest-if-below
    // gates. Independent of Command (a dangerous room may have no command).
    [ObservableProperty] private bool _doNotRest;

    // Never engage hostiles in this room — the loop treats it as if auto-combat
    // were off and walks on. A triggered rest still clears it. Independent of
    // Command and DoNotRest.
    [ObservableProperty] private bool _doNotAttack;

    // Rest up here — rest to the HP / mana rest-max in this room before moving on.
    [ObservableProperty] private bool _restHereHp;
    [ObservableProperty] private bool _restHereMana;

    // Hold here until the lap has run this long (LoopWaypoint.LapWaitMs). Independent
    // of Command and Delay: it reads the lap's clock, not the command's.
    [ObservableProperty] private long _lapWaitMs;

    // A fresh start of the loop walks here first and opens the lap here
    // (LoopWaypoint.StartHere).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartRoomNote))]
    private bool _startHere;

    // The loop's start room when it is another room: a loop has one, so the dialog
    // says that ticking the box here moves it.
    private readonly string? _otherStartRoom;

    public string? StartRoomNote => _otherStartRoom is null ? null
        : StartHere ? $"Saving moves the start from {_otherStartRoom} to this room."
        : $"The loop's start room is {_otherStartRoom}. A loop has one: ticking this moves it here.";

    public WaypointActionEditDialogViewModel(string waypointLabel, string? command, long delayMs, bool doNotRest, bool doNotAttack,
        bool restHereHp = false, bool restHereMana = false, long lapWaitMs = 0, bool startHere = false,
        string? otherStartRoom = null)
    {
        _startHere = startHere;
        _otherStartRoom = otherStartRoom;
        _lapWaitMs = lapWaitMs;
        _restHereHp = restHereHp;
        _restHereMana = restHereMana;
        WaypointLabel = waypointLabel ?? string.Empty;
        _command = command;
        _delayMs = delayMs;
        _doNotRest = doNotRest;
        _doNotAttack = doNotAttack;
    }

    [RelayCommand]
    private void Save()
    {
        string? trimmed = string.IsNullOrWhiteSpace(Command) ? null : Command.Trim();
        long delay = Math.Max(0, DelayMs);
        // Convention: clearing the command also clears the delay —
        // delay-without-command would never fire because the loop only
        // pauses around the command step.
        if (trimmed is null) delay = 0;
        GuideActions.Report(GuideActions.WaypointActionSaved);
        CloseRequested?.Invoke(new WaypointActionEditResult(trimmed, delay, DoNotRest, DoNotAttack, RestHereHp, RestHereMana,
            Math.Max(0, LapWaitMs), StartHere));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}

// Committed payload — null Command means "no command attached".
public sealed record WaypointActionEditResult(string? Command, long DelayMs, bool DoNotRest, bool DoNotAttack,
    bool RestHereHp, bool RestHereMana, long LapWaitMs, bool StartHere);
