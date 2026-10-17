using System.Collections.Generic;
using System.Linq;

namespace MudPlay.Game.Map;

// Deterministic Auto-Lair target picker. Given a set of marked lairs
// (with their respawn timers + last-arrival timestamps), the current
// room, and a travel-cost model, returns the lair the player should
// approach next.
//
// Why deterministic: the user has told us which lairs they want and how
// often each respawns, so a uniform-random pick over the marked set
// wastes that information. This scheduler picks the lair whose entry
// timing best fits the chosen heuristic.
//
// A lair repopulates when the player enters it after its clock has run
// out. The scheduler treats LairCandidate.ReadyAt as "earliest time the
// room repopulates when re-entered" — LairTimerStore.ClockStart (the
// room's last kill on Stock, the entry on Paradigm) + RespawnSeconds.
// Lairs with no clock start this session report ReadyAt = null and are
// treated as ready immediately.
//
// Scoring: per candidate, the scheduler computes slack = entryArrival -
// readyAt.
//   slack > 0 ⇒ slack seconds of wasted respawn (the mob has been up
//               that long when we step in).
//   slack < 0 ⇒ |slack| seconds of idle wait in the wait-room before
//               stepping in.
// Default heuristic: the closest lair with slack >= 0 (up by arrival), else
// the one whose fight could start soonest. Throughput heuristic minimises
// max(0, slack) only (idle time is free).
//
// Wait-room contract: the caller is responsible for picking the
// wait-room (the BFS-shortest neighbour of the lair that isn't itself a
// marked lair, and isn't avoided). The scheduler scores against the
// wait-room hop count, not the lair's own hop count, because the player
// must NOT enter the lair early — the respawn check only fires on entry.
// The single entry hop is added in via ITravelCostModel.EntryHopDuration.
public static class AutoLairScheduler
{
    // Pick the next lair to approach. Returns null when no candidate is
    // reachable (e.g. every marker is unwalkable from here, or the list
    // is empty).
    //
    // candidates: one entry per marked lair, with the pre-computed
    //   wait-room + approach hop count + ready-at timestamp. Entries with
    //   WaitRoom = null or ApproachHops = null are skipped (unreachable /
    //   no eligible wait-room).
    // travel: converts hop counts to durations.
    // heuristic: Default = closest ready-by-arrival; Throughput = wasted-only.
    // now: current wall-clock instant.
    public static LairDecision? PickNext(
        IReadOnlyList<LairCandidate> candidates,
        ITravelCostModel travel,
        AutoLairHeuristic heuristic = AutoLairHeuristic.Default,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(travel);
        if (candidates.Count == 0) return null;

        DateTimeOffset evalAt = now ?? DateTimeOffset.UtcNow;
        // Default two-tier: prefer a lair that's up by the time we ARRIVE — among
        // those the CLOSEST (soonest we start fighting = most hits per run) — and only
        // if none is ready by arrival, fall back to whichever we could start fighting
        // soonest (the least-bad wait). Never idle for a nearer lair unless it'd
        // actually be up when we get there.
        LairDecision? bestReady = null;     // ready by arrival — ranked by arrival time
        TimeSpan bestReadySlack = TimeSpan.MaxValue;   // tie-break: freshest among equidistant
        LairDecision? bestSoonest = null;   // fallback: soonest fight-start over all
        LairDecision? bestThroughput = null; // Throughput heuristic: min wasted-respawn

        foreach (LairCandidate c in candidates)
        {
            if (c.WaitRoom is null || c.ApproachHops is not int hops) continue;

            TimeSpan approach = travel.EstimateTravel(hops);
            TimeSpan entryHop = travel.EntryHopDuration;
            DateTimeOffset entryArrival = evalAt + approach + entryHop;

            DateTimeOffset readyAt = c.ReadyAt ?? evalAt; // null = ready now
            TimeSpan slack = entryArrival - readyAt;
            double wastedSeconds = Math.Max(0, slack.TotalSeconds);

            // When we could actually start fighting this lair: on arrival if it's
            // already up (slack ≥ 0), else when it respawns (we'd idle until then).
            DateTimeOffset fightStart = entryArrival > readyAt ? entryArrival : readyAt;
            double soonestScore = (fightStart - evalAt).TotalSeconds;

            LairDecision Decision(double score) => new(
                Lair: c.Lair, WaitRoom: c.WaitRoom.Value, ApproachDuration: approach,
                EntryArrival: entryArrival, SlackAtEntry: slack, Score: score);

            // Ready by the time we arrive → rank by arrival time so the CLOSEST wins
            // (a closer on-cooldown lair that pops during the walk lands here too, and
            // beats a farther already-up one).
            if (slack >= TimeSpan.Zero)
            {
                double arrivalScore = (entryArrival - evalAt).TotalSeconds;
                // Closest first; among equidistant ready lairs prefer the freshest
                // (smallest slack — least respawn already wasted) so the pick is
                // deterministic and independent of candidate order.
                if (bestReady is null
                    || arrivalScore < bestReady.Score
                    || (arrivalScore == bestReady.Score && slack < bestReadySlack))
                {
                    bestReady = Decision(arrivalScore);
                    bestReadySlack = slack;
                }
            }

            if (bestSoonest is null || soonestScore < bestSoonest.Score) bestSoonest = Decision(soonestScore);

            if (heuristic == AutoLairHeuristic.Throughput
                && (bestThroughput is null || wastedSeconds < bestThroughput.Score))
                bestThroughput = Decision(wastedSeconds);
        }

        return heuristic switch
        {
            // Throughput keeps its own semantics: minimise wasted respawn, idle is free.
            AutoLairHeuristic.Throughput => bestThroughput,
            // Default: closest ready-by-arrival lair, else the soonest we can fight.
            _                            => bestReady ?? bestSoonest,
        };
    }
}

// Scoring heuristic for AutoLairScheduler.PickNext.
public enum AutoLairHeuristic
{
    // Maximise hits per run: go to the CLOSEST lair that's up by the time you arrive
    // (a closer on-cooldown lair that pops during the walk counts, and beats a farther
    // already-up one). Never idle for a nearer lair unless it'd actually be ready when
    // you get there. Only when NO lair is up by arrival does it fall back to whichever
    // you could start fighting soonest.
    Default,
    // Penalise only wasted-respawn — idle wait is free (chases the soonest respawn
    // even if that means idling past an already-ready lair).
    Throughput,
}

// Per-candidate input for the scheduler. The caller (typically
// AutoLairManager) precomputes the BFS hop count and wait-room — keeps
// the scheduler pure (no graph dependency).
//   Lair: the marked lair room key.
//   ReadyAt: wall-clock instant at which the lair's spawn check will be
//     ready on next entry, i.e. LairTimerStore.NextReadyAt. null when
//     the lair has no clock start this session — treated as "ready now"
//     by the scheduler.
//   ApproachHops: BFS hop count from the current room to WaitRoom. null
//     when unreachable; the scheduler skips the candidate.
//   WaitRoom: the neighbour of Lair the walker should stop in while
//     waiting for ReadyAt. null when no eligible wait-room exists.
public sealed record LairCandidate(
    RoomKey Lair,
    DateTimeOffset? ReadyAt,
    int? ApproachHops,
    RoomKey? WaitRoom);

// Scheduler output. Carries every value the controller needs to drive
// the state machine + render the bottom-strip status without recomputing.
//   Lair: the target lair the player will enter.
//   WaitRoom: the room the player stops in until EntryArrival.
//   ApproachDuration: estimated wall-clock walk-time current → WaitRoom.
//   EntryArrival: wall-clock instant the player will step into the lair.
//   SlackAtEntry: EntryArrival minus the lair's ReadyAt. Negative = idle
//     wait we'll spend in WaitRoom; positive = wasted respawn (mob has
//     been up that long when we arrive). Zero = perfect timing.
//   Score: the heuristic score for this pick — exposed for diagnostics + logging.
public sealed record LairDecision(
    RoomKey Lair,
    RoomKey WaitRoom,
    TimeSpan ApproachDuration,
    DateTimeOffset EntryArrival,
    TimeSpan SlackAtEntry,
    double Score);
