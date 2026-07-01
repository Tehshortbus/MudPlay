using System.Collections.Generic;
using System.Globalization;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Bridges the walker's planned-route item requirements to the
/// <see cref="NeedsRegistry"/> as <see cref="NeedKind.PathItem"/> needs,
/// and resolves them when the required item enters inventory. Backs the
/// Settings → Other "search rooms if item needed" affordance: while any
/// PathItem need is outstanding (and the feature is on),
/// <see cref="AutoSearchManager"/> arms itself so every room entry issues a
/// bare <c>sea</c>, hunting the concealed item that unlocks the route.
/// </summary>
/// <remarks>
/// <para>
/// At walk-start the walker announces every item id gating an
/// <c>(Item: N)</c> / <c>(Ticket: N)</c> exit along the planned path (see
/// <see cref="AutoWalkManager.SetPathItemAnnouncer"/>). This tracker posts a
/// need for each such item we don't already carry; a need is resolved the
/// moment that item appears in inventory (<see cref="OnInventoryChanged"/>,
/// wired to <c>InventoryManager.Changed</c>). Posting is deduped by the
/// registry, so re-announcing the same route is a no-op.
/// </para>
/// <para>
/// PR B only <see cref="NeedsRegistry.Post">posts</see> and
/// <see cref="NeedsRegistry.Resolve">resolves</see> — there is no active
/// fulfiller, the item is expected to surface from a room search / drop.
/// Later PRs add claimants (shop routing, monster-drop reroute, party
/// obtain) that <see cref="NeedsRegistry.TryClaim">claim</see> the same
/// PathItem needs, which is why this rides the registry rather than a bespoke
/// flag. Needs are dropped on character swap by the registry's
/// <see cref="NeedsRegistry.Clear"/> (wired in <c>AppServices</c>).
/// </para>
/// <para>
/// Inventory is reached through delegates rather than the concrete
/// <c>InventoryManager</c> / <c>ItemNameStore</c> so the post/resolve logic
/// stays unit-testable without a live line stream + game-data load. Each
/// delegate has exactly one production binding in <c>AppServices</c>.
/// </para>
/// </remarks>
public sealed class PathItemDemandTracker
{
    private const string Requester = "PathItemDemandTracker";
    private const string LogCategory = "AutoSearch";

    private readonly NeedsRegistry _needs;
    private readonly Func<int, bool> _isCarried;
    private readonly Func<bool> _inventoryLoaded;
    private readonly Func<bool> _isEnabled;
    private readonly LogService? _log;

    public PathItemDemandTracker(
        NeedsRegistry needs,
        Func<int, bool> isCarried,
        Func<bool> inventoryLoaded,
        Func<bool> isEnabled,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(needs);
        ArgumentNullException.ThrowIfNull(isCarried);
        ArgumentNullException.ThrowIfNull(inventoryLoaded);
        ArgumentNullException.ThrowIfNull(isEnabled);
        _needs = needs;
        _isCarried = isCarried;
        _inventoryLoaded = inventoryLoaded;
        _isEnabled = isEnabled;
        _log = log;
    }

    /// <summary>
    /// True when the "search rooms if item needed" feature is on AND at
    /// least one <see cref="NeedKind.PathItem"/> need is outstanding — i.e.
    /// auto-search should arm to hunt the missing route item. Read live by
    /// <see cref="AutoSearchManager"/>'s demand gate.
    /// </summary>
    public bool SearchDemandActive
        => _isEnabled() && _needs.Outstanding(NeedKind.PathItem).Count > 0;

    /// <summary>
    /// Walk-start callback (bound to
    /// <see cref="AutoWalkManager.SetPathItemAnnouncer"/>): every item id
    /// gating an Item/Ticket exit along the planned route. Posts a PathItem
    /// need for each not currently carried. A no-op when the feature is off
    /// or inventory hasn't been read yet — without a known loadout we can't
    /// tell "missing" from "have it", and guessing would arm a false search.
    /// </summary>
    public void OnPathItemsRequired(IReadOnlyList<int> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (!_isEnabled()) return;
        if (!_inventoryLoaded()) return;
        if (itemIds.Count == 0) return;

        var considered = new HashSet<int>();
        foreach (int id in itemIds)
        {
            if (id <= 0 || !considered.Add(id)) continue;
            if (_isCarried(id)) continue;
            _needs.Post(NeedKind.PathItem, id.ToString(CultureInfo.InvariantCulture), Requester);
        }
    }

    /// <summary>
    /// Inventory-change callback (wired to <c>InventoryManager.Changed</c>):
    /// resolves any outstanding PathItem need whose item is now carried, so
    /// the demand gate — and thus auto-search — drops the moment the route's
    /// missing item turns up.
    /// </summary>
    public void OnInventoryChanged()
    {
        IReadOnlyList<Need> outstanding = _needs.Outstanding(NeedKind.PathItem);
        if (outstanding.Count == 0) return;
        foreach (Need need in outstanding)
        {
            if (int.TryParse(need.Descriptor, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int id)
                && _isCarried(id))
            {
                _needs.Resolve(need);
                _log?.Info(LogCategory, $"path item {id} acquired — search demand cleared");
            }
        }
    }
}
