using System.Collections.Generic;
using System.Linq;

namespace FujinTerm.Game.Map;

// Deterministic Auto-Lair target picker. Given a set of marked lairs
// (with their respawn timers + last-arrival timestamps), the current
// room, and a travel-cost model, returns the lair the player should
// approach next.
//
// Why deterministic: the user has told us which lairs they want and how
// often each respawns, so a uniform-random pick over the marked set
// wastes that information. This scheduler picks the lair whose entry
// timing minimises a configurable cost (wasted respawn vs idle wait).
//
// Entry-triggered respawn: in MajorMUD the lair's respawn counter
// doesn't tick until the player enters. The scheduler treats
// LairCandidate.ReadyAt as "earliest time the room repopulates when
// re-entered" — i.e. LastEntered + RespawnSeconds. Never-entered lairs
// report ReadyAt = null and are treated as ready immediately.
//
// Scoring: per candidate, the scheduler computes slack = entryArrival -
// readyAt.
//   slack > 0 ⇒ slack seconds of wasted respawn (the mob has been up
//               that long when we step in).
//   slack < 0 ⇒ |slack| seconds of idle wait in the wait-room before
//               stepping in.
// Default heuristic minimises max(0, slack) + max(0, -slack) * idlePenalty;
// throughput heuristic minimises max(0, slack) only (idle time is free).
// idlePenalty defaults to 1.0 (treat early and late equally).
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
    // heuristic: Default = idle-penalised; Throughput = wasted-only.
    // idlePenalty: weight on idle wait time under the Default heuristic (≥ 0).
    // now: current wall-clock instant.
    public static LairDecision? PickNext(
        IReadOnlyList<LairCandidate> candidates,
        ITravelCostModel travel,
        AutoLairHeuristic heuristic = AutoLairHeuristic.Default,
        double idlePenalty = 1.0,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(travel);
        if (candidates.Count == 0) return null;

        DateTimeOffset evalAt = now ?? DateTimeOffset.UtcNow;
        double penalty = Math.Max(0, idlePenalty);
        LairDecision? best = null;

        foreach (LairCandidate c in candidates)
        {
            if (c.WaitRoom is null || c.ApproachHops is not int hops) continue;

            TimeSpan approach = travel.EstimateTravel(hops);
            TimeSpan entryHop = travel.EntryHopDuration;
            DateTimeOffset entryArrival = evalAt + approach + entryHop;

            DateTimeOffset readyAt = c.ReadyAt ?? evalAt; // null = ready now
            TimeSpan slack = entryArrival - readyAt;
            double wastedSeconds = Math.Max(0, slack.TotalSeconds);
            double idleSeconds   = Math.Max(0, -slack.TotalSeconds);

            double score = heuristic switch
            {
                AutoLairHeuristic.Throughput => wastedSeconds,
                _                            => wastedSeconds + idleSeconds * penalty,
            };

            if (best is null || score < best.Score)
            {
                best = new LairDecision(
                    Lair: c.Lair,
                    WaitRoom: c.WaitRoom.Value,
                    ApproachDuration: approach,
                    EntryArrival: entryArrival,
                    SlackAtEntry: slack,
                    Score: score);
            }
        }

        return best;
    }
}

// Scoring heuristic for AutoLairScheduler.PickNext.
public enum AutoLairHeuristic
{
    // Penalise both wasted-respawn AND idle wait, weighted by idlePenalty.
    Default,
    // Penalise only wasted-respawn — idle wait is free.
    Throughput,
}

// Per-candidate input for the scheduler. The caller (typically
// AutoLairManager) precomputes the BFS hop count and wait-room — keeps
// the scheduler pure (no graph dependency) so the scoring logic stays
// trivial to unit-test against fixtures.
//   Lair: the marked lair room key.
//   ReadyAt: wall-clock instant at which the lair's spawn check will be
//     ready on next entry, i.e. LastEntered + RespawnSeconds. null when
//     the player hasn't entered the lair this session — treated as
//     "ready now" by the scheduler.
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
