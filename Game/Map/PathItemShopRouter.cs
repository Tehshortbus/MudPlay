using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Active fulfiller for NeedKind.PathItem needs backed by a shop: when a
// one-shot walk crosses an (Item: N) / (Ticket: N) gate whose item we're not
// carrying, and a shop in the active set stocks that item, detour to the shop
// that adds the fewest steps, buy the needed count (the whole party shortfall
// the need carries — one buy per still-missing copy), then resume to the
// original destination. Backs the Settings → Other "buy item if needed"
// affordance.
//
// Trigger. PathItemDemandTracker posts a PathItem need at walk-start;
// OnNeedPosted (wired to NeedsRegistry.NeedPosted) reacts. The event fires only
// for a genuinely new need, so its argument is always an item the current walk
// just demanded — never a stale leftover. Only one detour runs at a time:
// re-entrant posts (a multi-gate route announces several) are ignored while a
// detour is in flight, so we service one item per walk and leave the rest to
// demand-driven search.
//
// Shop selection. Among the rooms hosting a shop that stocks the item, pick the
// one minimising dist(cur, shop) + dist(shop, dest) — the smallest number of
// steps added to the trip. Distances use the same IRoomFilter the walker routes
// with, so the estimate matches the walk that actually runs. Ties break on the
// nearer shop, then room key order, for determinism.
//
// Scope. Detours apply only to a plain walk-to. When a loop or auto-lair run is
// driving movement (engineWalkActive) the need is left to demand-driven
// search — those routes rarely cross a possession gate, and hijacking a farm
// loop to shop would be surprising.
//
// Resolution paths. Reaching the needed count in inventory (OnInventoryChanged)
// resumes the original walk — whether the copies came from the buy run, from
// demand-search revealing them en route (found-first abort), or from a party
// hand-off. Buys that don't all land within the buy window (no gold, out of
// stock, refused) and a shop we can't reach both fail gracefully: log, resume
// to the destination, and leave the need outstanding so search can turn up the
// rest. The need's own lifecycle (post / resolve) stays owned by
// PathItemDemandTracker; this router only reacts.
//
// Inventory / graph / walker are reached through delegates so the FSM stays
// unit-testable without a live line stream, room graph, and dispatcher. Each
// delegate has exactly one production binding in AppServices.
public sealed class PathItemShopRouter : IDisposable
{
    private const string LogCategory = "AutoSearch";

    private enum Phase
    {
        Idle,
        WalkingToShop,
        Buying,
    }

    private readonly Func<int, IReadOnlyList<RoomKey>> _shopRoomsSellingItem;
    private readonly Func<RoomKey?> _currentRoom;
    private readonly Func<RoomKey?> _walkDestination;
    private readonly Func<RoomKey, RoomKey, int?> _distanceBetween;
    private readonly Func<int, int> _carriedCount;
    private readonly Func<int, string?> _itemName;
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _engineWalkActive;
    private readonly Action<RoomKey> _walkTo;
    private readonly Action<Action> _post;
    private readonly LogService? _log;
    private readonly TimeSpan _buyTimeout;
    private readonly WireSender _wire = new();
    private readonly Timer _buyTimer;

    private Phase _phase = Phase.Idle;
    private int _itemId;
    private int _targetCount = 1;
    private RoomKey _origDest;
    private RoomKey _shopRoom;

    public PathItemShopRouter(
        Func<int, IReadOnlyList<RoomKey>> shopRoomsSellingItem,
        Func<RoomKey?> currentRoom,
        Func<RoomKey?> walkDestination,
        Func<RoomKey, RoomKey, int?> distanceBetween,
        Func<int, int> carriedCount,
        Func<int, string?> itemName,
        Func<bool> isEnabled,
        Func<bool> engineWalkActive,
        Action<RoomKey> walkTo,
        Action<Action> post,
        LogService? log = null,
        TimeSpan? buyTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(shopRoomsSellingItem);
        ArgumentNullException.ThrowIfNull(currentRoom);
        ArgumentNullException.ThrowIfNull(walkDestination);
        ArgumentNullException.ThrowIfNull(distanceBetween);
        ArgumentNullException.ThrowIfNull(carriedCount);
        ArgumentNullException.ThrowIfNull(itemName);
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(engineWalkActive);
        ArgumentNullException.ThrowIfNull(walkTo);
        ArgumentNullException.ThrowIfNull(post);
        _shopRoomsSellingItem = shopRoomsSellingItem;
        _currentRoom = currentRoom;
        _walkDestination = walkDestination;
        _distanceBetween = distanceBetween;
        _carriedCount = carriedCount;
        _itemName = itemName;
        _isEnabled = isEnabled;
        _engineWalkActive = engineWalkActive;
        _walkTo = walkTo;
        _post = post;
        _log = log;
        _buyTimeout = buyTimeout ?? TimeSpan.FromSeconds(8);
        _buyTimer = new Timer(_ => _post(OnBuyTimeout), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    // Bind the wire sink used to issue the buy command.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Every buffer this router pushed to the wire, in order (test seam).
    internal IReadOnlyList<byte[]> LastSentForTests => _wire.LastSentForTests;

    // True while a shop detour is in progress (walking to shop or buying).
    public bool DetourActive => _phase != Phase.Idle;

    // New-need callback (wired to NeedsRegistry.NeedPosted). Decides whether the
    // item warrants a shop detour and, if so, arms one toward the
    // fewest-added-steps shop. A no-op when the feature is off, an engine walk
    // is driving, a detour is already running, no shop stocks the item, or we
    // can't compute a route.
    public void OnNeedPosted(Need need)
    {
        if (need.Kind != NeedKind.PathItem) return;
        if (_phase != Phase.Idle) return;
        if (!_isEnabled()) return;
        if (_engineWalkActive()) return;

        if (!int.TryParse(need.Descriptor, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int itemId)
            || itemId <= 0)
            return;
        int target = Math.Max(1, need.Quantity);
        if (_carriedCount(itemId) >= target) return;   // already hold the whole shortfall

        string? name = _itemName(itemId);
        if (string.IsNullOrWhiteSpace(name)) return;

        if (_currentRoom() is not { } cur) return;
        if (_walkDestination() is not { } dest) return;

        if (!TrySelectShop(cur, dest, itemId, out RoomKey shopRoom)) return;

        _itemId = itemId;
        _targetCount = target;
        _origDest = dest;
        _shopRoom = shopRoom;
        _phase = Phase.WalkingToShop;
        _log?.Info(LogCategory,
            $"path item {itemId} ('{name}') needed — detouring to shop at {shopRoom}");
        _post(() => _walkTo(shopRoom));
    }

    // Walker-event callback (wired to AutoWalkManager.Event). Advances the
    // detour: arrival at the shop starts the buy; a failed or user-redirected
    // walk abandons the detour.
    public void OnWalkEvent(WalkEvent e)
    {
        switch (_phase)
        {
            case Phase.WalkingToShop:
                if (e.Kind == WalkEventKind.Finished && KeyMatches(e.Destination, _shopRoom))
                    BeginBuying();
                else if (e.Kind == WalkEventKind.Failed)
                {
                    _log?.Info(LogCategory,
                        $"shop at {_shopRoom} unreachable ({e.Detail}) — resuming to {_origDest}");
                    ResumeToPath();
                }
                else if (e.Kind == WalkEventKind.Stopped)
                    Reset(); // user / another engine took over — abandon quietly
                break;

            case Phase.Buying:
                // Walker is idle at the shop while buying. A fresh walk means
                // the user redirected — drop the buy and let them drive.
                if (e.Kind is WalkEventKind.Started or WalkEventKind.Stopped)
                    Reset();
                break;
        }
    }

    // Inventory-change callback (wired to InventoryManager.Changed). When the
    // item we detoured for is now carried — bought, revealed by search en route,
    // or handed over — resume the original walk. The found case doubles as the
    // found-first abort for an in-flight shop walk.
    public void OnInventoryChanged()
    {
        if (_phase is not (Phase.WalkingToShop or Phase.Buying)) return;
        if (_carriedCount(_itemId) < _targetCount) return;   // still short of the party's need
        _log?.Info(LogCategory, $"path item {_itemId} acquired (x{_targetCount}) — resuming to {_origDest}");
        ResumeToPath();
    }

    // Buy-window elapsed. If the item still isn't carried the buy didn't land
    // (no gold, out of stock, class-refused) — resume to the destination and
    // leave the need outstanding for search. Invoked on the UI thread via the
    // injected post delegate; tests call it directly.
    public void OnBuyTimeout()
    {
        if (_phase != Phase.Buying) return;
        if (_carriedCount(_itemId) >= _targetCount) return; // race: OnInventoryChanged handled it
        _log?.Info(LogCategory,
            $"buy of path item {_itemId} did not complete in time — resuming to {_origDest}");
        ResumeToPath();
    }

    public void Dispose() => _buyTimer.Dispose();

    private void BeginBuying()
    {
        _phase = Phase.Buying;
        string? name = _itemName(_itemId);
        if (string.IsNullOrWhiteSpace(name)) { ResumeToPath(); return; }
        // Buy the whole shortfall in one visit — one `buy <name>` per copy the
        // party still lacks, so the leader leaves with enough to hand out. The
        // walk resumes only once the target count lands (or the buy window
        // elapses), so movement never interrupts the run of buys.
        int toBuy = Math.Max(1, _targetCount - _carriedCount(_itemId));
        _log?.Info(LogCategory, $"at shop {_shopRoom} — buying '{name}' x{toBuy}");
        for (int i = 0; i < toBuy; i++) _wire.Send($"buy {name}");
        ArmBuyTimer();
    }

    private void ResumeToPath()
    {
        DisarmBuyTimer();
        RoomKey dest = _origDest;
        _phase = Phase.Idle;
        _post(() => _walkTo(dest));
    }

    private void Reset()
    {
        DisarmBuyTimer();
        _phase = Phase.Idle;
    }

    private void ArmBuyTimer()
        => _buyTimer.Change(_buyTimeout, Timeout.InfiniteTimeSpan);

    private void DisarmBuyTimer()
        => _buyTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    // Pick the shop room minimising dist(cur,shop)+dist(shop,dest) among the
    // rooms that stock the item and are reachable both ways. Ties break on the
    // nearer shop, then room-key order — deterministic for testability.
    private bool TrySelectShop(RoomKey cur, RoomKey dest, int itemId, out RoomKey best)
    {
        best = default;
        int bestTotal = int.MaxValue;
        int bestToShop = int.MaxValue;
        foreach (RoomKey shop in _shopRoomsSellingItem(itemId))
        {
            if (_distanceBetween(cur, shop) is not { } toShop) continue;
            if (_distanceBetween(shop, dest) is not { } toDest) continue;
            int total = toShop + toDest;
            bool better = total < bestTotal
                || (total == bestTotal && toShop < bestToShop)
                || (total == bestTotal && toShop == bestToShop && CompareKeys(shop, best) < 0);
            if (better)
            {
                bestTotal = total;
                bestToShop = toShop;
                best = shop;
            }
        }
        return bestTotal != int.MaxValue;
    }

    private static bool KeyMatches(RoomKey? actual, RoomKey expected)
        => actual.HasValue && actual.Value.Equals(expected);

    private static int CompareKeys(RoomKey a, RoomKey b)
    {
        int byMap = a.Map.CompareTo(b.Map);
        return byMap != 0 ? byMap : a.Room.CompareTo(b.Room);
    }
}
