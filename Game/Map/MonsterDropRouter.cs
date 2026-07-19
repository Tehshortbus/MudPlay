using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// One candidate spawn site for a monster-dropped item: a room the
// dropping monster spawns in, tagged with the monster and its drop chance
// (0 = unknown) so the reroute prompt can name it.
public readonly record struct MonsterDropSpawn(
    RoomKey Room, int MonsterId, string MonsterName, int DropPercent);

// Active fulfiller for NeedKind.PathItem needs that no shop can satisfy:
// when a one-shot walk crosses an (Item: N) / (Ticket: N) gate whose item
// we're not carrying and no shop stocks it, find which monster drops it,
// and — with the user's confirmation — reroute to the nearest room that
// monster spawns in so they can hunt it, then resume to the original
// destination. Backs the item record's "Auto-obtain for path → source from
// drops" flag (ItemOverlay.SourceFromDropsForPath under the AutoObtainForPath
// master opt-in).
//
// Division of labour with PathItemShopRouter. Both react to the same
// NeedsRegistry.NeedPosted event and are mutually exclusive: the shop router
// acts when a shop sells the item; this router acts only when none does. A
// shop that sells the item but is unreachable stays the shop router's
// problem — it isn't re-sourced as a hunt.
//
// Prompt, don't auto-detour. Unlike a shop buy — cheap, deterministic, one
// command — a hunt is a real commitment: travel to a lair, fight, and hope
// for a percentage drop. So instead of silently rerouting, this asks and
// only walks on a yes. A no leaves the need outstanding for demand-driven
// search.
//
// Nearest spawn. A common monster spawns in hundreds of rooms, so the target
// is chosen with a single forward BFS from the current room and an
// O(candidates) scan for the minimum distance — not the shop router's
// per-candidate round-trip metric, which would be prohibitive at this
// fan-out. Ties break on the higher drop chance, then room-key order, for
// determinism.
//
// Resolution paths. The item entering inventory (OnInventoryChanged) resumes
// the original walk from any active phase — covering the hunt paying off, a
// search revealing it en route (found-first abort), and a party hand-off. A
// walk that fails or a user redirect abandons the reroute gracefully. The
// need's own lifecycle (post / resolve) stays owned by PathItemDemandTracker;
// this router only reacts.
//
// Inventory / graph / walker / confirm are reached through delegates so the
// FSM stays unit-testable without a live line stream, room graph, dispatcher,
// and dialog. Each delegate has one production binding in AppServices.
public sealed class MonsterDropRouter
{
    private const string LogCategory = "AutoSearch";

    private enum Phase
    {
        Idle,
        AwaitingConfirm,
        WalkingToSpawn,
        Hunting,
    }

    private readonly Func<int, IReadOnlyList<MonsterDropSpawn>> _dropSpawnsForItem;
    private readonly Func<int, bool> _anyShopSells;
    private readonly Func<RoomKey?> _currentRoom;
    private readonly Func<RoomKey?> _walkDestination;
    private readonly Func<RoomKey, IReadOnlyDictionary<RoomKey, int>> _distancesFrom;
    private readonly Func<int, bool> _isCarried;
    private readonly Func<int, string?> _itemName;
    private readonly Func<int, bool> _isEnabled;
    private readonly Func<bool> _engineWalkActive;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Action<RoomKey> _walkTo;
    private readonly Action<Action> _post;
    private readonly LogService? _log;

    private Phase _phase = Phase.Idle;
    private int _itemId;
    private RoomKey _origDest;
    private MonsterDropSpawn _spawn;

    public MonsterDropRouter(
        Func<int, IReadOnlyList<MonsterDropSpawn>> dropSpawnsForItem,
        Func<int, bool> anyShopSells,
        Func<RoomKey?> currentRoom,
        Func<RoomKey?> walkDestination,
        Func<RoomKey, IReadOnlyDictionary<RoomKey, int>> distancesFrom,
        Func<int, bool> isCarried,
        Func<int, string?> itemName,
        Func<int, bool> isEnabled,
        Func<bool> engineWalkActive,
        Func<string, string, Task<bool>> confirm,
        Action<RoomKey> walkTo,
        Action<Action> post,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(dropSpawnsForItem);
        ArgumentNullException.ThrowIfNull(anyShopSells);
        ArgumentNullException.ThrowIfNull(currentRoom);
        ArgumentNullException.ThrowIfNull(walkDestination);
        ArgumentNullException.ThrowIfNull(distancesFrom);
        ArgumentNullException.ThrowIfNull(isCarried);
        ArgumentNullException.ThrowIfNull(itemName);
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(engineWalkActive);
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(walkTo);
        ArgumentNullException.ThrowIfNull(post);
        _dropSpawnsForItem = dropSpawnsForItem;
        _anyShopSells = anyShopSells;
        _currentRoom = currentRoom;
        _walkDestination = walkDestination;
        _distancesFrom = distancesFrom;
        _isCarried = isCarried;
        _itemName = itemName;
        _isEnabled = isEnabled;
        _engineWalkActive = engineWalkActive;
        _confirm = confirm;
        _walkTo = walkTo;
        _post = post;
        _log = log;
    }

    // True while a reroute is pending confirmation or in progress.
    public bool DetourActive => _phase != Phase.Idle;

    // New-need callback (wired to NeedsRegistry.NeedPosted). When the item
    // warrants a hunt — item flagged for source-from-drops, no engine walk, no
    // shop sells it, at least one dropper spawns in a reachable room — arms the
    // confirmation prompt toward the nearest such spawn. A no-op otherwise.
    public void OnNeedPosted(Need need)
    {
        if (need.Kind != NeedKind.PathItem) return;
        if (_phase != Phase.Idle) return;
        if (_engineWalkActive()) return;

        if (!int.TryParse(need.Descriptor, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int itemId)
            || itemId <= 0)
            return;
        if (!_isEnabled(itemId)) return;   // per-item auto-obtain (source-from-drops method) gate
        if (_isCarried(itemId)) return;

        string? name = _itemName(itemId);
        if (string.IsNullOrWhiteSpace(name)) return;

        if (_currentRoom() is not { } cur) return;
        if (_walkDestination() is not { } dest) return;

        // Shop items are PathItemShopRouter's job — this router only sources
        // what no shop sells.
        if (_anyShopSells(itemId)) return;

        if (!TrySelectNearestSpawn(cur, itemId, out MonsterDropSpawn spawn, out int distance))
            return;

        _itemId = itemId;
        _origDest = dest;
        _spawn = spawn;
        _phase = Phase.AwaitingConfirm;

        string chance = spawn.DropPercent > 0 ? $" ({spawn.DropPercent}%)" : string.Empty;
        string title = $"Reroute to find {name}?";
        string body =
            $"No shop sells {name}. {spawn.MonsterName}{chance} drops it, " +
            $"{distance} room(s) away. Reroute there to hunt it, then continue to your destination?";
        _log?.Info(LogCategory,
            $"path item {itemId} ('{name}') unsold — asking to hunt '{spawn.MonsterName}' at {spawn.Room}");
        _post(() => _ = PromptThenWalkAsync(title, body));
    }

    // Walker-event callback (wired to AutoWalkManager.Event). Arrival at the
    // spawn starts the hunt; a failed walk resumes to the destination; a
    // user-driven walk abandons the reroute.
    public void OnWalkEvent(WalkEvent e)
    {
        switch (_phase)
        {
            case Phase.AwaitingConfirm:
                // User started driving while the prompt is still open — drop
                // the reroute; a late "yes" is ignored (phase no longer matches).
                if (e.Kind is WalkEventKind.Started or WalkEventKind.Stopped)
                    _phase = Phase.Idle;
                break;

            case Phase.WalkingToSpawn:
                if (e.Kind == WalkEventKind.Finished && KeyMatches(e.Destination, _spawn.Room))
                    BeginHunting();
                else if (e.Kind == WalkEventKind.Failed)
                {
                    _log?.Info(LogCategory,
                        $"spawn at {_spawn.Room} unreachable ({e.Detail}) — resuming to {_origDest}");
                    ResumeToPath();
                }
                else if (e.Kind == WalkEventKind.Stopped)
                    _phase = Phase.Idle; // user / another engine took over — abandon quietly
                break;

            case Phase.Hunting:
                // Waiting at the lair for the drop. A fresh walk means the
                // user redirected — stop waiting and let them drive.
                if (e.Kind is WalkEventKind.Started or WalkEventKind.Stopped)
                    _phase = Phase.Idle;
                break;
        }
    }

    // Inventory-change callback (wired to InventoryManager.Changed). When the
    // item we rerouted for is now carried — dropped by the hunt, revealed by
    // search en route, or handed over — resume the original walk. Doubles as
    // the found-first abort for a pending prompt or an in-flight spawn walk.
    public void OnInventoryChanged()
    {
        if (_phase is not (Phase.AwaitingConfirm or Phase.WalkingToSpawn or Phase.Hunting))
            return;
        if (!_isCarried(_itemId)) return;
        _log?.Info(LogCategory, $"path item {_itemId} acquired — resuming to {_origDest}");
        ResumeToPath();
    }

    private async Task PromptThenWalkAsync(string title, string body)
    {
        bool ok;
        try
        {
            ok = await _confirm(title, body);
        }
        catch
        {
            // A dialog that faults must not strand the FSM in AwaitingConfirm.
            if (_phase == Phase.AwaitingConfirm) _phase = Phase.Idle;
            return;
        }

        // The item may have turned up, or the user may have taken over, while
        // the prompt was open — honour that over a stale answer.
        if (_phase != Phase.AwaitingConfirm) return;

        if (ok)
        {
            _phase = Phase.WalkingToSpawn;
            _log?.Info(LogCategory,
                $"rerouting to spawn {_spawn.Room} to hunt '{_spawn.MonsterName}' for item {_itemId}");
            _walkTo(_spawn.Room);
        }
        else
        {
            _phase = Phase.Idle;
            _log?.Info(LogCategory,
                $"hunt for item {_itemId} declined — leaving it to search");
        }
    }

    private void BeginHunting()
    {
        _phase = Phase.Hunting;
        _log?.Info(LogCategory,
            $"arrived at spawn {_spawn.Room} — hunt '{_spawn.MonsterName}' for item {_itemId}");
    }

    private void ResumeToPath()
    {
        RoomKey dest = _origDest;
        _phase = Phase.Idle;
        _post(() => _walkTo(dest));
    }

    // Pick the reachable spawn room minimising dist(cur, spawn). A single
    // forward BFS (distancesFrom) covers every candidate; the ranking itself is
    // the shared static below so the route picker can name the same lair.
    private bool TrySelectNearestSpawn(
        RoomKey cur, int itemId, out MonsterDropSpawn best, out int bestDistance)
        => SelectNearestSpawn(_dropSpawnsForItem(itemId), _distancesFrom(cur), out best, out bestDistance);

    // Nearest reachable spawn among candidates by the given distance map; ties
    // break on the higher drop chance, then room-key order — deterministic for
    // tests. Static + internal so AppServices can resolve the picker's "dropped
    // by <monster>" tail to the exact spawn a reroute would target.
    internal static bool SelectNearestSpawn(
        IReadOnlyList<MonsterDropSpawn> candidates,
        IReadOnlyDictionary<RoomKey, int> distances,
        out MonsterDropSpawn best, out int bestDistance)
    {
        best = default;
        bestDistance = int.MaxValue;

        if (candidates.Count == 0) return false;

        bool found = false;
        foreach (MonsterDropSpawn c in candidates)
        {
            if (!distances.TryGetValue(c.Room, out int d)) continue; // unreachable / unknown
            bool better = !found
                || d < bestDistance
                || (d == bestDistance && c.DropPercent > best.DropPercent)
                || (d == bestDistance && c.DropPercent == best.DropPercent
                    && CompareKeys(c.Room, best.Room) < 0);
            if (better)
            {
                bestDistance = d;
                best = c;
                found = true;
            }
        }
        return found;
    }

    private static bool KeyMatches(RoomKey? actual, RoomKey expected)
        => actual.HasValue && actual.Value.Equals(expected);

    private static int CompareKeys(RoomKey a, RoomKey b)
    {
        int byMap = a.Map.CompareTo(b.Map);
        return byMap != 0 ? byMap : a.Room.CompareTo(b.Room);
    }
}
