using FujinTerm.Game.Map;

namespace FujinTerm.ViewModels.Navigation;

/// <summary>
/// Display-only wrapper around a <see cref="Loop"/> for the
/// Navigation right-rail LOOPS section. Carries the human-friendly
/// labels the list template binds to plus the underlying
/// <see cref="Source"/> for the Run command.
/// </summary>
public sealed class LoopRowViewModel
{
    public Loop Source { get; }
    public string Name => Source.Name;

    /// <summary>
    /// Start room key in <c>map/room</c> wire form (e.g. <c>"10/297"</c>),
    /// or empty when the loop has no waypoints. Shown below the loop's
    /// name in the right-rail row so the user can tell similarly-named
    /// loops apart by their anchor without expanding the row.
    /// </summary>
    public string StartRoomKey
        => Source.Waypoints.Count == 0
            ? string.Empty
            : $"{Source.Waypoints[0].Key.Map}/{Source.Waypoints[0].Key.Room}";

    /// <summary>"4 rooms" — count of waypoints in the loop.</summary>
    public string SubLabel => $"{Source.RoomCount} room{(Source.RoomCount == 1 ? "" : "s")}";

    public LoopRowViewModel(Loop source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
    }
}
