using System.Windows.Input;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels.Navigation;

// One row of the Current-route Details view: the route-picker's numbered
// "N> map/room < command" step — with the room name a clickable link that flashes
// and centres it on the map (the @where treatment) — plus that room's notable
// monsters (placed fixtures + lair spawners) as clickable record links. Wrapping
// RouteStepRow rather than extending it keeps the pure step builder UI-free: the
// links, which need a map/record command, are attached here in the VM layer.
public sealed class RouteDetailRow
{
    public RouteStepRow Step { get; }

    // The placed + lair monsters standing in this row's room, each opening its
    // Game Data record on click. Empty for a room with none (the common case).
    public IReadOnlyList<RoomDetailLink> Monsters { get; }

    // Click the room name to flash it on the map and centre there (like @where).
    public ICommand OpenRoom { get; }

    // Set when this row's room is a hazard (lava, a river crossing…) and/or an exit
    // off it is item-gated (rope & grapple, a raft…) — the harmful spell + the
    // item(s) needed to cross. Null for the common unremarkable room.
    public RouteStepWarning? Warning { get; }

    public bool HasMonsters => Monsters.Count > 0;
    public bool HasWarning => Warning is not null;
    public bool IsAcquire => Step.IsAcquire;
    public bool IsArrival => Step.IsArrival;

    // The line split so the room can be its own link: "1>" / "1/224 Town Square" /
    // "< e". An acquire row (never produced by the current-route view) keeps its
    // whole ◆ line in Location so nothing is lost if one ever appears. The final
    // arrival row shows the destination room with a muted "(arrive)" and no command.
    public string NumberLabel => $"{Step.Number}>";
    public string Location => IsAcquire ? Step.Line : Step.Location;
    public string CommandSuffix =>
        IsArrival ? "(arrive)"
        : IsAcquire ? string.Empty
        : $"< {Step.Command}";

    public RouteDetailRow(
        RouteStepRow step, IReadOnlyList<RoomDetailLink> monsters, ICommand openRoom,
        RouteStepWarning? warning = null)
    {
        Step = step;
        Monsters = monsters ?? Array.Empty<RoomDetailLink>();
        OpenRoom = openRoom;
        Warning = warning;
    }
}
