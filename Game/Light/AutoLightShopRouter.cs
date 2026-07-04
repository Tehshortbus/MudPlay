using System.Threading;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.Game.Light;

/// <summary>
/// Active provisioning detour for the auto-light engine — the shop-buying
/// counterpart to <see cref="AutoLightProvisioner"/>'s ready-a-carried-light
/// path. When the planner decides the route ahead runs dark and nothing in the
/// pack covers it (<see cref="AutoLightAction.Buy"/>), the provisioner hands off
/// an <see cref="AutoLightBuyRequest"/> here: detour to the shop that adds the
/// fewest steps, <c>buy</c> the carry-duration batch, then resume to the
/// original destination. The bought light is <em>lit</em> not here but by the
/// provisioner's ready path when the resumed walk re-announces the still-dark
/// route against a snapshot that now shows the light carried — so the
/// <c>use</c> logic lives in exactly one place.
/// </summary>
/// <remarks>
/// <para>
/// <b>Gating.</b> Every provisioning action sits behind the single AutoLight
/// master toggle — there is deliberately no separate "buy lights" opt-in (a
/// player who doesn't want the client buying light simply leaves AutoLight
/// off). The provisioner already gates the hand-off on that toggle; the
/// router's own <c>isEnabled</c> guards its async walk / inventory callbacks.
/// </para>
/// <para>
/// <b>Shop selection.</b> Mirrors <see cref="PathItemShopRouter"/>: among the
/// rooms hosting a shop that stocks the light, pick the one minimising
/// <c>dist(cur, shop) + dist(shop, dest)</c> — the fewest steps added to the
/// trip — using the same movement filter the walker routes with. Ties break on
/// the nearer shop then room-key order for determinism.
/// </para>
/// <para>
/// <b>Scope.</b> Detours apply only to a plain walk-to. While a loop or
/// auto-lair run is driving movement (<c>engineWalkActive</c>) provisioning is
/// suppressed — hijacking a farm loop to shop would be surprising, and the
/// intended flow provisions on the approach to a dark area, not mid-run. The
/// reactive <see cref="AutoLightManager"/> LightSource need still surfaces
/// meanwhile.
/// </para>
/// <para>
/// <b>Resolution &amp; failure.</b> The first freshly-bought copy landing in
/// inventory (<see cref="OnInventoryChanged"/>) resumes the original walk — the
/// dark route need is met by one copy even while the rest of the batch is still
/// buying. A shop we can't reach, or buys that don't land within
/// <see cref="OnBuyTimeout">the buy window</see> (no gold, out of stock),
/// resume to the destination and <em>suppress a re-detour to that same
/// destination</em>: unlike a deduped <see cref="Need"/>, the provisioner's Buy
/// verdict re-fires on the resumed walk's fresh route announcement, so without
/// the suppression a failed buy would loop forever. The suppression lifts as
/// soon as the destination changes or the light is acquired.
/// </para>
/// </remarks>
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

    /// <summary>Bind the wire sink used to issue the <c>buy</c> command.</summary>
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    /// <summary>Every buffer this router pushed to the wire, in order (test seam).</summary>
    internal IReadOnlyList<byte[]> LastSentForTests => _wire.LastSentForTests;

    /// <summary>True while a provisioning detour is in progress.</summary>
    public bool DetourActive => _phase != Phase.Idle;

    /// <summary>
    /// Provisioning hand-off (bound to <see cref="AutoLightProvisioner"/>'s Buy
    /// verdict). Arms a detour toward the fewest-added-steps shop that stocks
    /// the light. A no-op when the feature is off, an engine walk is driving, a
    /// detour is already running, this destination's last buy already failed,
    /// no reachable shop stocks it, or we can't compute a route.
    /// </summary>
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
        _post(() => _walkTo(shopRoom));
    }

    /// <summary>
    /// Walker-event callback (wired to <see cref="AutoWalkManager.Event"/>).
    /// Arrival at the shop starts the buy; a failed or user-redirected walk
    /// abandons the detour.
    /// </summary>
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
                    Reset(); // user / another engine took over — abandon quietly
                break;

            case Phase.Buying:
                // Walker idles at the shop while buying. A fresh walk means the
                // user redirected — drop the buy and let them drive.
                if (e.Kind is WalkEventKind.Started or WalkEventKind.Stopped)
                    Reset();
                break;
        }
    }

    /// <summary>
    /// Inventory-change callback (wired to <c>InventoryManager.Changed</c>).
    /// The first freshly-bought copy landing resumes the original walk — one
    /// copy covers the dark route even while the rest of the batch buys. Also
    /// doubles as a found-first abort if the light turns up mid-walk-to-shop.
    /// </summary>
    public void OnInventoryChanged()
    {
        if (_phase is not (Phase.WalkingToShop or Phase.Buying)) return;
        if (_carriedCount(_itemId) <= _baseCount) return;   // no new copy yet
        _log?.Info(LogCategory, $"'{_lightName}' acquired — resuming to {_origDest}");
        _suppressedDest = null;
        ResumeToPath();
    }

    /// <summary>
    /// Buy-window elapsed. If no fresh copy landed the <c>buy</c> didn't take
    /// (no gold, out of stock) — resume to the destination and suppress a
    /// re-detour to it, leaving the reactive LightSource need for search.
    /// Invoked on the UI thread via the injected post delegate; tests call it
    /// directly.
    /// </summary>
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
        _post(() => _walkTo(dest));
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
