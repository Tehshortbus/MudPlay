namespace MudPlay.Game.Map;

// Hop floors for a trip from one room to another by way of a stop (a shop, a dock):
// From[stop] no FindPath route from the source to the stop can beat, To[stop] none
// from the stop on to the destination (BfsMapper.HopFloorsFrom / HopFloorsTo). Two
// searches answer every stop, so a caller weighing many stops by two FindPath
// answers each searches only from the stops that can still win. A stop missing from
// either has no FindPath route that way at all. Taken under the filter state the
// exact answers are asked under, ignoreAvoids included: floors kept out of avoided
// rooms are no floor for a route that may pass through them.
public sealed record RouteFloors(IReadOnlyDictionary<RoomKey, int> From, IReadOnlyDictionary<RoomKey, int> To)
{
    public static RouteFloors Via(
        BfsMapper bfs, RoomKey source, RoomKey destination, IReadOnlyCollection<RoomKey> stops, IRoomFilter? filter,
        bool ignoreAvoids = false)
    {
        ArgumentNullException.ThrowIfNull(bfs);
        ArgumentNullException.ThrowIfNull(stops);
        return new(bfs.HopFloorsFrom(source, stops, filter, ignoreAvoids),
            bfs.HopFloorsTo(destination, stops, filter, ignoreAvoids));
    }

    // The least the whole trip can come to by way of the stops given, or null when
    // it can't be made that way.
    public int? Bound(RoomKey toStop, RoomKey fromStop) =>
        From.TryGetValue(toStop, out int to) && To.TryGetValue(fromStop, out int from) ? to + from : null;
}
