using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Map;

namespace MudPlay.ViewModels.Navigation;

// Display-only wrapper around a Loop for the Navigation right-rail LOOPS
// section. Carries the human-friendly labels the list template binds to plus
// the underlying Source for the Run command.
public sealed partial class LoopRowViewModel : ObservableObject
{
    // The loop the character is standing on. Observable because it moves with every
    // step, where the rows themselves are only rebuilt when the loops change.
    [ObservableProperty] private bool _isHere;

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

    // Whole-entry text for the row's mouseover tooltip — name plus the same
    // anchor · count detail line — so a narrow rail that ellipsises either line
    // still reveals the full entry on hover.
    public string RowTooltip =>
        StartRoomKey.Length == 0 ? $"{Name}\n{SubLabel}" : $"{Name}\n{StartRoomKey} · {SubLabel}";

    // Context-menu label reflecting the current favourite state (the loop's
    // Favorite flag feeds the terminal right-click Favourites flyout). The row is
    // rebuilt on every LoopsChanged, so a plain computed getter stays in sync.
    public string FavoriteMenuHeader
        => Source.Favorite ? "Remove from favourites" : "Add to favourites";

    public LoopRowViewModel(Loop source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
    }
}
