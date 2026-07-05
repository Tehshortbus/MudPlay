using System.Collections.Specialized;
using System.ComponentModel;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

// Leader-side roster hygiene for a party member who dies mid-farm. When we're
// leading an automated route (loop / walk-to / Auto-Lair) and a member is
// killed, MajorMUD doesn't drop them cleanly — the dead character lingers in
// our `par` as an [Invited] (pending) slot, indistinguishable from a genuine
// recruit we're still waiting on. Left alone, the auto-party engine treats that
// corpse as an invitee and holds the loop (PartyInviteGate) until its whole
// wait-window elapses. Because we KNOW this "invitee" is actually a member who
// just died, we can uninvite them promptly instead of stalling the route.
//
// Why the death line alone isn't enough. "<Name> has died." is the universal
// third-person death line every observer sees — it fires for randoms and (with
// variant wording) mobs too, not just our party. So every action here is doubly
// bounded: we only ever act on a name that was an ACTIVE member of OUR party at
// the moment it died (the active→invited transition the user described), and we
// only actually send `uninvite` once that same name is showing as an [Invited]
// par slot. A still-pending recruit was invited from the start (never active),
// so it's never recorded; a same-named mob is never in our roster; neither can
// trigger a real uninvite.
//
// Timing. We defer the uninvite until the room is clear of combat (the Combat
// gate releases) so we finish the current fight before touching party state,
// then let the loop continue on its own. par polls throughout the fight, so the
// dead member has usually flipped to [Invited] by the time combat clears; a
// per-row IsInvited watch backs up the rare case where that flip lands after.
//
// Scope. Gated on "a movement engine is actually running" — during hands-on
// play the user manages party state themselves, so we don't auto-uninvite. Only
// the leader can uninvite anyway, so a follower observing the death is a no-op.
//
// Read-only on party state: it reads PartyState and calls the public
// PartyManager.Uninvite (which routes the removal through the server echo), so
// PartyManager stays the sole writer of every roster field.
public sealed class PartyDeathRosterCleanup : IDisposable
{
    // Surfaced in the log timeline when the cleanup fires an uninvite.
    public const string LogCategory = "Party";

    private readonly PartyState _party;
    private readonly PartyManager _manager;
    private readonly MovementCoordinator _coordinator;
    private readonly Func<bool> _isMovementActive;
    private readonly LogService? _log;
    private readonly List<IDisposable> _subs = new();

    // given-name → time we observed the death. Bounded by CleanupWindow so a
    // recorded death that never materialises as an [Invited] slot (member was
    // instead fully removed, or the death-line wording is off) is dropped rather
    // than lingering forever.
    private readonly Dictionary<string, DateTimeOffset> _pendingDeaths =
        new(StringComparer.OrdinalIgnoreCase);

    // Give up on a recorded death if it hasn't surfaced as an [Invited] slot
    // within this window. Generous — a par poll is ~5 s and combat can run long.
    public TimeSpan CleanupWindow { get; set; } = TimeSpan.FromMinutes(2);

    // Test seam — overrides the wall clock for record/expiry timing.
    public Func<DateTimeOffset> NowProvider { get; set; } = () => DateTimeOffset.Now;

    private bool _disposed;

    public PartyDeathRosterCleanup(
        MessageRouter router,
        PartyState party,
        PartyManager manager,
        MovementCoordinator coordinator,
        Func<bool> isMovementActive,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(isMovementActive);
        _party = party;
        _manager = manager;
        _coordinator = coordinator;
        _isMovementActive = isMovementActive;
        _log = log;

        _subs.Add(router.Subscribe(KnownPatterns.PartyMemberDied, OnMemberDied));
        _coordinator.GatesChanged += OnGatesChanged;
        _party.Members.CollectionChanged += OnMembersChanged;
        foreach (PartyMember m in _party.Members)
            m.PropertyChanged += OnMemberPropertyChanged;
    }

    private void OnMemberDied(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        string name = result.Groups[0];
        if (string.IsNullOrEmpty(name)) return;

        // Only the leader can uninvite, and only during an automated route —
        // hands-on play leaves party state to the user.
        if (!_party.SelfIsLeader) return;
        if (!_isMovementActive()) return;

        string given = GivenNameOf(name);
        // Record only the active→invited transition: the name must be an ACTIVE
        // member of ours right now (not self, not an already-pending invite). A
        // rando/mob "has died." isn't in our roster; a still-pending recruit is
        // IsInvited already — both are correctly ignored here.
        if (!IsActiveMember(given)) return;

        _pendingDeaths[given] = NowProvider();
        _log?.Info(LogCategory,
            $"{given} died mid-route — will uninvite the corpse's pending slot once the room clears.");
        TryCleanup();
    }

    private void OnGatesChanged() => TryCleanup();

    private void OnMembersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (object? item in e.OldItems)
                if (item is PartyMember m) m.PropertyChanged -= OnMemberPropertyChanged;
        if (e.NewItems is not null)
            foreach (object? item in e.NewItems)
                if (item is PartyMember m) m.PropertyChanged += OnMemberPropertyChanged;
        TryCleanup();
    }

    // A dead member usually flips active→[Invited] during the fight; this backs
    // up the case where par delivers that flip after combat has already cleared.
    private void OnMemberPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PartyMember.IsInvited)) TryCleanup();
    }

    private void TryCleanup()
    {
        if (_pendingDeaths.Count == 0) return;
        // Hold until the room is clear of combat, then let the route continue.
        if (_coordinator.AssertedGates.Contains(MovementCoordinator.CombatGate)) return;

        DateTimeOffset now = NowProvider();
        foreach (string given in _pendingDeaths.Keys.ToArray())
        {
            if (now - _pendingDeaths[given] > CleanupWindow)
            {
                _pendingDeaths.Remove(given);
                continue;
            }
            if (!ShowsInvited(given)) continue;
            _manager.Uninvite(given);
            _pendingDeaths.Remove(given);
            _log?.Info(LogCategory,
                $"Uninvited {given} — dead member's pending par slot cleared; route continues.");
        }
    }

    private bool IsActiveMember(string given)
    {
        foreach (PartyMember m in _party.Members)
            if (!m.IsSelf && !m.IsInvited
                && GivenNameOf(m.Name).Equals(given, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private bool ShowsInvited(string given)
    {
        foreach (PartyMember m in _party.Members)
            if (m.IsInvited
                && GivenNameOf(m.Name).Equals(given, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // First whitespace-delimited token. "Raijin WuzHere" → "Raijin". MajorMUD
    // addresses players by given name only and chat/par disagree on the form,
    // so roster matching is always by given name (mirrors PartyManager/PartyPoller).
    private static string GivenNameOf(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (IDisposable sub in _subs) sub.Dispose();
        _subs.Clear();
        _coordinator.GatesChanged -= OnGatesChanged;
        _party.Members.CollectionChanged -= OnMembersChanged;
        foreach (PartyMember m in _party.Members)
            m.PropertyChanged -= OnMemberPropertyChanged;
    }
}
