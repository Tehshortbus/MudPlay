using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.ViewModels.Navigation;

// Modeless info dialog the EngineRecoveryGate pops when tier-3 backtrack
// exhausts without identifying the room uniquely. Single OK button — no
// candidate picker, no automatic recovery. Names the last room the engine was
// sure of (so the user has a concrete "you were here" anchor) and tells them to
// use the map's right-click "I am here" affordance to locate manually.
public sealed partial class LostRecoveryDialogViewModel : ObservableObject, IDialogViewModel<bool>
{
    public event Action<bool>? CloseRequested;

    // Headline shown above the body text.
    public string Header => "Lost — couldn't recover";

    // Body text. Carries the engine name, the last known-good room, and the gate's
    // terminal reason so the user has real context for why automation gave up.
    public string Body { get; }

    public LostRecoveryDialogViewModel(string engineName, string detail, RoomKey? lastGoodRoom)
    {
        ArgumentNullException.ThrowIfNull(engineName);
        ArgumentNullException.ThrowIfNull(detail);

        string where = FormatRoom(lastGoodRoom) is { Length: > 0 } label
            ? $" It last knew you were at {label}, then lost the trail."
            : string.Empty;

        Body =
            $"{engineName} couldn't confirm where you are after backtracking to recover.{where} " +
            $"Reason: {detail}. " +
            "Use the map and right-click \"I am here\" on the room you're standing in to set your location.";
    }

    // "(map/room) - Name" for the anchor, or empty when there's no anchor or the
    // graph can't name it. Mirrors NavigationViewModel.FormatRoomRef.
    private static string FormatRoom(RoomKey? key)
    {
        if (key is not { } k) return string.Empty;
        string? name = AppServices.Current?.RoomGraph.GetRoom(k)?.DisplayName;
        return string.IsNullOrWhiteSpace(name)
            ? $"({k.Map}/{k.Room})"
            : $"({k.Map}/{k.Room}) - {name}";
    }

    [RelayCommand]
    private void Ok() => CloseRequested?.Invoke(true);

    // The link drop explained the failure: the client wasn't lost after all.
    public void Dismiss() => CloseRequested?.Invoke(false);
}
