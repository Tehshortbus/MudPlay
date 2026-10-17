using System.Collections.Generic;
using System.Globalization;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Bridges the walker's planned-route item requirements to the NeedsRegistry as
// NeedKind.PathItem needs, and resolves them when the required item enters
// inventory. While any PathItem need is outstanding and Auto-Search is on,
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
// InventoryManager / ItemNameStore. Each delegate has
// exactly one production binding in AppServices.
public sealed class PathItemDemandTracker
{
    private const string Requester = "PathItemDemandTracker";
    private const string LogCategory = "AutoSearch";

    private readonly NeedsRegistry _needs;
    private readonly Func<int, int> _carriedCount;
    private readonly Func<bool> _inventoryLoaded;
    // Posting gate: whether a PathItem need is registered at all when the walker
    // demands one. On while the route picker's pick has this journey obtain the
    // item — posting the need is what arms the shop / give / drop fulfillers (the
    // reliable acquire path), so it stays open even with master auto-search off,
    // or the buy-at-shop fallback would never fire.
    private readonly Func<bool> _isEnabled;
    // Search-demand gate: whether an outstanding need should arm the per-room `sea`.
    // Deliberately SEPARATE from _isEnabled — a picker forced-obtain still posts
    // (so the shop buys) but its en-route searching is governed by the master
    // auto-search toggle, not the forced-obtain flag: "auto-search is the driver of
    // `sea` while moving", so toggling it off stops the search and leaves the walk
    // to the shop-buy fallback. Falls back to _isEnabled when not supplied.
    private readonly Func<bool> _searchEnabled;
    // "Is a room search a plausible way to get this item?" — false for an item with
    // a deterministic source (a keyword give, a guaranteed summon), which the run
    // is already detouring to collect. Null means every need is search-worthy,
    // preserving the original behaviour for callers that don't supply it.
    private readonly Func<int, bool>? _isSearchWorthy;
    private readonly LogService? _log;

    public PathItemDemandTracker(
        NeedsRegistry needs,
        Func<int, int> carriedCount,
        Func<bool> inventoryLoaded,
        Func<bool> isEnabled,
        Func<int, bool>? isSearchWorthy = null,
        Func<bool>? searchEnabled = null,
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
        _searchEnabled = searchEnabled ?? isEnabled;
        _isSearchWorthy = isSearchWorthy;
        _log = log;
    }

    // True when the search-demand gate is open AND at least one outstanding PathItem
    // need is actually worth searching for — i.e. auto-search should arm to hunt the
    // missing route item. Read live by AutoSearchManager's demand gate.
    //
    // A need whose item has a DETERMINISTIC source is not worth searching for: the
    // run is already walking to an NPC who hands it over on a keyword, or to a room
    // command that summons a guaranteed dropper. Searching every room on the way
    // there buys nothing and costs a `sea` per room — the whole trip to the gnome
    // commander for a bloodstone orb searched every room it crossed (report
    // paradigm-20260911-100708). A shop item or a percentage drop DOES stay
    // search-worthy: finding one loose is strictly better than paying or grinding
    // for it.
    public bool SearchDemandActive
    {
        get
        {
            if (!_searchEnabled()) return false;
            IReadOnlyList<Need> outstanding = _needs.Outstanding(NeedKind.PathItem);
            if (outstanding.Count == 0) return false;
            if (_isSearchWorthy is null) return true;
            foreach (Need n in outstanding)
                if (!int.TryParse(n.Descriptor, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int id)
                    || _isSearchWorthy(id))
                    return true;
            return false;
        }
    }

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
        bool postedAny = false;
        foreach (int id in itemIds)
        {
            if (id <= 0 || !considered.Add(id)) continue;
            if (_carriedCount(id) >= want) continue;
            _needs.Post(NeedKind.PathItem, id.ToString(CultureInfo.InvariantCulture), Requester, want);
            postedAny = true;
        }

        // Re-offer whatever is still outstanding. Post announces only NEW needs, so
        // a fulfiller that had to stand down when the need was first posted — the
        // second item on a two-gate route, deferred so two routers couldn't fight
        // over the walk — would never be asked again, and the route would fail at a
        // gate nobody was sent after. This walk is the previous detour's own resume,
        // so the deferring router is free again by now.
        if (postedAny) _needs.Reoffer(NeedKind.PathItem);
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
