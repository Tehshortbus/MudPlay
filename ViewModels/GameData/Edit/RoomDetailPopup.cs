using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Edit;

// Shared opener for the read-only "everything attached to this room" popup —
// used by the Rooms tab (row double-click) and the Monsters tab's clickable
// spawn/placed/summoned room chips. Reuses RoomTooltipBuilder (the same text the
// Navigation map hover renders) so the popup and the map never drift, then
// appends the placed-NPC monster the tooltip deliberately omits (a placed
// boss/shopkeeper lives on the room's Npc field, not its lair tag).
public static class RoomDetailPopup
{
    public static void Show(DialogService dialogs, RoomKey key)
    {
        AppServices svc = AppServices.Current;
        Room? room = svc.RoomGraph.GetRoom(key);
        if (room is null)
        {
            dialogs.ShowInfo($"Room {key}", $"No room record for {key} in the active game-data set.");
            return;
        }

        string body = RoomTooltipBuilder.Build(
            room, svc.RoomGraph, svc.GameData, svc.TBInfo,
            svc.MonsterSpawns, svc.SpellCatalog, svc.PlayerIllumination.Current);

        if (room.Npc > 0)
        {
            string? placed = svc.GameData.FindNameByNumber("Monsters", room.Npc);
            body += "\n\nPlaced here: " + (placed is { Length: > 0 } ? placed : $"#{room.Npc}");
        }

        dialogs.ShowInfo($"{room.DisplayName} ({key})", body);
    }
}
