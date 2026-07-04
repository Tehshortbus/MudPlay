using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.ViewModels;

// Yes/No prompt that fires when the room tracker learns a real name for a
// previously-unnamed room (typical of map-15 ganghouse rooms in 1.x MDB
// exports). On Save the name persists back to the active set's Rooms.json;
// on Skip the in-memory rename is kept for the session but nothing is
// written.
public sealed partial class RoomNameLearnedDialogViewModel
    : ObservableObject, IDialogViewModel<bool>
{
    public event Action<bool>? CloseRequested;

    public RoomKey Key { get; }
    public string ObservedName { get; }

    public RoomNameLearnedDialogViewModel(RoomKey key, string observedName)
    {
        Key = key;
        ObservedName = observedName;
    }

    // Header line — "Overwrite room name in game data for {map/room}?"
    public string HeaderText =>
        $"Overwrite room name in game data for {Key}?";

    public string SavedNameLabel    => "Saved Room name:";
    public string ObservedNameLabel => "Observed Room name:";
    public string SavedNameValue    => "null";
    public string ObservedNameValue => ObservedName;

    [RelayCommand] private void Save() => CloseRequested?.Invoke(true);
    [RelayCommand] private void Skip() => CloseRequested?.Invoke(false);
}
