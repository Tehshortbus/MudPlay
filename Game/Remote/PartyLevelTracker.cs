using System.Collections.Generic;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// Serves the party level-gate for path planning. Bounds() gives the party's
// most-constraining (Low, High) level window that MovementFilter reads at BFS
// time to route a following party around a (Level: MIN to MAX) gate the group
// can't clear — rather than stranding a member. WarmStaleLevels() fires a
// demand-driven @level re-probe when a planned route actually crosses a level
// gate and a member's recorded level is unknown or not from the current day.
//
// Keeping levels warm in the ordinary case — the once-a-day @level/@version
// refresh on partying with someone — lives in PartyProbeManager now; this type
// only READS the PlayerDatabase cache (Bounds) and tops it up on demand for a
// level-gated route (WarmStaleLevels). Both are leader-scoped: routing a
// following party around a gate is only meaningful while we lead.
//
// A member not yet probed contributes their title-derived band (see
// PartyLevelEstimate) so planning degrades gracefully until a reply lands
// instead of blocking on a round-trip. A recorded exact level counts as fresh
// for the current local day — the same "once per day" cadence the probe uses —
// so a route re-probe only fires when the reading is missing or from a prior
// day. Any @level reply (the auto-probe OR a manual /<player> @level) refreshes
// the cache through PartyLevelProbe, so a manual ask counts as today's reading.
//
// Invited members are never counted: an invited-not-joined row exists only for
// the PartyWindow chip and isn't yet someone we route the party around.
//
// Read-only on party state: never writes a party field.
public sealed class PartyLevelTracker
{
    private readonly PartyState _party;
    private readonly PartyLevelProbe _probe;
    private readonly PlayerDatabase _players;
    private readonly Func<int?> _selfLevel;
    private readonly Func<DateTime> _clock;
    private readonly LogService? _log;
    private DateTime _lastStaleProbeAt = DateTime.MinValue;

    // Debounces WarmStaleLevels so one route plan (many gate-exit evaluations)
    // fires at most one @level round-trip.
    public TimeSpan WarmDebounce { get; set; } = TimeSpan.FromSeconds(30);

    public PartyLevelTracker(
        PartyState party,
        PartyLevelProbe probe,
        PlayerDatabase players,
        Func<int?> selfLevel,
        LogService? log = null)
        : this(party, probe, players, selfLevel, clock: null, log: log) { }

    internal PartyLevelTracker(
        PartyState party,
        PartyLevelProbe probe,
        PlayerDatabase players,
        Func<int?> selfLevel,
        Func<DateTime>? clock,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(selfLevel);
        _party = party;
        _probe = probe;
        _players = players;
        _selfLevel = selfLevel;
        _clock = clock ?? (() => DateTime.UtcNow);
        _log = log;
    }

    // The party's most-constraining (Low, High) level window, or null when we're
    // not leading a party or nobody's level is known. Synchronous — reads only
    // the PlayerDatabase cache; each member contributes their exact level when
    // known (unless a risen title band overrides a now-stale exact), else their
    // title band.
    public (int Low, int High)? Bounds()
    {
        if (!_party.IsInParty || !_party.SelfIsLeader) return null;

        List<PartyLevelEstimate> estimates = new();
        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf) continue;
            // Skip pending invitees — an invited-not-joined row isn't yet a
            // party member we route around.
            if (m.IsInvited) continue;
            if (string.IsNullOrEmpty(m.Name)) continue;
            PlayerRecord? rec = _players.Find(m.Name);
            int? exact = rec?.Level;
            // Always carry the title band alongside the exact reading: the
            // estimate itself decides which wins (exact, unless the band's floor
            // has risen above a now-stale exact — see PartyLevelEstimate).
            (int Min, int Max)? title = _players.ClassTitles.LookupLevelRange(rec?.Title);
            estimates.Add(new PartyLevelEstimate(exact, title));
        }
        return PartyLevelBounds.Compute(_selfLevel(), estimates);
    }

    // The master switch (true = off): off, no @level round-trip is started.
    public Func<bool>? MasterSwitchOff { get; set; }

    // Route-scoped freshness poll: fire an @level round when a member we'd gate
    // on is unknown (no exact level yet) or their exact reading isn't from the
    // current local day. Called at walk-start via MovementFilter.WarmForRoute —
    // and only when the planned route actually crosses a level gate — so the
    // re-probe is demand-driven rather than firing on every hop. Debounced so one
    // plan fires at most one round-trip. Fire-and-forget: the reply refreshes the
    // cache for the next evaluation; the synchronous Bounds never blocks on it.
    public void WarmStaleLevels()
    {
        if (!_party.IsInParty || !_party.SelfIsLeader) return;
        if (MasterSwitchOff?.Invoke() == true) return;

        DateTime now = _clock();
        if (now - _lastStaleProbeAt < WarmDebounce) return;

        bool anyStale = false;
        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf || m.IsInvited || string.IsNullOrEmpty(m.Name)) continue;
            PlayerRecord? rec = _players.Find(m.Name);
            // Unknown (no exact reading) or last learned on a prior local day → due a probe.
            if (rec?.Level is null || rec.LevelAt is not { } at || !SameLocalDay(at, now))
            {
                anyStale = true;
                break;
            }
        }
        if (!anyStale) return;

        _lastStaleProbeAt = now;
        _log?.Info("PartyLevel", "Level gate on the planned route with a member's level unknown or not from today — probing @level.");
        _ = _probe.QueryAsync();   // fire-and-forget; the probe persists levels via RecordLevel
    }

    // Two UTC instants fall on the same local calendar day — the "fresh for
    // today" test, matching the once-per-local-day cadence of the party probe.
    private static bool SameLocalDay(DateTime aUtc, DateTime bUtc)
        => aUtc.ToLocalTime().Date == bUtc.ToLocalTime().Date;
}
