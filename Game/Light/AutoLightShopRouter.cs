using System.Threading;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.Game.Light;

// Active provisioning detour for the auto-light engine — the shop-buying
// counterpart to AutoLightProvisioner's ready-a-carried-light path. When the
// planner decides the route ahead runs dark and nothing in the pack covers it
// (the Buy verdict), the provisioner hands off an AutoLightBuyRequest here: detour
// to the shop that adds the fewest steps, buy the carry-duration batch, then
// resume to the original destination. The bought light is lit not here but by the
// provisioner's ready path when the resumed walk re-announces the still-dark route
// against a snapshot that now shows the light carried — so the use logic lives in
// exactly one place.
//
// Gating: every provisioning action sits behind the single AutoLight master
// toggle — there is deliberately no separate "buy lights" opt-in (a player who
// doesn't want the client buying light simply leaves AutoLight off). The
// provisioner already gates the hand-off on that toggle; the router's own
// isEnabled guards its async walk / inventory callbacks.
//
// Shop selection: mirrors PathItemShopRouter — among the rooms hosting a shop that
// stocks the light, pick the one minimising dist(cur, shop) + dist(shop, dest) —
// the fewest steps added to the trip — using the same movement filter the walker
// routes with. Ties break on the nearer shop then room-key order for determinism.
//
// Scope: detours apply only to a plain walk-to. While a loop or auto-lair run is
// driving movement (engineWalkActive) provisioning is suppressed — hijacking a
// farm loop to shop would be surprising, and the intended flow provisions on the
// approach to a dark area, not mid-run. The reactive AutoLightManager LightSource
// need still surfaces meanwhile.
//
// Resolution & failure: the first freshly-bought copy landing in inventory
// (OnInventoryChanged) resumes the original walk — the dark route need is met by
// one copy even while the rest of the batch is still buying. A shop we can't
// reach, or buys that don't land within the buy window (no gold, out of stock),
// resume to the destination and suppress a re-detour to that same destination:
// unlike a deduped need, the provisioner's Buy verdict re-fires on the resumed
// walk's fresh route announcement, so without the suppression a failed buy would
// loop forever. The suppression lifts as soon as the destination changes or the
// light is acquired.
public sealed class AutoLightShopRouter : IDisposable
{
    private const string LogCategory = AutoLightProvisioner.LogCategory;

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
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _engineWalkActive;
    private readonly Action<RoomKey> _walkTo;
    private readonly Action<Action> _post;
    private readonly LogService? _log;
    private readonly TimeSpan _buyTimeout;
    private readonly WireSender _wire = new();
    private readonly Timer _buyTimer;

    private Phase _phase = Phase.Idle;

    // Set while THIS router is issuing a walk. WalkTo pre-empts any in-progress
    // walk with Stop("superseded by new walk"), which raises a Stopped event —
    // without this flag our own detour-start would deliver that Stopped back into
    // Phase.WalkingToShop and tear the detour down before it moves, re-detouring
    // forever without ever buying. Mirrors the auto-deposit reroute's guard.
    private bool _drivingWalk;
    private int _itemId;
    private string _lightName = string.Empty;
    private int _targetCount = 1;
    private int _baseCount;
    private RoomKey _origDest;
    private RoomKey _shopRoom;

    // Destination whose last provisioning attempt failed (unreachable shop /
    // buy didn't land). Skips a re-detour to it — the resumed walk re-announces
    // the same still-dark route, which would otherwise re-fire the Buy verdict
    // forever. Cleared the moment a different destination is seen.
    private RoomKey? _suppressedDest;

    public AutoLightShopRouter(
        Func<int, IReadOnlyList<RoomKey>> shopRoomsSellingItem,
        Func<RoomKey?> currentRoom,
        Func<RoomKey?> walkDestination,
        Func<RoomKey, RoomKey, int?> distanceBetween,
        Func<int, int> carriedCount,
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
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(engineWalkActive);
        ArgumentNullException.ThrowIfNull(walkTo);
        ArgumentNullException.ThrowIfNull(post);
        _shopRoomsSellingItem = shopRoomsSellingItem;
        _currentRoom = currentRoom;
        _walkDestination = walkDestination;
        _distanceBetween = distanceBetween;
        _carriedCount = carriedCount;
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

    // True while a provisioning detour is in progress.
    public bool DetourActive => _phase != Phase.Idle;

    // Provisioning hand-off (bound to AutoLightProvisioner's Buy verdict). Arms a
    // detour toward the fewest-added-steps shop that stocks the light. A no-op when
    // the feature is off, an engine walk is driving, a detour is already running,
    // this destination's last buy already failed, no reachable shop stocks it, or
    // we can't compute a route.
    public void OnBuyRequested(AutoLightBuyRequest req)
    {
        if (_phase != Phase.Idle) return;
        if (!_isEnabled()) return;
        if (_engineWalkActive()) return;
        if (req.ItemId <= 0 || string.IsNullOrWhiteSpace(req.LightName)) return;

        if (_currentRoom() is not { } cur) return;
        if (_walkDestination() is not { } dest) return;

        // A different destination means the situation changed — lift any
        // suppression left by a previous failed buy, then honour a live one.
        if (_suppressedDest is { } sup && !sup.Equals(dest)) _suppressedDest = null;
        if (_suppressedDest is not null) return;

        if (!TrySelectShop(cur, dest, req.ItemId, out RoomKey shopRoom))
        {
            _log?.Info(LogCategory,
                $"no reachable shop stocks '{req.LightName}' — leaving provisioning to search");
            _suppressedDest = dest;
            return;
        }

        _itemId = req.ItemId;
        _lightName = req.LightName;
        _targetCount = Math.Max(1, req.Count);
        _baseCount = _carriedCount(req.ItemId);
        _origDest = dest;
        _shopRoom = shopRoom;
        _phase = Phase.WalkingToShop;
        _log?.Info(LogCategory,
            $"provisioning '{req.LightName}' x{_targetCount} — detouring to shop at {shopRoom}");
        _post(() => DriveWalk(shopRoom));
    }

    // Walker-event callback (wired to AutoWalkManager.Event). Arrival at the shop
    // starts the buy; a failed or user-redirected walk abandons the detour.
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
                    FailAndResume();
                }
                else if (e.Kind == WalkEventKind.Stopped)
                {
                    if (_drivingWalk) return; // our own supersede of the prior walk, not a takeover
                    Reset();                  // user / another engine took over — abandon quietly
                }
                break;

            case Phase.Buying:
                // Walker idles at the shop while buying. A fresh walk means the
                // user redirected — drop the buy and let them drive.
                if (e.Kind is WalkEventKind.Started or WalkEventKind.Stopped)
                {
                    if (_drivingWalk) return;
                    Reset();
                }
                break;
        }
    }

    // Inventory-change callback (wired to InventoryManager.Changed). The first
    // freshly-bought copy landing resumes the original walk — one copy covers the
    // dark route even while the rest of the batch buys. Also doubles as a
    // found-first abort if the light turns up mid-walk-to-shop.
    public void OnInventoryChanged()
    {
        if (_phase is not (Phase.WalkingToShop or Phase.Buying)) return;
        if (_carriedCount(_itemId) <= _baseCount) return;   // no new copy yet
        _log?.Info(LogCategory, $"'{_lightName}' acquired — resuming to {_origDest}");
        _suppressedDest = null;
        ResumeToPath();
    }

    // Buy-window elapsed. If no fresh copy landed the buy didn't take (no gold, out
    // of stock) — resume to the destination and suppress a re-detour to it, leaving
    // the reactive LightSource need for search. Invoked on the UI thread via the
    // injected post delegate; tests call it directly.
    public void OnBuyTimeout()
    {
        if (_phase != Phase.Buying) return;
        if (_carriedCount(_itemId) > _baseCount) return;   // race: OnInventoryChanged handled it
        _log?.Info(LogCategory,
            $"buy of '{_lightName}' did not complete in time — resuming to {_origDest}");
        FailAndResume();
    }

    public void Dispose() => _buyTimer.Dispose();

    private void BeginBuying()
    {
        _phase = Phase.Buying;
        // Buy the whole carry batch in one visit — one `buy <name>` per copy
        // still short of the target. Movement resumes only once a fresh copy
        // lands (or the buy window elapses), so the run of buys isn't cut short.
        int toBuy = Math.Max(1, _targetCount - _carriedCount(_itemId));
        _log?.Info(LogCategory, $"at shop {_shopRoom} — buying '{_lightName}' x{toBuy}");
        for (int i = 0; i < toBuy; i++) _wire.Send($"buy {_lightName}");
        ArmBuyTimer();
    }

    private void ResumeToPath()
    {
        DisarmBuyTimer();
        RoomKey dest = _origDest;
        _phase = Phase.Idle;
        _post(() => DriveWalk(dest));
    }

    // Issue a router-driven walk with the self-supersede guard raised, so the
    // Stopped that WalkTo emits when it pre-empts the in-progress walk isn't
    // mistaken for a user takeover.
    private void DriveWalk(RoomKey dest)
    {
        _drivingWalk = true;
        try { _walkTo(dest); }
        finally { _drivingWalk = false; }
    }

    // Resume after a failure (unreachable shop / buy didn't land) and suppress a
    // re-detour to this destination so the re-announced dark route can't loop.
    private void FailAndResume()
    {
        _suppressedDest = _origDest;
        ResumeToPath();
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
    // rooms that stock the light and are reachable both ways. Ties break on the
    // nearer shop, then room-key order — deterministic for testability. Public so
    // the auto-deposit reroute can reuse the same fewest-added-steps choice for its
    // own single-controller light-shop leg (bank -> shop -> origin) rather than
    // duplicating the selection; a pure query with no side effects.
    public bool TrySelectShop(RoomKey cur, RoomKey dest, int itemId, out RoomKey best)
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
