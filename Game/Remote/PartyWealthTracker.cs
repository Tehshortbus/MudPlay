using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

// Demand-driven party-wealth gate for path planning. Unlike the level tracker —
// which keeps every member's level warm on every roster change because level is
// stable — wealth drifts constantly with loot / spend, so it is NOT kept warm.
// It's polled only when a route actually needs it: Probe fires an @wealth round
// only when the walker/loop is about to walk a route that crosses a (Toll: N)
// exit. No toll on the planned path, no poll.
//
// The synchronous gate and the async probe are decoupled through the reading
// cache. Two distinct entry points:
//   MinWealth — a pure cache read. MovementFilter calls it per-exit while BFS
//     evaluates a toll, and it answers from the last poll's readings without
//     touching the wire. This is why it must NOT probe: BFS explores off-path
//     toll edges too (any toll edge inside the search frontier, in any
//     direction), so probing from here would fire an @wealth for a toll the
//     party will never actually walk through.
//   Probe — the route-scoped trigger. The walk-start sites
//     (AutoWalkManager / LoopRunner) call MovementFilter.WarmForRoute, which
//     probes ONLY when the tolls-permitted shortest route actually crosses a
//     toll. Debounced (via the post seam, since it touches the wire) so one
//     expansion of a multi-segment loop fires at most one round-trip.
//
// A follower with no fresh reading is NOT treated as broke — we don't block a
// toll on a wallet we simply haven't read. Instead the walk-start Probe fires an
// @wealth round for the missing readings and the gate folds in only the members
// we've confirmed, blocking the toll only when self or a KNOWN follower can't
// cover it. So a party that can afford the toll walks it on the first pass
// instead of detouring while the probe warms up; a follower confirmed short by a
// reply routes the party around on the next evaluation. (There's a one-pass
// window before the reply lands where an unread-but-broke follower could be led
// to the toll — accepted: tolls are rare, and the probe closes the window fast.)
//
// Self wealth is folded in from the same live snapshot MovementFilter's self-only
// branch reads; when our own wallet is unknown the gate stands down (returns
// null → self-only branch, which also won't refuse on an unknown wallet), matching
// the established "an unknown wallet never refuses a walk" rule for self.
public sealed class PartyWealthTracker
{
    private readonly PartyState _party;
    private readonly PartyWealthProbe _probe;
    private readonly Func<long?> _selfWealth;
    private readonly Action<Action> _post;
    private readonly Func<DateTime> _clock;
    private readonly LogService? _log;
    private readonly Dictionary<string, (long Copper, DateTime At)> _readings =
        new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastPollAt = DateTime.MinValue;

    // A reading older than this is treated as absent (gates the toll), and a poll
    // older than this re-fires on the next MinWealth. One window governs both:
    // wealth drifts, so a reading only stays trustworthy briefly, and the same
    // horizon debounces the demand-poll so a single BFS (many toll-exit probes)
    // fires at most one @wealth round-trip.
    public TimeSpan FreshnessWindow { get; set; } = TimeSpan.FromSeconds(30);

    public PartyWealthTracker(
        PartyState party,
        PartyWealthProbe probe,
        Func<long?> selfWealth,
        Action<Action>? post = null,
        LogService? log = null)
        : this(party, probe, selfWealth, post, clock: null, log: log) { }

    internal PartyWealthTracker(
        PartyState party,
        PartyWealthProbe probe,
        Func<long?> selfWealth,
        Action<Action>? post,
        Func<DateTime>? clock,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(selfWealth);
        _party = party;
        _probe = probe;
        _selfWealth = selfWealth;
        _post = post ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        _clock = clock ?? (() => DateTime.UtcNow);
        _log = log;
    }

    // Record one member's freshly-reported wealth (copper). Bound to the probe's
    // recordWealth seam, so each @wealth reply lands here timestamped.
    public void Record(string givenName, long copper)
    {
        if (string.IsNullOrEmpty(givenName)) return;
        _readings[GivenName(givenName)] = (copper, _clock());
    }

    // The party's minimum CONFIRMED on-hand wealth in copper, or null when the
    // party gate shouldn't apply (solo, not leading, or our own wallet unknown).
    // Pure cache read — MovementFilter calls this per toll exit during BFS, so it
    // must not touch the wire (BFS explores off-path toll edges the party never
    // walks; the route-scoped Probe handles the actual poll). A follower with no
    // fresh reading is skipped, not counted as broke: we fold in only self plus
    // the members we've confirmed, so the toll blocks only when someone KNOWN
    // can't cover it. The walk-start Probe fetches the missing readings so a
    // later pass gates on the confirmed figure.
    public long? MinWealth()
    {
        if (!_party.IsInParty || !_party.SelfIsLeader) return null;
        if (_selfWealth() is not { } self) return null;   // our own wallet unknown → stand down

        long min = self;
        DateTime now = _clock();
        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf || string.IsNullOrEmpty(m.Name)) continue;
            string given = GivenName(m.Name);
            if (_readings.TryGetValue(given, out (long Copper, DateTime At) r)
             && now - r.At <= FreshnessWindow)
                min = Math.Min(min, r.Copper);
            // else: unread / stale follower — don't gate on an unknown wallet;
            // the walk-start Probe warms it for a later confirmed evaluation.
        }
        return min;
    }

    // Fire an @wealth probe when the last one is older than the freshness window.
    // Fire-and-forget on the UI thread; the replies land via Record for the next
    // plan. Debounced so one route expansion (a multi-segment loop can hit
    // several toll legs) fires at most one round-trip. Called route-scoped from
    // MovementFilter.WarmForRoute — only when the tolls-permitted shortest route
    // actually crosses a toll — so nothing polls on an off-path toll edge.
    public void Probe()
    {
        DateTime now = _clock();
        if (now - _lastPollAt < FreshnessWindow) return;
        _lastPollAt = now;
        _log?.Info("PartyWealth", "Toll on the planned route — probing party @wealth.");
        _post(() => _ = _probe.QueryAsync());
    }

    private static string GivenName(string name)
    {
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }
}
