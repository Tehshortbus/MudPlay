namespace MudPlay.Game.Map;

// The route filter of a search that may not go into some rooms at all: everything
// the wrapped filter says, with every exit into those rooms refused. A refused exit,
// unlike an avoided room, stays shut when a search sets avoids or exit gates aside,
// so no pass of the search finds its way in (BfsMapper.FindPath, RoomsNoRouteCrosses).
public sealed class RoomsRefusedFilter : IRoomFilter
{
    private readonly IRoomFilter? _inner;
    private readonly IReadOnlySet<RoomKey> _refused;

    public RoomsRefusedFilter(IRoomFilter? inner, IReadOnlySet<RoomKey> refused)
    {
        ArgumentNullException.ThrowIfNull(refused);
        _inner = inner;
        _refused = refused;
    }

    public bool IsExitRefused(RoomKey from, in RoomExit exit) =>
        _refused.Contains(exit.Target) || (_inner?.IsExitRefused(from, in exit) ?? false);
    public bool IsAvoided(RoomKey key) => _inner?.IsAvoided(key) ?? false;
    public IDisposable? BeginSearch() => _inner?.BeginSearch();
    public bool IsExitBlocked(in RoomExit exit) => _inner?.IsExitBlocked(in exit) ?? false;
    public bool IsExitHeldOpen(RoomKey from, in RoomExit exit) => _inner?.IsExitHeldOpen(from, in exit) ?? false;
    public bool IsPoorOddsDoor(in RoomExit exit) => _inner?.IsPoorOddsDoor(in exit) ?? false;
    public string? DescribeDoorRefusal(in RoomExit exit) => _inner?.DescribeDoorRefusal(in exit);
    public string? DescribePurseFor(in RoomExit exit) => _inner?.DescribePurseFor(in exit);
    public bool IsOwnPurseShort(in RoomExit exit) => _inner?.IsOwnPurseShort(in exit) ?? false;
    public int DoorRuleStamp => _inner?.DoorRuleStamp ?? 0;
    public ExitBlockReason DescribeExitBlock(in RoomExit exit) => _inner?.DescribeExitBlock(in exit) ?? ExitBlockReason.None;
    public bool IsBoatPassable(in BoatPassage passage) => _inner?.IsBoatPassable(in passage) ?? true;
    public ExitBlockReason DescribeBoatBlock(in BoatPassage passage) => _inner?.DescribeBoatBlock(in passage) ?? ExitBlockReason.None;
    public void WarmForBoat() => _inner?.WarmForBoat();
    public void WarmForRoute(BfsMapper bfs, RoomKey source, RoomKey destination) => _inner?.WarmForRoute(bfs, source, destination);
    public IDisposable SuspendAcquirableGates() => _inner?.SuspendAcquirableGates() ?? NoSuspension.Instance;
    public IDisposable SuspendAcquirableGatesButUncounteredHazards() =>
        _inner?.SuspendAcquirableGatesButUncounteredHazards() ?? NoSuspension.Instance;
    public IDisposable SuspendAcquirableGatesExcept(
        IReadOnlyCollection<int> keepClosed, bool keepUncounteredHazards = false,
        IReadOnlyCollection<RoomKey>? openHazardRooms = null) =>
        _inner?.SuspendAcquirableGatesExcept(keepClosed, keepUncounteredHazards, openHazardRooms) ?? NoSuspension.Instance;
    public bool IsClosedToRoutes(RoomKey room) => _inner?.IsClosedToRoutes(room) ?? false;
    public ClosedRoomTerms? CrossingTerms(RoomKey room) => _inner?.CrossingTerms(room);
    public bool MayCrossClosedRooms() => _inner?.MayCrossClosedRooms() ?? false;
    public bool TeleportsOnArrival(RoomKey room) => _inner?.TeleportsOnArrival(room) ?? false;
    public IDisposable SuspendAcquirableGatesButUnprotectableHazards() =>
        _inner?.SuspendAcquirableGatesButUnprotectableHazards() ?? NoSuspension.Instance;
    private sealed class NoSuspension : IDisposable
    {
        public static readonly NoSuspension Instance = new();
        public void Dispose() { }
    }
}
