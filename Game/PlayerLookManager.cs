using System.Text;
using MudPlay.Game.Combat;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game;

// Reactive `look <player>` automation — two independent Settings → Talk toggles,
// both off by default:
//
//  • LookBackWhenLookedAt — when the wire shows "<name> is looking at you.", we
//    send `look <name>` back.
//
// BOTH TOGGLES ARE ONCE PER PLAYER PER LOCAL-CALENDAR DAY, on the same rule
// and the same clock GreetManager uses (PlayerObservation.LastLookedUtc via
// PlayerDatabase.GetLastLookedUtc / RecordLooked, local midnight to local
// midnight). A look tells us race, class and loadout, and none of those change
// often enough to be worth re-asking inside a day.
//
// THE LOOK-BACK USED TO HAVE NO DEDUP AT ALL, on the reasoning that "a look-at
// is a deliberate social poke and mirroring it each time is the point". That
// holds for a person; it does not hold for two clients. With the box ticked on
// both sides it is an unconditional mutual mirror and it never terminates:
//
//     A looks at B  ->  server tells B  ->  B looks back at A
//                   ->  server tells A  ->  A looks back at B  ->  ...
//
// Reported from a live pair looking at each other non-stop. The arrival toggle
// only lights the fuse; the look-back is the engine, so the throttle has to
// cover it too or the loop survives.
//
// STAMPED ON SEND, not on a parsed reply — a look at someone we cannot resolve
// (a shadowy figure, a truncated block) must still count, or the throttle would
// never engage for exactly the players it can learn nothing from.
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
    private readonly PlayerDatabase _players;
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

    private readonly Func<DateTime> _now;

    public PlayerLookManager(
        MessageRouter router,
        RoomEntryWatcher roomEntry,
        PlayerDatabase players,
        PartyState party,
        Func<string?> selfNameProvider,
        Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(roomEntry);
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(selfNameProvider);
        _roomEntry = roomEntry;
        _players = players;
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

    // The master switch (true = off): off, no look is sent, back at a looker or at
    // an arrival. Nothing is stamped, so the look is made at the next occasion.
    public Func<bool>? MasterSwitchOff { get; set; }

    private void OnLookedAt(MatchResult match)
    {
        if (!LookBackWhenLookedAt) return;
        if (MasterSwitchOff?.Invoke() == true) return;
        if (match.Groups.Count < 1) return;
        TryLookBack(match.Groups[0]);
    }

    // The look-back decision.
    internal void TryLookBack(string? name)
    {
        if (_wireSender is null) return;
        (string given, _) = PlayerObservation.SplitName(name);
        if (string.IsNullOrEmpty(given)) return;
        if (IsSelf(given)) return; // never look-back at ourselves
        if (LookedAlreadyToday(given)) return;
        Send(given);
    }

    private void OnArrival(RoomEntryArrivalEvent e)
    {
        if (!LookAtPlayersOnArrival) return;
        if (e.Kind != EntityKind.Player) return;
        if (MasterSwitchOff?.Invoke() == true) return;
        TryLookAtArrival(e.Name);
    }

    // The arrival look decision (the caller has already filtered to
    // EntityKind.Player).
    internal void TryLookAtArrival(string? name)
    {
        if (_wireSender is null) return;
        (string given, _) = PlayerObservation.SplitName(name);
        if (string.IsNullOrEmpty(given)) return;
        if (IsSelf(given)) return;
        if (IsPartyMember(given)) return;
        if (LookedAlreadyToday(given)) return;
        Send(given);
    }

    // Have we already auto-looked at them since local midnight? The day
    // boundary is LOCAL, matching GreetManager, so "once a day" means what a
    // person sitting at the client would take it to mean.
    private bool LookedAlreadyToday(string given)
    {
        DateTime? last = _players.GetLastLookedUtc(given);
        return last is { } when
            && when.ToLocalTime().Date == _now().ToLocalTime().Date;
    }

    // Write the look and stamp it in one place, so no caller can send without
    // recording and quietly re-open the loop.
    private void Send(string given)
    {
        _wireSender!(Encoding.Latin1.GetBytes($"look {given}\r"));
        _players.RecordLooked(given, _now());
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
