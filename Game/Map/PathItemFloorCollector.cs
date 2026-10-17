using System.Globalization;
using MudPlay.Game.Inventory;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Collects a demanded path item the moment it's revealed on the room floor —
// independent of the Auto-Get engine. Closes a real gap: AutoSearchManager fires
// `sea` while a NeedKind.PathItem is outstanding, but the revealed item is only
// picked up by AutoGetItemsManager, which needs its own master toggle ON and the
// item flagged AutoCollect. So a counter you searched up for a route (the gypsy
// rope) sat on the floor uncollected unless it happened to be an auto-get item.
// This watcher makes the OBTAIN pipeline self-collect: on each "You notice" survey
// it `get`s any outstanding path-item that's now on the floor, so search becomes a
// real sourcing method (the route's own demand drives the grab). One `get` per
// outstanding item per survey; the need resolves when the item enters inventory
// (PathItemDemandTracker), after which it's no longer outstanding and isn't re-got.
public sealed class PathItemFloorCollector : IDisposable
{
    private readonly NeedsRegistry _needs;
    private readonly Func<int, bool> _isOnFloor;
    private readonly Func<int, string?> _itemName;
    private readonly Action<string> _send;
    private readonly LogService? _log;
    private GroundItemTracker? _ground;

    public PathItemFloorCollector(
        NeedsRegistry needs,
        Func<int, bool> isOnFloor,
        Func<int, string?> itemName,
        Action<string> send,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(needs);
        ArgumentNullException.ThrowIfNull(isOnFloor);
        ArgumentNullException.ThrowIfNull(itemName);
        ArgumentNullException.ThrowIfNull(send);
        _needs = needs;
        _isOnFloor = isOnFloor;
        _itemName = itemName;
        _send = send;
        _log = log;
    }

    public void Attach(GroundItemTracker ground)
    {
        ArgumentNullException.ThrowIfNull(ground);
        if (_ground is not null) _ground.SurveyUpdated -= OnSurvey;
        _ground = ground;
        _ground.SurveyUpdated += OnSurvey;
    }

    // The collect pass, run off the floor-survey event.
    internal void CollectRevealed()
    {
        foreach (Need n in _needs.Outstanding(NeedKind.PathItem))
        {
            if (!int.TryParse(n.Descriptor, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                continue;
            if (!_isOnFloor(id)) continue;
            if (_itemName(id) is not { Length: > 0 } name) continue;
            _send($"get {name}");
            _log?.Info("PathItemFloor",
                $"demanded path item {id} '{name}' revealed on the floor — collecting with `get`");
        }
    }

    // The master switch (true = off): off, a route item seen on the floor is not
    // picked up. Stateless, so the next survey after it is back on collects it.
    public Func<bool>? MasterSwitchOff { get; set; }

    private void OnSurvey()
    {
        if (MasterSwitchOff?.Invoke() == true) return;
        CollectRevealed();
    }

    public void Dispose()
    {
        if (_ground is not null) _ground.SurveyUpdated -= OnSurvey;
        _ground = null;
    }
}
