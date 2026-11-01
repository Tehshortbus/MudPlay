using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Train;

// A member this trip trains: where it is now and how far it wants to go this trip
// (its own banked levels, capped by its ceiling and the level-11 party rule).
public readonly record struct PartyTrainee(string Name, int Level, int ClassNumber, int TargetLevel);

// One trainer the trip stops at, the members it trains there, and whether the
// leader trains here (only ever the final stop).
public readonly record struct PartyTrainStop(TrainerShop Trainer, IReadOnlyList<string> Members, bool LeaderTrains);

// Orders the trainer stops for a party-train trip.
//
// Members first, leader last — a confirmed mechanic forces the order: a FOLLOWER's
// `train` drops only that follower (it's re-invited when it re-enters the realm), but
// the LEADER's `train` disbands the whole party. So the leader trains exactly once,
// at the final stop, where every member is standing and can be re-invited; it never
// chains on to another trainer afterwards, or the members would be left behind.
//
// Member stops are greedy: by default the reachable, allowed trainer that serves the
// MOST members still short of their target goes first (the trainer choice, closest or
// cheapest, breaks a tie); with PartyStopChoice.ByTrainerChoice it's the closest or
// cheapest trainer that serves any of them. Each served member advances as far as
// that trainer's band takes it, and the next stop is chosen from there — so members
// in different level bands chain across trainers the same way a single character's
// itinerary does (TrainItineraryPlanner).
//
// Pure: the caller injects the distance metric, so it's testable without a map.
public static class PartyTrainItineraryPlanner
{
    // A party trip that needs more than this many trainers has something wrong with
    // it (or is spread across too many bands to be worth one trip).
    public const int MaxStops = 8;

    public static IReadOnlyList<PartyTrainStop> Plan(
        IReadOnlyList<TrainerShop> trainers,
        IReadOnlyList<PartyTrainee> members,
        PartyTrainee? leader,
        IReadOnlyCollection<string> disabled,
        RoomKey from,
        Func<RoomKey, RoomKey, int?> distance,
        TrainerChoice by,
        PartyStopChoice stopChoice)
    {
        ArgumentNullException.ThrowIfNull(trainers);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(disabled);
        ArgumentNullException.ThrowIfNull(distance);

        Dictionary<string, int> projected = members.ToDictionary(
            m => m.Name, m => m.Level, StringComparer.OrdinalIgnoreCase);
        List<PartyTrainStop> stops = new();
        RoomKey cursor = from;

        while (stops.Count < MaxStops)
        {
            List<PartyTrainee> pending = members.Where(m => projected[m.Name] < m.TargetLevel).ToList();
            if (pending.Count == 0) break;

            TrainerShop? best = null;
            int bestCount = 0, bestDist = int.MaxValue;
            foreach (TrainerShop t in trainers)
            {
                if (!t.HasRoom || disabled.Contains(t.RowKey)) continue;
                if (distance(cursor, Room(t)) is not { } dist) continue;
                int count = pending.Count(m => Serves(t, projected[m.Name], m.ClassNumber));
                if (count == 0) continue;
                int order = stopChoice == PartyStopChoice.MostMembers ? bestCount.CompareTo(count) : 0;
                if (order == 0 && best is { } held) order = TrainerCatalog.Prefer(t, dist, held, bestDist, by);
                if (best is null || order < 0)
                {
                    best = t;
                    bestCount = count;
                    bestDist = dist;
                }
            }
            if (best is not { } stop) break;   // nobody left is servable — they keep their levels banked

            List<string> served = new();
            foreach (PartyTrainee m in pending)
            {
                if (!Serves(stop, projected[m.Name], m.ClassNumber)) continue;
                projected[m.Name] = Advance(stop, projected[m.Name], m.TargetLevel);
                served.Add(m.Name);
            }
            stops.Add(new PartyTrainStop(stop, served, LeaderTrains: false));
            cursor = Room(stop);
        }

        if (leader is not { } l) return stops;

        // The leader trains at the last stop when that trainer serves it — no extra walk.
        if (stops.Count > 0 && Serves(stops[^1].Trainer, l.Level, l.ClassNumber))
        {
            stops[^1] = stops[^1] with { LeaderTrains = true };
            return stops;
        }

        // Otherwise one more stop for the leader, and any member with levels left
        // that the leader's trainer also serves trains there too.
        RoomKey at = cursor;
        if (TrainerCatalog.SelectTrainer(trainers, l.Level, l.ClassNumber, disabled,
                t => distance(at, Room(t)), by) is { } leaderStop
            && stops.Count < MaxStops)
        {
            List<string> extra = new();
            foreach (PartyTrainee m in members)
            {
                if (projected[m.Name] >= m.TargetLevel) continue;
                if (!Serves(leaderStop, projected[m.Name], m.ClassNumber)) continue;
                projected[m.Name] = Advance(leaderStop, projected[m.Name], m.TargetLevel);
                extra.Add(m.Name);
            }
            stops.Add(new PartyTrainStop(leaderStop, extra, LeaderTrains: true));
        }
        return stops;
    }

    // The plan for the program log and the bug report: the choice that ordered the
    // stops, then who trains where, each with the level it set out at and the one it
    // means to reach — e.g. "most members first, then closest: 1. Dwarven Trainer
    // 3/1200 (markup 90%) trains Aunty L30→32; 2. Aged Titan 10/271 (markup 100%)
    // trains Barter L48→50, leader Masta L32→33".
    public static string Describe(IReadOnlyList<PartyTrainStop> stops, IReadOnlyList<PartyTrainee> members,
        PartyTrainee? leader, TrainerChoice by, PartyStopChoice stopChoice)
    {
        ArgumentNullException.ThrowIfNull(stops);
        ArgumentNullException.ThrowIfNull(members);
        string order = stopChoice == PartyStopChoice.MostMembers
            ? $"most members first, then {Word(by)}"
            : Word(by);
        if (stops.Count == 0) return $"{order}: no stop";
        Dictionary<string, PartyTrainee> byName = members.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        List<string> parts = new(stops.Count);
        for (int i = 0; i < stops.Count; i++)
        {
            PartyTrainStop stop = stops[i];
            string where = $"{stop.Trainer.Name} {stop.Trainer.Map}/{stop.Trainer.Room} (markup {stop.Trainer.Markup}%)";
            List<string> who = stop.Members.Select(n => byName.TryGetValue(n, out PartyTrainee m) ? Levels(m) : n).ToList();
            if (stop.LeaderTrains && leader is { } l) who.Add($"leader {Levels(l)}");
            parts.Add($"{i + 1}. {where} trains {(who.Count == 0 ? "nobody" : string.Join(", ", who))}");
        }
        return $"{order}: {string.Join("; ", parts)}";

        static string Word(TrainerChoice c) => c == TrainerChoice.Cheapest ? "cheapest" : "closest";
        static string Levels(PartyTrainee m) => $"{m.Name} L{m.Level}→{m.TargetLevel}";
    }

    private static bool Serves(TrainerShop t, int level, int classNumber) =>
        t.ServesLevel(level) && t.ServesClass(classNumber);

    // How far this trainer takes a member toward its target — the same per-level
    // ServesLevel walk TrainItineraryPlanner uses, so the two agree on a band's top.
    private static int Advance(TrainerShop t, int level, int target)
    {
        while (level < target && t.ServesLevel(level)) level++;
        return level;
    }

    private static RoomKey Room(TrainerShop t) => new(t.Map, t.Room);
}
