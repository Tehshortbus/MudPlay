using System.Collections.Generic;
using System.Linq;

namespace FujinTerm.Services;

// The kinds of acquisition need an engine can post to the NeedsRegistry.
// Deliberately minimal. New kinds are added when an engine actually needs
// one; no speculative bloat.
public enum NeedKind
{
    // The character needs an illumination source (torch / lantern / +illu
    // gear / light spell) to see a dark room. Posted by auto-light, fulfilled
    // opportunistically by auto-get.
    LightSource,

    // The character needs a specific item to traverse a planned route — an
    // (Item: N) / (Ticket: N) exit gate the walker will hit but we don't
    // carry the required item. The Need.Descriptor is the item id (decimal).
    // Posted by Game.Map.PathItemDemandTracker at walk-start; resolved when
    // the item enters inventory. Its consumers include the Settings → Other
    // "search rooms if item needed" affordance (arms auto-search while any
    // such need is outstanding) and active fulfillers (shop routing,
    // monster-drop reroute, party obtain) that claim the same needs.
    PathItem,
}

// One outstanding acquisition need. Immutable value identified by (Kind,
// Descriptor) — the registry treats two needs with the same kind + descriptor
// as the same request and dedupes them on NeedsRegistry.Post.
//   Kind       — what category of thing is needed.
//   Descriptor — a free-form specifier the fulfiller reads to know what to
//     get — e.g. the required illu gap ("illu>=42") for a LightSource. The
//     requester and fulfiller agree on the format; the registry treats it as
//     an opaque identity key.
//   Requester  — identifier of the engine that posted the need (e.g.
//     "AutoLightManager"). Diagnostic only.
//   PostedAt   — wall-clock time the need was first posted.
//   Quantity   — how many copies the local character should acquire to
//     satisfy this need — 1 solo, the party shortfall when the leader is
//     provisioning everyone across a gate. Fulfillers read it to acquire the
//     full count (e.g. buy N), and the requester resolves the need on
//     count-met rather than the first copy. Not part of registry identity
//     (dedup keys on kind + descriptor), so it is fixed at first post.
public readonly record struct Need(
    NeedKind Kind,
    string Descriptor,
    string Requester,
    DateTimeOffset PostedAt,
    int Quantity = 1);

// The fulfillment half of the auto-engine coordination model (the gating half
// is Game.Map.MovementCoordinator). No engine references another by type: a
// requester Posts a Need, and a fulfilling engine claims it (TryClaim),
// satisfies it, then resolves it (Resolve). The requester never knows who
// fulfils. Auto-light posts a LightSource need; auto-get fulfils by grabbing
// a torch/lantern en route.
//
// Thread affinity: posts come from MessageRouter handlers and claims come
// from engine ticks — both on the UI thread. The internal list is
// nonetheless guarded by a lock (cheap insurance, matching LogService /
// Game.Map.MovementCoordinator). The NeedPosted event fires on the posting
// thread after the lock is released.
public sealed class NeedsRegistry
{
    private sealed class Slot
    {
        public required Need Need { get; init; }
        public string? Claimant { get; set; }
    }

    private readonly object _gate = new();
    private readonly List<Slot> _slots = new();
    private readonly LogService? _log;

    // Fires after a genuinely new need is posted (not on a deduped re-post).
    // Lets a fulfilling engine wake and attempt a TryClaim immediately instead
    // of polling. Fired on the posting thread after the internal lock is
    // released.
    public event Action<Need>? NeedPosted;

    public NeedsRegistry(LogService? log = null)
    {
        _log = log;
    }

    // Post a need for quantity copies (clamped to at least one). Deduped on
    // (kind, descriptor): re-posting an already-outstanding identical need is
    // a no-op (no duplicate slot, no event) and returns the existing need — so
    // the quantity is fixed by the first post and a re-announce with a
    // different count doesn't disturb an acquisition already in flight.
    // Returns the (new or existing) need so the requester can hold the handle.
    public Need Post(NeedKind kind, string descriptor, string requester, int quantity = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(requester);

        Need need;
        bool isNew;
        lock (_gate)
        {
            Slot? existing = Find(kind, descriptor);
            if (existing is not null)
            {
                isNew = false;
                need = existing.Need;
            }
            else
            {
                isNew = true;
                need = new Need(kind, descriptor, requester, DateTimeOffset.Now, Math.Max(1, quantity));
                _slots.Add(new Slot { Need = need });
            }
        }

        if (isNew)
        {
            _log?.Info("Needs", $"posted — kind={kind} descriptor={descriptor} qty={need.Quantity} by={requester}");
            NeedPosted?.Invoke(need);
        }
        return need;
    }

    // Try to claim the oldest unclaimed need of kind for claimant. A claimed
    // need stays in the registry (still outstanding) but won't be handed to a
    // second claimant — only Resolve or Release changes that. Returns false
    // with need defaulted when nothing is claimable.
    public bool TryClaim(NeedKind kind, string claimant, out Need need)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimant);
        lock (_gate)
        {
            Slot? slot = _slots.FirstOrDefault(s => s.Need.Kind == kind && s.Claimant is null);
            if (slot is null)
            {
                need = default;
                return false;
            }
            slot.Claimant = claimant;
            need = slot.Need;
        }
        _log?.Info("Needs", $"claimed — kind={need.Kind} descriptor={need.Descriptor} by={claimant}");
        return true;
    }

    // Remove a need from the registry — the fulfiller got the thing. Identity
    // is (Need.Kind, Need.Descriptor). Returns true if a matching need was
    // removed.
    public bool Resolve(Need need)
    {
        bool removed;
        lock (_gate)
        {
            Slot? slot = Find(need.Kind, need.Descriptor);
            removed = slot is not null && _slots.Remove(slot);
        }
        if (removed) _log?.Info("Needs", $"resolved — kind={need.Kind} descriptor={need.Descriptor}");
        return removed;
    }

    // Drop a claim without resolving — the fulfiller gave up (e.g. the only
    // source is a monster drop we couldn't grab this trip). The need returns
    // to the unclaimed pool so another fulfiller (or the same one on a later
    // tick) can retry. Returns true if a matching claimed need was released.
    public bool Release(Need need)
    {
        bool released = false;
        lock (_gate)
        {
            Slot? slot = Find(need.Kind, need.Descriptor);
            if (slot is { Claimant: not null })
            {
                slot.Claimant = null;
                released = true;
            }
        }
        if (released) _log?.Info("Needs", $"released — kind={need.Kind} descriptor={need.Descriptor}");
        return released;
    }

    // Snapshot of every outstanding need of kind (claimed or not), oldest
    // first. Lets a requester check whether its need is still pending without
    // re-posting.
    public IReadOnlyList<Need> Outstanding(NeedKind kind)
    {
        lock (_gate)
        {
            return _slots.Where(s => s.Need.Kind == kind).Select(s => s.Need).ToArray();
        }
    }

    // Drop every outstanding need. Called on character swap so one character's
    // pending acquisitions don't leak into the next.
    public void Clear()
    {
        lock (_gate) { _slots.Clear(); }
    }

    private Slot? Find(NeedKind kind, string descriptor)
        => _slots.FirstOrDefault(
            s => s.Need.Kind == kind &&
                 string.Equals(s.Need.Descriptor, descriptor, StringComparison.Ordinal));
}
