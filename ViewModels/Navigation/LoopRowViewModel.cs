using FujinTerm.Game.Map;

namespace FujinTerm.ViewModels.Navigation;

// Display-only wrapper around a Loop for the Navigation right-rail LOOPS
// section. Carries the human-friendly labels the list template binds to plus
// the underlying Source for the Run command.
public sealed class LoopRowViewModel
{
    public Loop Source { get; }
    public string Name => Source.Name;

    // Start room key in map/room wire form (e.g. "10/297"), or empty when
    // the loop has no waypoints. Shown below the loop's name in the
    // right-rail row so the user can tell similarly-named loops apart by
    // their anchor without expanding the row.
    public string StartRoomKey
        => Source.Waypoints.Count == 0
            ? string.Empty
            : $"{Source.Waypoints[0].Key.Map}/{Source.Waypoints[0].Key.Room}";

    // "4 rooms" — count of waypoints in the loop.
    public string SubLabel => $"{Source.RoomCount} room{(Source.RoomCount == 1 ? "" : "s")}";

    public LoopRowViewModel(Loop source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
    }
}
