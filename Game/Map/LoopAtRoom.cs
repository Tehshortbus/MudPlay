namespace MudPlay.Game.Map;

// The saved loop a room belongs to, for offering it first when the user goes to pick
// one. A loop that starts here beats one merely passing through; then a favourite;
// then the name, so the pick is the same each time.
public static class LoopAtRoom
{
    public static Loop? Pick(IEnumerable<Loop> loops, RoomKey here)
    {
        ArgumentNullException.ThrowIfNull(loops);
        return loops
            .Where(l => l.Waypoints.Any(w => w.Key.Equals(here)))
            .OrderBy(l => StartsAt(l, here) ? 0 : 1)
            .ThenBy(l => l.Favorite ? 0 : 1)
            .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool StartsAt(Loop loop, RoomKey here) =>
        (loop.Waypoints.FirstOrDefault(w => w.StartHere) ?? loop.Waypoints[0]).Key.Equals(here);
}
