using System.Text;
using FujinTerm.Game.Combat;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

// Reactive `look <player>` automation — two independent Settings → Talk toggles,
// both off by default:
//
//  • LookBackWhenLookedAt — when the wire shows "<name> is looking at you.", we
//    send `look <name>` back. One look per line; no dedup — a look-at is a
//    deliberate social poke and mirroring it each time is the point.
//
//  • LookAtPlayersOnArrival — when a non-party player walks into our room, we
//    send `look <name>` to learn/refresh their kit. Driven off
//    RoomEntryWatcher.ArrivalObserved (fires per arrival with the entity already
//    classified Player/Monster), so monsters and "Also here:" re-displays don't
//    trip it. Once per arrival — the watcher fires once per walk-in.
//
// Both address players by GIVEN name (MajorMUD targets `look` by the first name
// token) and skip our own character. The arrival path also skips current party
// members; the look-back path mirrors whoever looked, party or not.
//
// Off by default — each flag is pushed in from its Settings → Talk checkbox. The
// wire-sender is bound by MainWindowViewModel after telnet connects; before that
// the manager observes but can't send.
public sealed class PlayerLookManager : IDisposable
{
    private readonly RoomEntryWatcher _roomEntry;
    private readonly PartyState _party;
    private readonly Func<string?> _selfNameProvider;
    private readonly IDisposable _lookedAtSub;
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    // Settings → Talk "Look back when a player looks at us". Default off.
    public bool LookBackWhenLookedAt { get; set; }

    // Settings → Talk "Look at players to learn/update their inventories".
    // Default off.
    public bool LookAtPlayersOnArrival { get; set; }

    public PlayerLookManager(
        MessageRouter router,
        RoomEntryWatcher roomEntry,
        PartyState party,
        Func<string?> selfNameProvider)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(roomEntry);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(selfNameProvider);
        _roomEntry = roomEntry;
        _party = party;
        _selfNameProvider = selfNameProvider;
        _lookedAtSub = router.Subscribe(KnownPatterns.PlayerLooksAtYou, OnLookedAt);
        _roomEntry.ArrivalObserved += OnArrival;
    }

    // Bind the wire-sender — same shape as every other engine. Without it the
    // manager observes but sends nothing.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lookedAtSub.Dispose();
        _roomEntry.ArrivalObserved -= OnArrival;
    }

    private void OnLookedAt(MatchResult match)
    {
        if (!LookBackWhenLookedAt) return;
        if (match.Groups.Count < 1) return;
        TryLookBack(match.Groups[0]);
    }

    // Test seam — runs the look-back decision directly.
    internal void TryLookBack(string? name)
    {
        if (_wireSender is null) return;
        (string given, _) = PlayerObservation.SplitName(name);
        if (string.IsNullOrEmpty(given)) return;
        if (IsSelf(given)) return; // never look-back at ourselves
        _wireSender(Encoding.Latin1.GetBytes($"look {given}\r"));
    }

    private void OnArrival(RoomEntryArrivalEvent e)
    {
        if (!LookAtPlayersOnArrival) return;
        if (e.Kind != EntityKind.Player) return;
        TryLookAtArrival(e.Name);
    }

    // Test seam — runs the arrival look decision directly (caller has already
    // filtered to EntityKind.Player).
    internal void TryLookAtArrival(string? name)
    {
        if (_wireSender is null) return;
        (string given, _) = PlayerObservation.SplitName(name);
        if (string.IsNullOrEmpty(given)) return;
        if (IsSelf(given)) return;
        if (IsPartyMember(given)) return;
        _wireSender(Encoding.Latin1.GetBytes($"look {given}\r"));
    }

    private bool IsSelf(string given)
    {
        (string selfGiven, _) = PlayerObservation.SplitName(_selfNameProvider());
        return !string.IsNullOrEmpty(selfGiven)
            && selfGiven.Equals(given, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsPartyMember(string given)
    {
        foreach (PartyMember m in _party.Members)
        {
            (string memberGiven, _) = PlayerObservation.SplitName(m.Name);
            if (memberGiven.Equals(given, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
