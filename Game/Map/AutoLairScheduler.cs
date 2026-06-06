using System.Collections.Generic;
using System.Linq;

namespace FujinTerm.Game.Map;

/// <summary>
/// Deterministic Auto-Lair target picker. Given a set of marked lairs
/// (with their respawn timers + last-arrival timestamps), the current
/// room, and a travel-cost model, returns the lair the player should
/// approach next.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why deterministic</b>: MudProxy's Auto-Roam uses uniform random
/// over the marked set, which churns CPU for no benefit when the user
/// has actually told us which lairs they want and how often each
/// respawns. This scheduler picks the lair whose entry timing minimises
/// a configurable cost (wasted respawn vs idle wait).
/// </para>
/// <para>
/// <b>Entry-triggered respawn</b>: in MajorMUD the lair's respawn
/// counter doesn't tick until the player enters. The scheduler treats
/// <see cref="LairCandidate.ReadyAt"/> as "earliest time the room
/// repopulates when re-entered" — i.e. <c>LastEntered + RespawnSeconds</c>.
/// Never-entered lairs report <see cref="LairCandidate.ReadyAt"/> =
/// <c>null</c> and are treated as ready immediately.
/// </para>
/// <para>
/// <b>Scoring</b>: per candidate, the scheduler computes
/// <c>slack = entryArrival - readyAt</c>.
/// <list type="bullet">
///   <item><c>slack &gt; 0</c> ⇒ <c>slack</c> seconds of wasted respawn
///   (the mob has been up that long when we step in).</item>
///   <item><c>slack &lt; 0</c> ⇒ <c>|slack|</c> seconds of idle wait
///   in the wait-room before stepping in.</item>
/// </list>
/// Default heuristic minimises <c>max(0, slack) + max(0, -slack) * idlePenalty</c>;
/// throughput heuristic minimises <c>max(0, slack)</c> only (idle time
/// is free). Per the plan, idlePenalty defaults to 1.0 (treat early and
/// late equally).
/// </para>
/// <para>
/// <b>Wait-room contract</b>: the caller is responsible for picking the
/// wait-room (the BFS-shortest neighbour of the lair that isn't itself
/// a marked lair, and isn't avoided). The scheduler scores against the
/// wait-room hop count, not the lair's own hop count, because the
/// player must NOT enter the lair early — the respawn check only fires
/// on entry. The single entry hop is added in via
/// <see cref="ITravelCostModel.EntryHopDuration"/>.
/// </para>
/// </remarks>
public static class AutoLairScheduler
{
    /// <summary>
    /// Pick the next lair to approach. Returns <c>null</c> when no
    /// candidate is reachable (e.g. every marker is unwalkable from
    /// here, or the list is empty).
    /// </summary>
    /// <param name="candidates">
    /// One entry per marked lair, with the pre-computed wait-room +
    /// approach hop count + ready-at timestamp. Entries with
    /// <see cref="LairCandidate.WaitRoom"/> = null or
    /// <see cref="LairCandidate.ApproachHops"/> = null are skipped
    /// (unreachable / no eligible wait-room).
    /// </param>
    /// <param name="travel">Travel-cost model — converts hop counts to durations.</param>
    /// <param name="heuristic">Default = idle-penalised; Throughput = wasted-only.</param>
    /// <param name="idlePenalty">Weight on idle wait time under the Default heuristic. ≥ 0.</param>
    /// <param name="now">Current wall-clock instant. Pass <see cref="DateTimeOffset.UtcNow"/> in prod.</param>
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

/// <summary>
/// Scoring heuristic for <see cref="AutoLairScheduler.PickNext"/>.
/// </summary>
public enum AutoLairHeuristic
{
    /// <summary>Penalise both wasted-respawn AND idle wait, weighted by idlePenalty.</summary>
    Default,
    /// <summary>Penalise only wasted-respawn — idle wait is free.</summary>
    Throughput,
}

/// <summary>
/// Per-candidate input for the scheduler. The caller (typically
/// <see cref="AutoLairManager"/>) precomputes the BFS hop count and
/// wait-room — keeps the scheduler pure (no graph dependency) so the
/// scoring logic stays trivial to unit-test against fixtures.
/// </summary>
/// <param name="Lair">The marked lair room key.</param>
/// <param name="ReadyAt">
/// Wall-clock instant at which the lair's spawn check will be ready on
/// next entry, i.e. <c>LastEntered + RespawnSeconds</c>. <c>null</c>
/// when the player hasn't entered the lair this session — treated as
/// "ready now" by the scheduler.
/// </param>
/// <param name="ApproachHops">
/// BFS hop count from the current room to <see cref="WaitRoom"/>.
/// <c>null</c> when unreachable; the scheduler skips the candidate.
/// </param>
/// <param name="WaitRoom">
/// The neighbour of <see cref="Lair"/> the walker should stop in
/// while waiting for <see cref="ReadyAt"/>. <c>null</c> when no
/// eligible wait-room exists.
/// </param>
public sealed record LairCandidate(
    RoomKey Lair,
    DateTimeOffset? ReadyAt,
    int? ApproachHops,
    RoomKey? WaitRoom);

/// <summary>
/// Scheduler output. Carries every value the controller needs to drive
/// the state machine + render the bottom-strip status without recomputing.
/// </summary>
/// <param name="Lair">The target lair the player will enter.</param>
/// <param name="WaitRoom">The room the player stops in until <see cref="EntryArrival"/>.</param>
/// <param name="ApproachDuration">Estimated wall-clock walk-time current → WaitRoom.</param>
/// <param name="EntryArrival">Wall-clock instant the player will step into the lair.</param>
/// <param name="SlackAtEntry">
/// <see cref="EntryArrival"/> minus the lair's ReadyAt. Negative = idle
/// wait we'll spend in <see cref="WaitRoom"/>; positive = wasted respawn
/// (mob has been up that long when we arrive). Zero = perfect timing.
/// </param>
/// <param name="Score">The heuristic score for this pick — exposed for diagnostics + logging.</param>
public sealed record LairDecision(
    RoomKey Lair,
    RoomKey WaitRoom,
    TimeSpan ApproachDuration,
    DateTimeOffset EntryArrival,
    TimeSpan SlackAtEntry,
    double Score);
