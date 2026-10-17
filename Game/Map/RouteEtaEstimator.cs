using System.Collections.Generic;

namespace MudPlay.Game.Map;

// Projects the wall-clock arrival time over a walk's REMAINING route: the
// sum of per-hop travel estimates plus, when auto-combat is on, a dwell
// allowance for every lair the walker will actually FIGHT through on the way
// (a lair of friendly / passive occupants it walks straight past adds nothing —
// see the lairWillBeFought gate).
//
// ITravelCostModel is hop-count-only (it can't see the route's rooms), so
// the lair accounting has to live outside the model — here, where the
// remaining room keys are available. The walk-status label and the program
// log both read this so the surfaced ETA matches what the scheduler's cost
// model actually predicts per hop.
public static class RouteEtaEstimator
{
    // Combat dwell added per monster for each lair the walker steps into
    // while auto-combat is on. A round figure — the real fight length varies,
    // but this keeps the ETA honest that lair rooms aren't free walk-throughs.
    public const double LairSecondsPerMonster = 5.0;

    // Estimate remaining-route arrival time.
    //   remainingRooms   — the room the walker is standing in followed by each
    //                      room it will step into (AutoWalkManager.RemainingRoomKeys).
    //   travel           — the realm-aware per-hop cost model.
    //   getRoom          — room lookup, for lair detection along the route.
    //   includeLairDwell — true only when auto-combat is on; adds
    //                      LairSecondsPerMonster × monster count for each lair
    //                      room the walker steps into. False leaves the ETA as
    //                      pure travel time (no fight allowance).
    //   lairWillBeFought — optional gate on the dwell: given a lair room, returns
    //                      true only if its occupants are actually hostile (the
    //                      client will fight them). A lair of friendly guardsmen or
    //                      passive neutrals is walked straight through, so it adds no
    //                      combat dwell — without this the ETA over-counts every
    //                      town "lair" as a fight (report paradigm-20260909-004947:
    //                      a hostile-free walk estimated ~30s over its real time).
    //                      Null = count every lair (older callers).
    public static TimeSpan Estimate(
        IReadOnlyList<RoomKey> remainingRooms,
        ITravelCostModel travel,
        Func<RoomKey, Room?> getRoom,
        bool includeLairDwell,
        Func<Room, bool>? lairWillBeFought = null)
    {
        ArgumentNullException.ThrowIfNull(travel);
        ArgumentNullException.ThrowIfNull(getRoom);
        // Fewer than two entries means no hop left to walk (Idle or already at
        // the destination) — nothing to estimate.
        if (remainingRooms is null || remainingRooms.Count < 2) return TimeSpan.Zero;

        int hops = remainingRooms.Count - 1;
        TimeSpan total = travel.EstimateTravel(hops);

        if (includeLairDwell)
        {
            // Skip index 0 — that's the room we're already in; the dwell counts
            // lairs we'll walk INTO, not the one we're leaving.
            for (int i = 1; i < remainingRooms.Count; i++)
            {
                if (getRoom(remainingRooms[i]) is not { HasLair: true } room) continue;
                // A lair we won't actually fight (friendly / passive occupants) is a
                // free walk-through — no combat dwell.
                if (lairWillBeFought is not null && !lairWillBeFought(room)) continue;
                int monsters = RoomTooltipBuilder.TryParseLairMax(room.RawLairTag, out int max) && max > 0
                    ? max
                    : 1;
                total += TimeSpan.FromSeconds(monsters * LairSecondsPerMonster);
            }
        }

        return total;
    }

    // Compact ETA phrasing shared by every surface that shows a route arrival
    // time (the walk-status label, the route picker): seconds under a minute,
    // "Nm" / "Nm Ss" beyond it. Non-positive spans read "0s".
    public static string FormatCompact(TimeSpan eta)
    {
        if (eta <= TimeSpan.Zero) return "0s";
        int totalSeconds = (int)Math.Round(eta.TotalSeconds);
        if (totalSeconds < 60) return $"{totalSeconds}s";
        int m = totalSeconds / 60, s = totalSeconds % 60;
        return s == 0 ? $"{m}m" : $"{m}m {s}s";
    }
}
