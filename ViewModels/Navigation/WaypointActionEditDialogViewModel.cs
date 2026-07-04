using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Navigation;

// Small modeless dialog for editing a single loop waypoint's per-row action
// — a free-form Command to send at the waypoint and a Delay in milliseconds
// before the loop resumes walking. Spawned from the Loop editor's per-row ✎
// button.
//
// The dialog doesn't write to LoopWaypointRowViewModel directly — it bubbles
// the committed values up via a LairTimerEditDialogResult-shaped result so
// the Loop editor can decide how to apply them (same shape both fields are
// nullable: Command empty = "no command", Delay 0 = "default per-step
// delay").
public sealed partial class WaypointActionEditDialogViewModel : ObservableObject, IDialogViewModel<WaypointActionEditResult?>
{
    public event Action<WaypointActionEditResult?>? CloseRequested;

    // Header label so the dialog can name which waypoint is being edited.
    public string WaypointLabel { get; }

    [ObservableProperty] private string? _command;
    [ObservableProperty] private int _delayMs;

    public WaypointActionEditDialogViewModel(string waypointLabel, string? command, int delayMs)
    {
        WaypointLabel = waypointLabel ?? string.Empty;
        _command = command;
        _delayMs = delayMs;
    }

    [RelayCommand]
    private void Save()
    {
        string? trimmed = string.IsNullOrWhiteSpace(Command) ? null : Command.Trim();
        int delay = Math.Max(0, DelayMs);
        // Convention: clearing the command also clears the delay —
        // delay-without-command would never fire because the loop only
        // pauses around the command step.
        if (trimmed is null) delay = 0;
        CloseRequested?.Invoke(new WaypointActionEditResult(trimmed, delay));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}

// Committed payload — null Command means "no command attached".
public sealed record WaypointActionEditResult(string? Command, int DelayMs);
