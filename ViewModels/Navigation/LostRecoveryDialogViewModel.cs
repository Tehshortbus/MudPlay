using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Navigation;

// Modeless info dialog the EngineRecoveryGate pops when tier-3 backtrack
// exhausts without identifying the room uniquely. Single OK button — no
// candidate picker, no automatic recovery. Tells the user to use the map's
// right-click "I am here" affordance to locate manually.
public sealed partial class LostRecoveryDialogViewModel : ObservableObject, IDialogViewModel<bool>
{
    public event Action<bool>? CloseRequested;

    // Headline shown above the body text.
    public string Header => "Lost — couldn't recover";

    // Body text. Carries the engine name + the gate's terminal reason so the
    // user has context for why automation gave up.
    public string Body { get; }

    public LostRecoveryDialogViewModel(string engineName, string detail)
    {
        ArgumentNullException.ThrowIfNull(engineName);
        ArgumentNullException.ThrowIfNull(detail);
        Body =
            $"{engineName} couldn't figure out where you are after backtracking. " +
            $"({detail}.) Use the map and right-click \"I am here\" on the room you're standing in to set your location.";
    }

    [RelayCommand]
    private void Ok() => CloseRequested?.Invoke(true);
}
