using System.Collections.Generic;
using System.Globalization;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Bridges the walker's planned-route item requirements to the NeedsRegistry as
// NeedKind.PathItem needs, and resolves them when the required item enters
// inventory. Backs the Settings → Other "search rooms if item needed"
// affordance: while any PathItem need is outstanding (and the feature is on),
// AutoSearchManager arms itself so every room entry issues a bare "sea",
// revealing the concealed item that unlocks the route.
//
// At walk-start the walker announces every item id gating an (Item: N) /
// (Ticket: N) exit along the planned path (see
// AutoWalkManager.SetPathItemAnnouncer). This tracker posts a need for each
// such item we don't already carry; a need is resolved the moment that item
// appears in inventory (OnInventoryChanged, wired to InventoryManager.Changed).
// Posting is deduped by the registry, so re-announcing the same route is a
// no-op.
//
// This tracker only posts and resolves — the fulfillers react off the
// registry: PartyPathItemGate (party give / provision), PathItemShopRouter (buy
// the shortfall at a shop), and MonsterDropRouter (reroute for a drop-only
// item), plus the armed room search that reveals it off the floor. Riding the
// registry rather than a bespoke flag is what lets those claimants coordinate
// on the same need. Needs are dropped on character swap by the registry's Clear
// (wired in AppServices).
//
// Inventory is reached through delegates rather than the concrete
// InventoryManager / ItemNameStore so the post/resolve logic stays
// unit-testable without a live line stream + game-data load. Each delegate has
// exactly one production binding in AppServices.
public sealed class PathItemDemandTracker
{
    private const string Requester = "PathItemDemandTracker";
    private const string LogCategory = "AutoSearch";

    private readonly NeedsRegistry _needs;
    private readonly Func<int, int> _carriedCount;
    private readonly Func<bool> _inventoryLoaded;
    private readonly Func<bool> _isEnabled;
    private readonly LogService? _log;

    public PathItemDemandTracker(
        NeedsRegistry needs,
        Func<int, int> carriedCount,
        Func<bool> inventoryLoaded,
        Func<bool> isEnabled,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(needs);
        ArgumentNullException.ThrowIfNull(carriedCount);
        ArgumentNullException.ThrowIfNull(inventoryLoaded);
        ArgumentNullException.ThrowIfNull(isEnabled);
        _needs = needs;
        _carriedCount = carriedCount;
        _inventoryLoaded = inventoryLoaded;
        _isEnabled = isEnabled;
        _log = log;
    }

    // True when the "search rooms if item needed" feature is on AND at least
    // one PathItem need is outstanding — i.e. auto-search should arm to hunt the
    // missing route item. Read live by AutoSearchManager's demand gate.
    public bool SearchDemandActive
        => _isEnabled() && _needs.Outstanding(NeedKind.PathItem).Count > 0;

    // Walk-start callback (reached via PartyPathItemGate's forward): every item
    // id gating an Item/Ticket exit along the planned route the party can't
    // already cover. Posts a PathItem need for quantity copies (1 solo; the
    // leader's shortfall when provisioning the whole party) for each item we
    // don't already carry that many of. A no-op when the feature is off or
    // inventory hasn't been read yet — without a known loadout we can't tell
    // "missing" from "have it", and guessing would arm a false search.
    public void OnPathItemsRequired(IReadOnlyList<int> itemIds, int quantity = 1)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (!_isEnabled()) return;
        if (!_inventoryLoaded()) return;
        if (itemIds.Count == 0) return;

        int want = Math.Max(1, quantity);
        var considered = new HashSet<int>();
        foreach (int id in itemIds)
        {
            if (id <= 0 || !considered.Add(id)) continue;
            if (_carriedCount(id) >= want) continue;
            _needs.Post(NeedKind.PathItem, id.ToString(CultureInfo.InvariantCulture), Requester, want);
        }
    }

    // Inventory-change callback (wired to InventoryManager.Changed): resolves
    // any outstanding PathItem need once we carry the full requested count — not
    // just the first copy — so the demand gate (and thus auto-search) stays
    // armed until the leader has acquired every copy the party needs, then
    // drops.
    public void OnInventoryChanged()
    {
        IReadOnlyList<Need> outstanding = _needs.Outstanding(NeedKind.PathItem);
        if (outstanding.Count == 0) return;
        foreach (Need need in outstanding)
        {
            if (int.TryParse(need.Descriptor, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int id)
                && _carriedCount(id) >= need.Quantity)
            {
                _needs.Resolve(need);
                _log?.Info(LogCategory, $"path item {id} acquired (x{need.Quantity}) — search demand cleared");
            }
        }
    }
}
