using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Edit;

// One clickable "map/room" chip in a monster record's spawn/placed/summoned room
// lists. Clicking opens the shared room-detail popup for that room, so the
// monster pane doubles as a jump-off to the geography it references.
public sealed class RoomLink
{
    public string Label { get; }
    public ICommand Open { get; }

    public RoomLink(string label, RoomKey key, DialogService dialogs)
    {
        Label = label;
        Open = new RelayCommand(() => RoomDetailPopup.Show(dialogs, key));
    }
}
