using System.Collections.Generic;
using System.Linq;

namespace FujinTerm.Services;

/// <summary>
/// The kinds of acquisition need an engine can post to the
/// <see cref="NeedsRegistry"/>. Deliberately minimal — only
/// <see cref="LightSource"/> has a consumer today (Phase 9 PR 9.K
/// auto-light). New kinds are added when an engine actually needs one;
/// no speculative bloat.
/// </summary>
public enum NeedKind
{
    /// <summary>The character needs an illumination source (torch /
    /// lantern / +illu gear / light spell) to see a dark room. Posted
    /// by auto-light, fulfilled opportunistically by auto-get.</summary>
    LightSource,

    /// <summary>The character needs a specific item to traverse a
    /// planned route — an <c>(Item: N)</c> / <c>(Ticket: N)</c> exit
    /// gate the walker will hit but we don't carry the required item.
    /// The <see cref="Need.Descriptor"/> is the item id (decimal). Posted
    /// by <see cref="Game.Map.PathItemDemandTracker"/> at walk-start;
    /// resolved when the item enters inventory. Its consumer today is the
    /// Settings → Other "search rooms if item needed" affordance, which
    /// arms auto-search while any such need is outstanding; later PRs add
    /// active fulfillers (shop routing, monster-drop reroute, party
    /// obtain) that claim the same needs.</summary>
    PathItem,
}

/// <summary>
/// One outstanding acquisition need. Immutable value identified by
/// (<see cref="Kind"/>, <see cref="Descriptor"/>) — the registry treats
/// two needs with the same kind + descriptor as the same request and
/// dedupes them on <see cref="NeedsRegistry.Post"/>.
/// </summary>
/// <param name="Kind">What category of thing is needed.</param>
/// <param name="Descriptor">A free-form specifier the fulfiller reads to
/// know what to get — e.g. the required illu gap (<c>"illu>=42"</c>) for
/// a <see cref="NeedKind.LightSource"/>. The requester and fulfiller
/// agree on the format; the registry treats it as an opaque identity
/// key.</param>
/// <param name="Requester">Identifier of the engine that posted the need
/// (e.g. <c>"AutoLightManager"</c>). Diagnostic only.</param>
/// <param name="PostedAt">Wall-clock time the need was first posted.</param>
/// <param name="Quantity">How many copies the local character should
/// acquire to satisfy this need — 1 solo, the party shortfall when the
/// leader is provisioning everyone across a gate. Fulfillers read it to
/// acquire the full count (e.g. <c>buy</c> N), and the requester resolves
/// the need on count-met rather than the first copy. Not part of registry
/// identity (dedup keys on kind + descriptor), so it is fixed at first
/// post.</param>
public readonly record struct Need(
    NeedKind Kind,
    string Descriptor,
    string Requester,
    DateTimeOffset PostedAt,
    int Quantity = 1);

/// <summary>
/// The fulfillment half of the Phase 9 auto-engine coordination model
/// (the gating half is <see cref="Game.Map.MovementCoordinator"/>). No
/// engine references another by type: a requester <see cref="Post"/>s a
/// <see cref="Need"/>, and a fulfilling engine
/// (<see cref="TryClaim">claims</see> it, satisfies it, then
/// <see cref="Resolve">resolves</see> it. The requester never knows who
/// fulfils.
/// </summary>
/// <remarks>
/// <para>
/// See <c>docs/auto-engine-orchestration.md § Coordination principle</c>.
/// Auto-light posts a <see cref="NeedKind.LightSource"/> need; auto-get
/// fulfils by grabbing a torch/lantern en route.
/// </para>
/// <para>
/// Thread affinity: posts come from <c>MessageRouter</c> handlers and
/// claims come from engine ticks — both on the UI thread. The internal
/// list is nonetheless guarded by a lock (cheap insurance, matching
/// <see cref="LogService"/> / <see cref="Game.Map.MovementCoordinator"/>).
/// The <see cref="NeedPosted"/> event fires on the posting thread after
/// the lock is released.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Fires after a genuinely new need is posted (not on a deduped
    /// re-post). Lets a fulfilling engine wake and attempt a
    /// <see cref="TryClaim"/> immediately instead of polling. Fired on
    /// the posting thread after the internal lock is released.
    /// </summary>
    public event Action<Need>? NeedPosted;

    public NeedsRegistry(LogService? log = null)
    {
        _log = log;
    }

    /// <summary>
    /// Post a need for <paramref name="quantity"/> copies (clamped to at
    /// least one). Deduped on (<paramref name="kind"/>,
    /// <paramref name="descriptor"/>): re-posting an already-outstanding
    /// identical need is a no-op (no duplicate slot, no event) and
    /// returns the existing need — so the quantity is fixed by the first
    /// post and a re-announce with a different count doesn't disturb an
    /// acquisition already in flight. Returns the (new or existing) need
    /// so the requester can hold the handle.
    /// </summary>
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

    /// <summary>
    /// Try to claim the oldest unclaimed need of <paramref name="kind"/>
    /// for <paramref name="claimant"/>. A claimed need stays in the
    /// registry (still <see cref="Outstanding">outstanding</see>) but
    /// won't be handed to a second claimant — only <see cref="Resolve"/>
    /// or <see cref="Release"/> changes that. Returns <c>false</c> with
    /// <paramref name="need"/> defaulted when nothing is claimable.
    /// </summary>
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

    /// <summary>
    /// Remove a need from the registry — the fulfiller got the thing.
    /// Identity is (<see cref="Need.Kind"/>, <see cref="Need.Descriptor"/>).
    /// Returns <c>true</c> if a matching need was removed.
    /// </summary>
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

    /// <summary>
    /// Drop a claim without resolving — the fulfiller gave up (e.g. the
    /// only source is a monster drop we couldn't grab this trip). The
    /// need returns to the unclaimed pool so another fulfiller (or the
    /// same one on a later tick) can retry. Returns <c>true</c> if a
    /// matching claimed need was released.
    /// </summary>
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

    /// <summary>
    /// Snapshot of every outstanding need of <paramref name="kind"/>
    /// (claimed or not), oldest first. Lets a requester check whether
    /// its need is still pending without re-posting.
    /// </summary>
    public IReadOnlyList<Need> Outstanding(NeedKind kind)
    {
        lock (_gate)
        {
            return _slots.Where(s => s.Need.Kind == kind).Select(s => s.Need).ToArray();
        }
    }

    /// <summary>
    /// Drop every outstanding need. Called on character swap so one
    /// character's pending acquisitions don't leak into the next.
    /// </summary>
    public void Clear()
    {
        lock (_gate) { _slots.Clear(); }
    }

    private Slot? Find(NeedKind kind, string descriptor)
        => _slots.FirstOrDefault(
            s => s.Need.Kind == kind &&
                 string.Equals(s.Need.Descriptor, descriptor, StringComparison.Ordinal));
}
