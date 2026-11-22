namespace MudPlay.Game.Map;

// The saved loop a room belongs to, for offering it first when the user goes to pick
// one. The loop running now stays the pick wherever its lap crosses another's; then a
// loop that starts here beats one merely passing through; then a favourite; then the
// name, so the pick is the same each time.
public static class LoopAtRoom
{
    public static Loop? Pick(IEnumerable<Loop> loops, RoomKey here, string? running = null)
    {
        ArgumentNullException.ThrowIfNull(loops);
        return loops
            .Where(l => l.Waypoints.Any(w => w.Key.Equals(here)))
            .OrderBy(l => string.Equals(l.Name, running, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(l => StartsAt(l, here) ? 0 : 1)
            .ThenBy(l => l.Favorite ? 0 : 1)
            .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    // Every loop by the rooms it crosses, for a list asked again on each step.
    public static ILookup<RoomKey, Loop> ByRoom(IEnumerable<Loop> loops)
    {
        ArgumentNullException.ThrowIfNull(loops);
        return loops
            .SelectMany(l => l.Waypoints.Select(w => w.Key).Distinct().Select(key => (Key: key, Loop: l)))
            .ToLookup(p => p.Key, p => p.Loop);
    }

    private static bool StartsAt(Loop loop, RoomKey here) =>
        (loop.Waypoints.FirstOrDefault(w => w.StartHere) ?? loop.Waypoints[0]).Key.Equals(here);
}
