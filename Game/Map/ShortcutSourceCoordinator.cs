using System;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Drives the route picker's "take the shortcut" pick when the crosser does NOT yet
// hold the shortcut item. A shortcut item (e.g. an amber talisman that opens a
// rooftop passage) has no reliable source — it drops off a monster that may be
// dead — so instead of auto-fetching it (which could strand the walk), this walks
// to the item's source, lets the room settle (auto-combat clears any hostile,
// which drops the item on the ground), grabs the drop, then makes ONE live-filter
// walk to the destination. That final walk is self-adapting: if the item is now in
// hand the shortest route IS the shortcut, so it's taken; if it never turned up
// (monster dead / no drop) the shortest route is the long reliable one, so that's
// taken instead. The player always arrives; the shortcut is taken only when the
// gamble pays off.
//
// Give-up rule (user choice): "not there on arrival → fall back." A dead source
// means no hostile to fight, so the room is settled immediately, the get finds
// nothing, and the live-filter walk falls back to the long route with no waiting.
//
// Delegate-injected (walker / combat / inventory / send / scheduler). Each delegate has one binding in
// AppServices.
public sealed class ShortcutSourceCoordinator
{
    private const string LogCategory = "Navigation";

    // How long to let a `get` of the ground drop land before the resume walk plans
    // its route — long enough for the pickup line + inventory update to arrive.
    private const int PickupSettleMs = 900;

    private enum Phase { Idle, WalkingToSource, Settling, AwaitingPickup }

    private readonly Func<int, RoomKey?> _resolveSource;
    private readonly Func<int, bool> _isCarried;
    private readonly Func<int, string?> _itemName;
    private readonly Func<bool> _hasHostiles;
    private readonly Func<bool> _inCombat;
    private readonly Action<RoomKey> _walkToSource;
    private readonly Action<RoomKey> _liveWalkToDest;
    private readonly Action<string> _sendGet;
    private readonly Action<int, Action> _schedule;
    private readonly LogService? _log;

    private Phase _phase = Phase.Idle;

    // For a bug report: the shortcut-item trip in progress, or null.
    public string? DescribePending() => _phase == Phase.Idle ? null : $"phase {_phase}";
    private int _itemId;
    private RoomKey _dest;
    private RoomKey _source;
    private Action? _beforeLongRoute;

    public ShortcutSourceCoordinator(
        Func<int, RoomKey?> resolveSource,
        Func<int, bool> isCarried,
        Func<int, string?> itemName,
        Func<bool> hasHostiles,
        Func<bool> inCombat,
        Action<RoomKey> walkToSource,
        Action<RoomKey> liveWalkToDest,
        Action<string> sendGet,
        Action<int, Action> schedule,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(resolveSource);
        ArgumentNullException.ThrowIfNull(isCarried);
        ArgumentNullException.ThrowIfNull(itemName);
        ArgumentNullException.ThrowIfNull(hasHostiles);
        ArgumentNullException.ThrowIfNull(inCombat);
        ArgumentNullException.ThrowIfNull(walkToSource);
        ArgumentNullException.ThrowIfNull(liveWalkToDest);
        ArgumentNullException.ThrowIfNull(sendGet);
        ArgumentNullException.ThrowIfNull(schedule);
        _resolveSource = resolveSource;
        _isCarried = isCarried;
        _itemName = itemName;
        _hasHostiles = hasHostiles;
        _inCombat = inCombat;
        _walkToSource = walkToSource;
        _liveWalkToDest = liveWalkToDest;
        _sendGet = sendGet;
        _schedule = schedule;
        _log = log;
    }

    // Begin the walk-to-source → obtain → shortcut-or-long flow. Returns false when
    // the item has no resolvable source (caller falls back to the long route).
    // beforeLongRoute runs just ahead of the walk to the destination when the item
    // didn't turn up: the long route is another card's route, and the caller sets
    // the walk up for that one (how it takes teleports, which gates it goes round)
    // in place of the shortcut's.
    public bool TryBegin(int shortcutItemId, RoomKey destination, Action? beforeLongRoute = null)
    {
        if (_phase != Phase.Idle) return false;
        if (_resolveSource(shortcutItemId) is not { } src) return false;

        _itemId = shortcutItemId;
        _dest = destination;
        _source = src;
        _beforeLongRoute = beforeLongRoute;
        _phase = Phase.WalkingToSource;
        _log?.Info(LogCategory,
            $"shortcut item {shortcutItemId} ('{_itemName(shortcutItemId)}') not held — walking to its source {src}, then destination {destination}");
        _walkToSource(src);
        return true;
    }

    // Walker-event hook (AutoWalkManager.Event). Arrival at the source begins the
    // settle; an unreachable source or a user-driven walk abandons to the long route.
    public void OnWalkEvent(WalkEvent e)
    {
        switch (_phase)
        {
            case Phase.WalkingToSource:
                if (e.Kind == WalkEventKind.Finished && KeyMatches(e.Destination, _source))
                    EnterSettling();
                else if (e.Kind == WalkEventKind.Failed)
                    FallBackLong($"source {_source} unreachable ({e.Detail})");
                else if (e.Kind == WalkEventKind.Stopped)
                    _phase = Phase.Idle;   // user / another engine took over
                break;

            case Phase.Settling:
            case Phase.AwaitingPickup:
                // A fresh user-driven walk means they redirected — stand down. The
                // resume walk WE issue arrives as Started too, but _phase is already
                // Idle by then, so it's ignored.
                if (e.Kind is WalkEventKind.Started or WalkEventKind.Stopped)
                    _phase = Phase.Idle;
                break;
        }
    }

    // Combat-state hook (fires when hostiles clear / combat ends). Re-checks whether
    // the source room has settled enough to grab the drop.
    public void OnCombatStateChanged()
    {
        if (_phase == Phase.Settling) TrySettle();
    }

    // Inventory hook (InventoryManager.Changed). The shortcut item turning up — via
    // auto-get after the kill, our explicit get, or any other means — resumes at once.
    public void OnInventoryChanged()
    {
        if (_phase is Phase.Settling or Phase.AwaitingPickup && _isCarried(_itemId))
            Resume();
    }

    // Reset States: stand down without resuming the walk it was part of.
    public void Cancel() => _phase = Phase.Idle;

    private void EnterSettling()
    {
        _phase = Phase.Settling;
        _log?.Info(LogCategory, $"arrived at shortcut source {_source} — settling before grabbing the drop");
        TrySettle();
    }

    private void TrySettle()
    {
        if (_phase != Phase.Settling) return;
        if (_isCarried(_itemId)) { Resume(); return; }      // already picked up
        if (_hasHostiles() || _inCombat()) return;          // still fighting — wait for the next state change

        // Room is clear. Grab any ground drop the kill left, then resume adaptively
        // once the pickup has had a beat to land.
        _phase = Phase.AwaitingPickup;
        if (_itemName(_itemId) is { Length: > 0 } name)
        {
            _log?.Info(LogCategory, $"source room clear — grabbing '{name}' off the ground");
            _sendGet(name);
        }
        _schedule(PickupSettleMs, () => { if (_phase == Phase.AwaitingPickup) Resume(); });
    }

    // The one live-filter walk to the destination: with the shortcut item now in
    // hand its gate is open so the shortest route IS the shortcut; without it the
    // shortest route is the long reliable one. Either way the walk adapts itself.
    private void Resume()
    {
        RoomKey dest = _dest;
        bool got = _isCarried(_itemId);
        _phase = Phase.Idle;
        _log?.Info(LogCategory, got
            ? $"got the shortcut item — taking the shortcut to {dest}"
            : $"shortcut item unavailable — taking the long route to {dest}");
        if (!got) _beforeLongRoute?.Invoke();
        _liveWalkToDest(dest);
    }

    private void FallBackLong(string why)
    {
        RoomKey dest = _dest;
        _phase = Phase.Idle;
        _log?.Info(LogCategory, $"{why} — falling back to the long route to {dest}");
        _beforeLongRoute?.Invoke();
        _liveWalkToDest(dest);
    }

    private static bool KeyMatches(RoomKey? actual, RoomKey expected)
        => actual.HasValue && actual.Value.Equals(expected);
}
