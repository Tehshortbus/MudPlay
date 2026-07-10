using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Edit;

// Shared opener for the interactive "everything attached to this room" popup —
// used by the Rooms tab (row double-click) and the Monsters tab's clickable
// spawn/placed/summoned room chips. The dialog reuses RoomTooltipBuilder for the
// descriptive text (so it never drifts from the Navigation map hover) and layers
// clickable room/exit/monster links plus blacklist toggle buttons on top.
public static class RoomDetailPopup
{
    public static void Show(DialogService dialogs, RoomKey key)
    {
        RoomDetailDialogViewModel vm = new(AppServices.Current, key);
        // Modeless, fire-and-forget: the popup mutates the blacklist store
        // directly and centres the map via AppServices, so nothing awaits a
        // result — the user dismisses it with Close / the title-bar X.
        _ = dialogs.OpenWindowAsync<RoomDetailDialogViewModel, bool>(vm);
    }
}
