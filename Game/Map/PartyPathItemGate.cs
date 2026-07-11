using System.Collections.Generic;
using System.Text;
using FujinTerm.Game.Remote;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Party-first stage of the path-item demand pipeline. Sits between the walker's
// walk-start item announcer and PathItemDemandTracker: when a planned route
// crosses an (Item: N) / (Ticket: N) gate whose per-member item the party might
// lack, this consults the party (via PartyInventoryProbe) before spending a
// search / shop detour on it. Backs the item record's "Auto-obtain for path →
// provision party" flag (ItemOverlay.ProvisionPartyForPath under the
// AutoObtainForPath master opt-in) — a per-item gate, so the announced list is
// partitioned: flagged items go through the party pool, the rest pass straight
// through to the per-item demand pipeline unchanged.
//
// The behaviour splits on who's driving:
//
// Leader — provision the whole party. The leader is the one walking a party
// route (followers are held by the movement gate), so it owns getting everyone
// across the gate. For each gated item it probes the party for a count, then
// treats the party (self + everyone who replied) as one pool that needs exactly
// one copy each. Members keep whatever they're holding — nothing is
// redistributed early. If the pool already holds enough
// (totalHeld >= partySize), the leader immediately coordinates the hand-off so
// every zero-holder ends up with one: it gives its own spares directly
// (give <item> to <R>) and directs other holders' spares with a targeted
// /<holder> @do give <item> to <R>. If the pool is short, the shortfall count —
// how many copies the leader's own bag must gain to reach one-per-member — is
// forwarded to the demand pipeline (search / shop, per the user's checked
// options), so a shop detour buys that many rather than a single copy; when
// "search rooms if item needed" is on, the slot is retained so
// SearchDemandActive keeps auto-search armed past the first copy and the
// redistribution fires (via OnInventoryChanged) the moment the pool becomes
// whole.
//
// Non-leader — borrow a spare for self. A grouped follower doing its own
// walk-to: if it lacks a gated item and a member holds a spare (count >= 2,
// holder keeps one), it asks for a single hand-off to itself rather than
// posting a search / shop need.
//
// Both paths degrade to forwarding an item unchanged when it isn't flagged for
// party provisioning or we're solo. The probe round-trip is async (a telepath
// each way), so the decision is deferred off the walker's WalkTo call stack
// through _post.
//
// Scope: multiple copies are acquired by the forwarded shortfall count — a shop
// detour buys that many, and auto-search stays armed (until the pool is whole)
// to reveal the rest off the floor. Monster-drop reroute remains single-copy
// best-effort. Non-responders are treated as outside the pool — the leader
// provisions only itself and the members that answered, leaving a silent member
// to its own per-member pipeline.
public sealed class PartyPathItemGate
{
    private const string LogCategory = "AutoSearch";

    private static readonly IReadOnlyDictionary<string, int> NoCounts =
        new Dictionary<string, int>();

    // A route item the leader is provisioning: its name and the per-member
    // counts captured from the probe (given name → copies held).
    private sealed record Pending(int Id, string Name, IReadOnlyDictionary<string, int> Others);

    private readonly Func<int, bool> _isCarried;
    private readonly Func<int, int> _selfCount;
    private readonly Func<int, string, Task<PartyInventoryProbe.PartyItemResult>> _query;
    private readonly Func<int, string?> _itemName;
    private readonly Func<int, bool> _isEnabled;
    private readonly Func<bool> _searchEnabled;
    private readonly Func<bool> _inParty;
    private readonly Func<bool> _selfIsLeader;
    private readonly Func<string?> _selfGivenName;
    private readonly Action<IReadOnlyList<int>, int> _forward;
    private readonly Action<Action> _post;
    private readonly LogService? _log;
    private readonly object _gate = new();
    private readonly Dictionary<int, Pending> _pending = new();
    private Action<byte[]>? _wireSender;

    public PartyPathItemGate(
        Func<int, bool> isCarried,
        Func<int, int> selfCount,
        Func<int, string, Task<PartyInventoryProbe.PartyItemResult>> query,
        Func<int, string?> itemName,
        Func<int, bool> isEnabled,
        Func<bool> searchEnabled,
        Func<bool> inParty,
        Func<bool> selfIsLeader,
        Func<string?> selfGivenName,
        Action<IReadOnlyList<int>, int> forward,
        Action<Action> post,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(isCarried);
        ArgumentNullException.ThrowIfNull(selfCount);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(itemName);
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(searchEnabled);
        ArgumentNullException.ThrowIfNull(inParty);
        ArgumentNullException.ThrowIfNull(selfIsLeader);
        ArgumentNullException.ThrowIfNull(selfGivenName);
        ArgumentNullException.ThrowIfNull(forward);
        ArgumentNullException.ThrowIfNull(post);
        _isCarried = isCarried;
        _selfCount = selfCount;
        _query = query;
        _itemName = itemName;
        _isEnabled = isEnabled;
        _searchEnabled = searchEnabled;
        _inParty = inParty;
        _selfIsLeader = selfIsLeader;
        _selfGivenName = selfGivenName;
        _forward = forward;
        _post = post;
        _log = log;
    }

    // Bind the wire-sender for the give hand-off. MainWindowVM supplies the
    // engine-gated SendUserInput.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // True while the leader is still acquiring copies to make the party whole
    // AND search is a chosen acquisition option — keeps AutoSearchManager armed
    // past the first copy (the shared demand tracker resolves its need at copy
    // one). OR'd into auto-search's demand gate alongside
    // PathItemDemandTracker.SearchDemandActive.
    public bool SearchDemandActive
    {
        get
        {
            if (!_searchEnabled()) return false;
            lock (_gate) return _pending.Count > 0;
        }
    }

    // Walk-start callback (re-points AutoWalkManager.SetPathItemAnnouncer ahead
    // of the demand tracker): every item id gating an Item / Ticket exit along
    // the planned route. Solo forwards unchanged. In a party the list is
    // partitioned by the per-item provision-party flag: flagged items go through
    // the pool (the leader provisions the whole party, a follower borrows a
    // spare for any it personally lacks); unflagged items pass straight through
    // to the per-item demand pipeline as a self-only copy.
    public void OnPathItemsRequired(IReadOnlyList<int> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (!_inParty())
        {
            _forward(itemIds, 1);   // solo: one copy for self, unchanged behaviour
            return;
        }

        bool leader = _selfIsLeader();
        var considered = new HashSet<int>();
        List<int>? passthrough = null;   // items not flagged for party provisioning
        foreach (int id in itemIds)
        {
            if (id <= 0 || !considered.Add(id)) continue;
            if (!_isEnabled(id))
            {
                (passthrough ??= new()).Add(id);   // self-only demand, per-item routers handle it
                continue;
            }
            if (leader)
            {
                // The leader provisions even for an item it already carries —
                // a follower may still lack it, and only the leader coordinates
                // the hand-off. Skip only when one is already in flight.
                bool inFlight;
                lock (_gate) inFlight = _pending.ContainsKey(id);
                if (inFlight) continue;
                _post(() => _ = ProvisionAsync(id));
            }
            else
            {
                if (_isCarried(id)) continue;
                _post(() => _ = TryBorrowSpareAsync(id));
            }
        }
        if (passthrough is not null) _forward(passthrough, 1);
    }

    // Inventory-change callback (wired to InventoryManager.Changed): re-checks
    // each in-flight provisioning and coordinates the hand-off the moment the
    // pool becomes whole. A no-op when the leader isn't provisioning anything.
    public void OnInventoryChanged()
    {
        int[] ids;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            ids = new int[_pending.Count];
            _pending.Keys.CopyTo(ids, 0);
        }
        foreach (int id in ids) TryComplete(id);
    }

    private async Task ProvisionAsync(int id)
    {
        string? name = _itemName(id);
        if (string.IsNullOrWhiteSpace(name)) { _forward(new[] { id }, 1); return; }

        // Reserve the slot up-front so a re-announce mid-probe doesn't kick off
        // a second probe / double-give for the same item.
        lock (_gate)
        {
            if (!_pending.TryAdd(id, new Pending(id, name, NoCounts))) return;
        }

        PartyInventoryProbe.PartyItemResult result = await _query(id, name).ConfigureAwait(true);
        var others = new Dictionary<string, int>(result.CountsByMember, StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            if (!_pending.ContainsKey(id)) return;   // cleared while probing
            _pending[id] = new Pending(id, name, others);
        }

        if (TryComplete(id)) return;

        // Genuine shortfall: feed the demand pipeline (search / shop per the
        // user's checked options) with the count the leader must acquire so the
        // whole party clears the gate — the leader's own bag must hold at least
        // (partySize - othersTotal) so the pool reaches one-per-member. This is
        // the same wholeness check TryComplete uses, expressed as a target.
        // With search on, keep the slot so SearchDemandActive stays armed for
        // copies beyond the first and the redistribution fires once the pool is
        // whole; otherwise it's a one-shot best-effort and the slot is dropped.
        int othersTotal = 0;
        foreach (int c in others.Values) othersTotal += c;
        int target = Math.Max(1, (1 + others.Count) - othersTotal);
        if (!_searchEnabled())
            lock (_gate) _pending.Remove(id);
        _forward(new[] { id }, target);
    }

    // Redistributes if the pool (self + responders) now holds at least one per
    // member; returns true when the slot is settled (either handed out or
    // nothing to do), false while still short.
    private bool TryComplete(int id)
    {
        Pending? p;
        lock (_gate) p = _pending.TryGetValue(id, out Pending? found) ? found : null;
        if (p is null) return false;

        int selfNow = _selfCount(id);
        int othersTotal = 0;
        foreach (int c in p.Others.Values) othersTotal += c;
        int partySize = 1 + p.Others.Count;      // self + everyone who replied
        int totalHeld = selfNow + othersTotal;
        if (totalHeld < partySize) return false; // not enough yet — keep acquiring

        lock (_gate)
        {
            if (!_pending.Remove(id)) return true;   // another pass already handled it
        }
        Redistribute(p.Name, p.Others, selfNow, id);
        return true;
    }

    private void Redistribute(string name, IReadOnlyDictionary<string, int> others, int selfNow, int id)
    {
        if (_wireSender is null) { _forward(new[] { id }, 1); return; }

        // null giver / sink == self. Sources offer their surplus above one;
        // sinks are the zero-holders. Feasibility (totalHeld >= partySize) was
        // already checked, so surplus covers the sinks.
        var sources = new List<string?>();   // one entry per giveable copy
        var sinks = new List<string?>();
        if (selfNow == 0) sinks.Add(null);
        else for (int i = 1; i < selfNow; i++) sources.Add(null);
        foreach (KeyValuePair<string, int> kv in others)
        {
            if (kv.Value == 0) sinks.Add(kv.Key);
            else for (int i = 1; i < kv.Value; i++) sources.Add(kv.Key);
        }
        if (sinks.Count == 0) return;   // everyone already holds one

        string? self = _selfGivenName();
        bool selfInvolved = sinks.Contains(null) || sources.Contains(null);
        if (selfInvolved && string.IsNullOrEmpty(self)) { _forward(new[] { id }, 1); return; }

        int sent = 0;
        int si = 0;
        foreach (string? sink in sinks)
        {
            if (si >= sources.Count) break;   // guarded by feasibility, but stay safe
            string? giver = sources[si++];
            string recipient = sink ?? self!;
            if (giver is null)
                SendRaw($"give {name} to {recipient}");                    // hand over our own
            else
                SendRaw($"/{giver} @do give {name} to {recipient}");       // direct the holder
            sent++;
        }
        _log?.Info(LogCategory,
            $"party provisioning {name}: issued {sent} give(s) so each member holds one.");
    }

    private async Task TryBorrowSpareAsync(int id)
    {
        string? name = _itemName(id);
        if (string.IsNullOrWhiteSpace(name)) { _forward(new[] { id }, 1); return; }

        PartyInventoryProbe.PartyItemResult result = await _query(id, name).ConfigureAwait(true);

        // Might have arrived between the announce and the reply window (an
        // earlier give, a floor pickup) — nothing to do then.
        if (_isCarried(id)) return;

        string? holder = null;
        int holderCount = 0;
        int membersWithAny = 0;
        foreach (KeyValuePair<string, int> kv in result.CountsByMember)
        {
            if (kv.Value > 0) membersWithAny++;
            // Spare = 2+ (the holder keeps one for their own gate crossing).
            if (kv.Value >= 2 && kv.Value > holderCount)
            {
                holder = kv.Key;
                holderCount = kv.Value;
            }
        }

        // No member has a spare to give — post the need so demand-driven
        // search / shop take over.
        if (holder is null) { _forward(new[] { id }, 1); return; }

        string self = _selfGivenName() ?? string.Empty;
        if (self.Length == 0 || _wireSender is null) { _forward(new[] { id }, 1); return; }

        if (membersWithAny == 1)
            // Only the holder has any copies: @party give is permission-free
            // and only they can act on it, so there's no risk of over-giving.
            SendRaw($"@party give {name} to {self}");
        else
            // Several members hold copies: target the chosen holder so we
            // don't collect a duplicate from every one of them.
            SendRaw($"/{holder} @do give {name} to {self}");

        _log?.Info(LogCategory,
            $"party holds {name} (x{holderCount} on {holder}); requested a give — path-item search deferred.");
    }

    private void SendRaw(string command)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(command + "\r"));
    }
}
