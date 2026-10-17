using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// One resolved way to be handed an item on demand: a room to stand in, the
// verbatim command to type there, and the giver's display name for the log.
// AppServices builds these off ItemSourceIndex — a Monster giver becomes
// `ask <noun> <keyword>` at each of its spawn rooms (the noun is the last word of
// the name, since the game's `ask` parser takes a single-word target), a Room
// giver becomes the bare room-CMD keyword typed in that room (e.g. "insert fang").
//
// RefusalLines are what this giver says when a condition on the hand-over isn't
// met (the old hermit's "You can't serve your cause like that!"), so a refused ask
// is recognised at once instead of by the item never arriving.
//
// TakesItemId is the item a trade takes in exchange, zero for a free hand-over.
// GiverName then says so ("sleazy shopkeeper, in trade for your opal brooch").
public readonly record struct GiveSource(
    RoomKey Room, string Command, string GiverName, IReadOnlyList<string>? RefusalLines = null,
    int TakesItemId = 0);

// Active fulfiller for NeedKind.PathItem needs an NPC / room hands over on a
// keyword: when a one-shot walk crosses an (Item: N) / (Ticket: N) gate whose item
// we're not carrying and a textblock `giveitem` supplies it (an
// `ask <npc> <keyword>` dialogue give, or a room-CMD keyword give), detour to
// the giver that adds the fewest steps, issue the command, and resume once the
// item lands. Backs the item record's "Auto-obtain for path" flag
// (ItemOverlay.AutoObtainForPath).
//
// The candidates come from PathItemGiveSources, already resolved to a room and a
// command, so a list is always something we can walk to and ask for. They are of
// two kinds:
//   - A free hand-over: keyword-carrying and ungated. It costs nothing and needs
//     no RNG, so the shop and drop routers stand down whenever one exists.
//   - A plain trade, which hands over one item of the user's for the wanted one.
//     It reaches here only for a door key nothing else yields, with the item to
//     hand over in the pack, after the user picked a route card that named the
//     trade. It never stands in for a free give, a purchase or a summon.
// A purchase, a quest reward, a conditioned turn-in or a `random` roll is excluded
// upstream — none is a reliable one-command hand-over.
//
// Auto-detour, don't prompt. A free give is cheap, certain and a single command,
// so it runs without asking. A trade was asked about already, on the card.
//
// Giver selection. Among the candidate rooms, pick the one minimising
// dist(cur, giver) + dist(giver, dest) — the fewest steps added to the trip,
// the same metric the shop router uses. Distances use the walker's own
// IRoomFilter so the estimate matches the walk that runs. Ties break on the
// nearer giver, then room-key order, for determinism.
//
// The command issues once. A give either fires or it doesn't; re-asking a
// deterministic giver isn't a known way to draw a second copy, so we don't
// invent one — a shortfall beyond the first hand-over falls through the buy
// window to search rather than spamming the NPC.
//
// Scope. Detours apply only to a plain walk-to. When a loop or auto-lair run is
// driving movement (engineWalkActive) the need is left to demand-driven search.
//
// Resolution paths. The item entering inventory (OnInventoryChanged) resumes the
// original walk from any active phase — the give landing, or a search / party
// hand-off revealing it en route (found-first abort). A walk that fails, a give
// that doesn't land within the window, and a user redirect all resume / abandon
// gracefully and leave the need outstanding for the other fulfillers. The need's
// own lifecycle (post / resolve) stays owned by PathItemDemandTracker; this
// router only reacts.
//
// Inventory / graph / walker are reached through delegates. Each delegate has exactly one production binding in AppServices.
public sealed class PathItemGiveRouter : IDisposable
{
    private const string LogCategory = "AutoSearch";

    private enum Phase
    {
        Idle,
        WalkingToGiver,
        Acquiring,
    }

    private readonly Func<int, IReadOnlyList<GiveSource>> _giveSourcesForItem;
    private readonly Func<RoomKey?> _currentRoom;
    private readonly Func<RoomKey?> _walkDestination;
    private readonly Func<RoomKey, RoomKey, int?> _distanceBetween;
    private readonly Func<int, int> _carriedCount;
    private readonly Func<int, string?> _itemName;
    private readonly Func<int, bool> _isEnabled;
    private readonly Func<bool> _engineWalkActive;
    private readonly Action<RoomKey> _walkTo;
    private readonly Action<Action> _post;
    private readonly LogService? _log;
    private readonly TimeSpan _giveTimeout;
    private readonly WireSender _wire = new();
    private IDisposable? _giveTimer;
    // Arms one tick after the delay; disposing what it returns cancels it.
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;

    private Phase _phase = Phase.Idle;
    // Items a giver didn't hand over on this trip. The resumed walk re-announces
    // what it still lacks, which would send us straight back to ask again; held
    // until that walk ends, so a later walk asks afresh.
    private readonly HashSet<int> _declined = new();
    private RoomKey? _lastGiverRoom;
    private int _itemId;
    private int _targetCount = 1;
    private RoomKey _origDest;
    private GiveSource _giver;

    public PathItemGiveRouter(
        Func<int, IReadOnlyList<GiveSource>> giveSourcesForItem,
        Func<RoomKey?> currentRoom,
        Func<RoomKey?> walkDestination,
        Func<RoomKey, RoomKey, int?> distanceBetween,
        Func<int, int> carriedCount,
        Func<int, string?> itemName,
        Func<int, bool> isEnabled,
        Func<bool> engineWalkActive,
        Action<RoomKey> walkTo,
        Action<Action> post,
        LogService? log = null,
        TimeSpan? giveTimeout = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        ArgumentNullException.ThrowIfNull(giveSourcesForItem);
        ArgumentNullException.ThrowIfNull(currentRoom);
        ArgumentNullException.ThrowIfNull(walkDestination);
        ArgumentNullException.ThrowIfNull(distanceBetween);
        ArgumentNullException.ThrowIfNull(carriedCount);
        ArgumentNullException.ThrowIfNull(itemName);
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(engineWalkActive);
        ArgumentNullException.ThrowIfNull(walkTo);
        ArgumentNullException.ThrowIfNull(post);
        _giveSourcesForItem = giveSourcesForItem;
        _currentRoom = currentRoom;
        _walkDestination = walkDestination;
        _distanceBetween = distanceBetween;
        _carriedCount = carriedCount;
        _itemName = itemName;
        _isEnabled = isEnabled;
        _engineWalkActive = engineWalkActive;
        _walkTo = walkTo;
        _post = post;
        _schedule = schedule
            ?? ((delay, tick) => new Timer(_ => _post(tick), null, delay, Timeout.InfiniteTimeSpan));
        _log = log;
        _giveTimeout = giveTimeout ?? TimeSpan.FromSeconds(8);
    }

    // Bind the wire sink used to issue the give command.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // True while a give detour is walking to the giver or waiting for the item.
    public bool DetourActive => _phase != Phase.Idle;

    // The destination this detour will resume to once the item is in hand — the
    // walk the user actually asked for, held aside while we fetch the give. Null
    // when no detour is running. Lets the nav display draw the whole journey
    // (current → giver → this) instead of stopping the route line at the giver.
    public RoomKey? OnwardDestination => _phase == Phase.Idle ? null : _origDest;

    // The giver room of the running detour, or of the last one. A loop on its
    // approach reads it to tell this router's own walk there from its approach.
    public RoomKey? LastGiverRoom => _lastGiverRoom;

    // Items asked for and not handed over on the walk in progress (bug report).
    public IReadOnlyCollection<int> Declined => _declined;

    // New-need callback (wired to NeedsRegistry.NeedPosted). Arms a detour toward
    // the fewest-added-steps giver when the item is flagged, no engine walk is
    // driving, a give exists, and we can route both to the giver and on to the
    // destination. A no-op otherwise.
    public void OnNeedPosted(Need need)
    {
        if (need.Kind != NeedKind.PathItem) return;
        if (_phase != Phase.Idle) return;
        if (_engineWalkActive()) return;

        if (!int.TryParse(need.Descriptor, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int itemId)
            || itemId <= 0)
            return;
        if (!_isEnabled(itemId)) return;   // per-item auto-obtain gate
        if (_declined.Contains(itemId)) return;   // asked on this trip already; the walk goes on without it
        int target = Math.Max(1, need.Quantity);
        if (_carriedCount(itemId) >= target) return;   // already hold the shortfall

        string? name = _itemName(itemId);
        if (string.IsNullOrWhiteSpace(name)) return;

        if (_currentRoom() is not { } cur) return;
        if (_walkDestination() is not { } dest) return;

        if (!TrySelectGiver(cur, dest, itemId, out GiveSource giver)) return;

        _itemId = itemId;
        _targetCount = target;
        _origDest = dest;
        _giver = giver;
        _lastGiverRoom = giver.Room;
        _phase = Phase.WalkingToGiver;
        _log?.Info(LogCategory,
            $"path item {itemId} ('{name}') given by {giver.GiverName} — detouring to {giver.Room} to '{giver.Command}'");
        _post(() => _walkTo(giver.Room));
    }

    // Walker-event callback (wired to AutoWalkManager.Event). Arrival at the giver
    // issues the command; a failed walk resumes to the destination; a user-driven
    // walk abandons the detour.
    public void OnWalkEvent(WalkEvent e)
    {
        switch (_phase)
        {
            case Phase.Idle:
                // The walk a failed give resumed is over, one way or the other.
                if (e.Kind is WalkEventKind.Finished or WalkEventKind.Failed or WalkEventKind.Stopped)
                    _declined.Clear();
                break;

            case Phase.WalkingToGiver:
                if (e.Kind == WalkEventKind.Finished && KeyMatches(e.Destination, _giver.Room))
                    BeginAcquiring();
                else if (e.Kind == WalkEventKind.Failed)
                {
                    _log?.Info(LogCategory,
                        $"giver at {_giver.Room} unreachable ({e.Detail}) — resuming to {_origDest}");
                    ResumeToPath();
                }
                else if (e.Kind == WalkEventKind.Stopped)
                    Reset(); // user / another engine took over — abandon quietly
                break;

            case Phase.Acquiring:
                // Idle at the giver while the give settles. A fresh walk means the
                // user redirected — drop the detour and let them drive.
                if (e.Kind is WalkEventKind.Started or WalkEventKind.Stopped)
                    Reset();
                break;
        }
    }

    // Inventory-change callback (wired to InventoryManager.Changed). When the item
    // we detoured for is now carried — the give landed, or search / a party
    // hand-off revealed it en route — resume the original walk. The found case
    // doubles as the found-first abort for an in-flight walk to the giver.
    public void OnInventoryChanged()
    {
        if (_phase is not (Phase.WalkingToGiver or Phase.Acquiring)) return;
        if (_carriedCount(_itemId) < _targetCount) return;   // still short
        _log?.Info(LogCategory, $"path item {_itemId} acquired — resuming to {_origDest}");
        ResumeToPath();
    }

    // Give-window elapsed. If the item still isn't carried the give didn't land
    // (dialogue mismatch, a hidden prerequisite, a moved NPC) — resume to the
    // destination and leave the need outstanding for the other fulfillers.
    // Runs on the UI thread.
    private void OnGiveTimeout()
    {
        if (_phase != Phase.Acquiring) return;
        if (_carriedCount(_itemId) >= _targetCount) return; // race: OnInventoryChanged handled it
        GiveFailed("did not land in time");
    }

    // A line from the server while we wait on the give. The giver's own refusal
    // (a condition on the hand-over we don't meet) ends the wait at once. A give
    // refused for a missed quest step prints nothing, and is caught by the timeout.
    public void OnLine(string text)
    {
        if (_phase != Phase.Acquiring || _giver.RefusalLines is not { Count: > 0 } refusals) return;
        string line = text.Trim();
        foreach (string refusal in refusals)
            if (line.EndsWith(refusal, StringComparison.Ordinal))
            {
                GiveFailed($"was refused (\"{refusal}\")");
                return;
            }
    }

    // The ask produced no item. The walk goes on regardless: what it needed the
    // item for may be passable anyway (a key door someone left open), and if not,
    // the walk stops there and names what it lacks. It isn't asked for again on
    // this trip.
    private void GiveFailed(string how)
    {
        _declined.Add(_itemId);
        _log?.Info(LogCategory,
            $"give of path item {_itemId} by {_giver.GiverName} {how} — going on to {_origDest} without it");
        ResumeToPath();
    }

    public void Dispose() => _giveTimer?.Dispose();

    private void BeginAcquiring()
    {
        _phase = Phase.Acquiring;
        _log?.Info(LogCategory, $"at giver {_giver.Room} — '{_giver.Command}' for path item {_itemId}");
        _wire.Send(_giver.Command);
        // Re-read the pack immediately after the ask. The hand-over line is
        // per-give flavor text ("The gnome commander gives you the heavy bloodstone
        // orb.") — wording we can't recognise and mustn't try to, since every giver
        // words it differently. Without a re-read the item sat in the pack unnoticed,
        // the need never resolved, the give window expired, and the walk resumed and
        // failed for want of an item it was already carrying (report
        // paradigm-20260911-103025). The `i` reply is a plain "You are carrying …"
        // line the inventory parse already owns, so asking is the reliable test of
        // whether the give landed. Two commands is far inside the realms' burst
        // allowance, and the game processes them in order, so the listing reflects
        // the give.
        _wire.Send("i");
        ArmGiveTimer();
    }

    private void ResumeToPath()
    {
        DisarmGiveTimer();
        RoomKey dest = _origDest;
        _phase = Phase.Idle;
        _post(() => _walkTo(dest));
    }

    // Reset States: stand down without resuming the walk it was part of.
    public void Cancel() => Reset();

    private void Reset()
    {
        DisarmGiveTimer();
        _phase = Phase.Idle;
    }

    private void ArmGiveTimer()
    {
        _giveTimer?.Dispose();
        _giveTimer = _schedule(_giveTimeout, OnGiveTimeout);
    }

    private void DisarmGiveTimer()
    {
        _giveTimer?.Dispose();
        _giveTimer = null;
    }

    private bool TrySelectGiver(RoomKey cur, RoomKey dest, int itemId, out GiveSource best)
        => TrySelectGiver(_giveSourcesForItem(itemId), cur, dest, _distanceBetween, out best);

    // Pick the give source minimising dist(cur,giver)+dist(giver,dest) among the
    // candidates reachable both ways. Ties break on the nearer giver, then
    // room-key order, so the pick is deterministic. Static + internal so the
    // route picker can name the very giver this router would detour to without
    // duplicating the selection rule.
    internal static bool TrySelectGiver(
        IReadOnlyList<GiveSource> givers, RoomKey cur, RoomKey dest,
        Func<RoomKey, RoomKey, int?> distanceBetween, out GiveSource best)
    {
        ArgumentNullException.ThrowIfNull(givers);
        ArgumentNullException.ThrowIfNull(distanceBetween);
        best = default;
        int bestTotal = int.MaxValue;
        int bestToGiver = int.MaxValue;
        foreach (GiveSource g in givers)
        {
            if (distanceBetween(cur, g.Room) is not { } toGiver) continue;
            if (distanceBetween(g.Room, dest) is not { } toDest) continue;
            int total = toGiver + toDest;
            bool better = total < bestTotal
                || (total == bestTotal && toGiver < bestToGiver)
                || (total == bestTotal && toGiver == bestToGiver && CompareKeys(g.Room, best.Room) < 0);
            if (better)
            {
                bestTotal = total;
                bestToGiver = toGiver;
                best = g;
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
