using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Active fulfiller for <see cref="NeedKind.PathItem"/> needs backed by a
/// shop: when a one-shot <see cref="AutoWalkManager.WalkTo(RoomKey)">walk</see>
/// crosses an <c>(Item: N)</c> / <c>(Ticket: N)</c> gate whose item we're not
/// carrying, and a shop in the active set stocks that item, detour to the
/// shop that adds the fewest steps, <c>buy</c> the item, then resume to the
/// original destination. Backs the Settings → Other "buy item if needed"
/// affordance.
/// </summary>
/// <remarks>
/// <para>
/// <b>Trigger.</b> <see cref="PathItemDemandTracker"/> posts a PathItem need
/// at walk-start; <see cref="OnNeedPosted"/> (wired to
/// <see cref="NeedsRegistry.NeedPosted"/>) reacts. The event fires only for a
/// genuinely new need, so its argument is always an item the current walk
/// just demanded — never a stale leftover. Only one detour runs at a time:
/// re-entrant posts (a multi-gate route announces several) are ignored while
/// a detour is in flight, so v1 services one item per walk and leaves the
/// rest to demand-driven search.
/// </para>
/// <para>
/// <b>Shop selection.</b> Among the rooms hosting a shop that stocks the
/// item, pick the one minimising <c>dist(cur, shop) + dist(shop, dest)</c> —
/// the smallest number of steps added to the trip. Distances use the same
/// <see cref="IRoomFilter"/> the walker routes with, so the estimate matches
/// the walk that actually runs. Ties break on the nearer shop, then room key
/// order, for determinism.
/// </para>
/// <para>
/// <b>Scope.</b> Detours apply only to a plain walk-to. When a loop or
/// auto-lair run is driving movement (<c>engineWalkActive</c>) the need is
/// left to demand-driven search — those routes rarely cross a possession
/// gate, and hijacking a farm loop to shop would be surprising.
/// </para>
/// <para>
/// <b>Resolution paths.</b> The item entering inventory
/// (<see cref="OnInventoryChanged"/>) resumes the original walk — whether it
/// arrived from the <c>buy</c>, from demand-search revealing it en route
/// (found-first abort), or from a party hand-off. A <c>buy</c> that produces
/// no item within <see cref="OnBuyTimeout">the buy window</see> (no gold,
/// out of stock, refused) and a shop we can't reach both fail gracefully:
/// log, resume to the destination, and leave the need outstanding for search
/// to keep hunting. The need's own lifecycle (post / resolve) stays owned by
/// <see cref="PathItemDemandTracker"/>; this router only reacts.
/// </para>
/// <para>
/// Inventory / graph / walker are reached through delegates so the FSM stays
/// unit-testable without a live line stream, room graph, and dispatcher.
/// Each delegate has exactly one production binding in <c>AppServices</c>.
/// </para>
/// </remarks>
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
    private readonly Func<int, bool> _isCarried;
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
    private RoomKey _origDest;
    private RoomKey _shopRoom;

    public PathItemShopRouter(
        Func<int, IReadOnlyList<RoomKey>> shopRoomsSellingItem,
        Func<RoomKey?> currentRoom,
        Func<RoomKey?> walkDestination,
        Func<RoomKey, RoomKey, int?> distanceBetween,
        Func<int, bool> isCarried,
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
        ArgumentNullException.ThrowIfNull(isCarried);
        ArgumentNullException.ThrowIfNull(itemName);
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(engineWalkActive);
        ArgumentNullException.ThrowIfNull(walkTo);
        ArgumentNullException.ThrowIfNull(post);
        _shopRoomsSellingItem = shopRoomsSellingItem;
        _currentRoom = currentRoom;
        _walkDestination = walkDestination;
        _distanceBetween = distanceBetween;
        _isCarried = isCarried;
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

    /// <summary>Bind the wire sink used to issue the <c>buy</c> command.</summary>
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    /// <summary>Every buffer this router pushed to the wire, in order (test seam).</summary>
    internal IReadOnlyList<byte[]> LastSentForTests => _wire.LastSentForTests;

    /// <summary>True while a shop detour is in progress (walking to shop or buying).</summary>
    public bool DetourActive => _phase != Phase.Idle;

    /// <summary>
    /// New-need callback (wired to <see cref="NeedsRegistry.NeedPosted"/>).
    /// Decides whether the item warrants a shop detour and, if so, arms one
    /// toward the fewest-added-steps shop. A no-op when the feature is off,
    /// an engine walk is driving, a detour is already running, no shop
    /// stocks the item, or we can't compute a route.
    /// </summary>
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
        if (_isCarried(itemId)) return;

        string? name = _itemName(itemId);
        if (string.IsNullOrWhiteSpace(name)) return;

        if (_currentRoom() is not { } cur) return;
        if (_walkDestination() is not { } dest) return;

        if (!TrySelectShop(cur, dest, itemId, out RoomKey shopRoom)) return;

        _itemId = itemId;
        _origDest = dest;
        _shopRoom = shopRoom;
        _phase = Phase.WalkingToShop;
        _log?.Info(LogCategory,
            $"path item {itemId} ('{name}') needed — detouring to shop at {shopRoom}");
        _post(() => _walkTo(shopRoom));
    }

    /// <summary>
    /// Walker-event callback (wired to <see cref="AutoWalkManager.Event"/>).
    /// Advances the detour: arrival at the shop starts the buy; a failed or
    /// user-redirected walk abandons the detour.
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

    /// <summary>
    /// Inventory-change callback (wired to <c>InventoryManager.Changed</c>).
    /// When the item we detoured for is now carried — bought, revealed by
    /// search en route, or handed over — resume the original walk. The found
    /// case doubles as the found-first abort for an in-flight shop walk.
    /// </summary>
    public void OnInventoryChanged()
    {
        if (_phase is not (Phase.WalkingToShop or Phase.Buying)) return;
        if (!_isCarried(_itemId)) return;
        _log?.Info(LogCategory, $"path item {_itemId} acquired — resuming to {_origDest}");
        ResumeToPath();
    }

    /// <summary>
    /// Buy-window elapsed. If the item still isn't carried the <c>buy</c>
    /// didn't land (no gold, out of stock, class-refused) — resume to the
    /// destination and leave the need outstanding for search. Invoked on the
    /// UI thread via the injected post delegate; tests call it directly.
    /// </summary>
    public void OnBuyTimeout()
    {
        if (_phase != Phase.Buying) return;
        if (_isCarried(_itemId)) return; // race: OnInventoryChanged handled it
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
        _log?.Info(LogCategory, $"at shop {_shopRoom} — buying '{name}'");
        _wire.Send($"buy {name}");
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
