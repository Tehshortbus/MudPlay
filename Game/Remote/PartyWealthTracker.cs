using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

// Demand-driven party-wealth gate for path planning. Unlike the level tracker —
// which keeps every member's level warm on every roster change because level is
// stable — wealth drifts constantly with loot / spend, so it is NOT kept warm.
// It's polled only when a route actually needs it: MinWealth is invoked by
// MovementFilter ONLY while BFS is evaluating a (Toll: N) exit, so a toll on a
// candidate path is the sole thing that fires an @wealth probe. No toll in play,
// no poll.
//
// The synchronous gate and the async probe are decoupled through the reading
// cache: MinWealth kicks a debounced background probe (via the post seam, since
// BFS may run off the UI thread and the broadcast touches the wire) and answers
// from the last poll's readings. A follower with no fresh reading — never polled,
// answered "wealth unknown", or a reading gone stale — gates the toll (confirmed
// mechanic: a member who can't cover it OR doesn't reply → avoid that toll room).
// So the first plan across a toll routes around it while the probe warms up; a
// loop's next lap, or a walk-to retry, then uses the fresh readings. Being
// conservative until confirmed affordable is the safe side — it never strands a
// follower at a gate they can't pay.
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

    // The party's minimum on-hand wealth in copper, or null when the party gate
    // shouldn't apply (solo, not leading, or our own wallet unknown).
    // MovementFilter calls this only for a (Toll: N) exit, so it doubles as the
    // demand signal to (re)poll @wealth. A follower with no fresh reading returns
    // 0 — gating any positive toll — per the confirmed avoid-on-unknown rule.
    public long? MinWealth()
    {
        if (!_party.IsInParty || !_party.SelfIsLeader) return null;
        if (_selfWealth() is not { } self) return null;   // our own wallet unknown → stand down

        MaybeRefresh();

        long min = self;
        DateTime now = _clock();
        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf || string.IsNullOrEmpty(m.Name)) continue;
            string given = GivenName(m.Name);
            if (_readings.TryGetValue(given, out (long Copper, DateTime At) r)
             && now - r.At <= FreshnessWindow)
                min = Math.Min(min, r.Copper);
            else
                return 0;   // missing / stale follower → avoid the toll this pass
        }
        return min;
    }

    // Fire an @wealth probe when the last one is older than the freshness window.
    // Fire-and-forget on the UI thread; the replies land via Record for the next
    // plan. Debounced so one BFS pass doesn't spam the party with probes.
    private void MaybeRefresh()
    {
        DateTime now = _clock();
        if (now - _lastPollAt < FreshnessWindow) return;
        _lastPollAt = now;
        _log?.Info("PartyWealth", "Toll on a candidate route — probing party @wealth.");
        _post(() => _ = _probe.QueryAsync());
    }

    private static string GivenName(string name)
    {
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }
}
