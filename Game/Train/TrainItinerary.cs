using System;
using System.Collections.Generic;
using MudPlay.Game.Calculators;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Train;

// One trainer and the run of levels it can actually take us through.
public readonly record struct TrainSegment(
    TrainerShop Trainer,
    int FromLevel,
    int ToLevel,
    long CostCopper)
{
    public int Levels => ToLevel - FromLevel;
}

// Splits "train N banked levels" across however many trainers it actually takes.
//
// A trainer serves a contiguous level band, so a character sitting at the top of
// one band can only be taken as far as that band's ceiling: at level 9 with two
// levels banked, a 1-10 trainer gets us to 10 and then refuses, because
// ServesLevel is false the moment MaxLVL <= level. The run has to walk on to
// whichever trainer covers 11 and finish there.
//
// Planning that chain up front — rather than discovering it as a "You have
// progressed too far" rejection mid-run — is also what makes the money question
// answerable: each trainer charges its own Markup%, so the bill for the whole
// chain can only be priced once the chain is known.
//
// Pure: the caller injects the distance metric, so the whole split is testable
// without a map.
public static class TrainItineraryPlanner
{
    // Matches the reactive train loop's own step cap — an itinerary longer than
    // the loop that executes it could never be finished anyway.
    public const int MaxSegments = 16;

    public static IReadOnlyList<TrainSegment> Build(
        IReadOnlyList<TrainerShop> trainers,
        int currentLevel,
        int levelsToTrain,
        int classNumber,
        IReadOnlyCollection<string> disabled,
        RoomKey from,
        Func<RoomKey, RoomKey, int?> distance,
        TrainerChoice by)
    {
        ArgumentNullException.ThrowIfNull(trainers);
        ArgumentNullException.ThrowIfNull(disabled);
        ArgumentNullException.ThrowIfNull(distance);
        if (currentLevel <= 0 || levelsToTrain <= 0) return Array.Empty<TrainSegment>();

        List<TrainSegment> segments = new();
        RoomKey cursor = from;
        int level = currentLevel;
        int remaining = levelsToTrain;

        while (remaining > 0 && segments.Count < MaxSegments)
        {
            RoomKey at = cursor;
            TrainerShop? pick = TrainerCatalog.SelectTrainer(
                trainers, level, classNumber, disabled,
                t => distance(at, new RoomKey(t.Map, t.Room)), by);

            // No allowed trainer covers the next level — stop here rather than
            // pricing levels we have no way to buy. A partial itinerary is honest:
            // the run trains what it can and the rest stays banked.
            if (pick is not { } trainer) break;

            int segmentStart = level;
            long cost = 0;
            while (remaining > 0 && trainer.ServesLevel(level))
            {
                cost += (long)ShopPriceCalculator.TrainCopper(level, trainer.Markup);
                level++;
                remaining--;
            }

            // Defensive: SelectTrainer already filtered on ServesLevel(level), so
            // the inner loop always advances at least once. If a future filter
            // change broke that, bail instead of spinning forever.
            if (level == segmentStart) break;

            segments.Add(new(trainer, segmentStart, level, cost));
            cursor = new RoomKey(trainer.Map, trainer.Room);
        }

        return segments;
    }

    // Which trainer (if any) a run should walk on to after the one it's standing at
    // refuses with "You have progressed too far to use the training provided here."
    //
    // Extracted from the coordinator so the decision is testable on its own: the
    // coordinator around it needs a game-data cache, a room graph and a live walker
    // to construct, which is why it has no tests — but this is the part that can
    // actually be wrong.
    //
    // Null means "stop here": the reserve is reached, the ceiling is reached, no
    // allowed trainer serves the next level, or the only candidate is the trainer
    // that just refused us — which would spin the run walking to the room it's
    // already in.
    public static TrainerShop? NextTrainerInChain(
        IReadOnlyList<TrainerShop> trainers,
        int attainedLevel,
        int bankableAboveAttained,
        int keepLevels,
        int ceiling,
        int classNumber,
        IReadOnlyCollection<string> disabled,
        RoomKey current,
        Func<RoomKey, RoomKey, int?> distance,
        TrainerChoice by)
    {
        ArgumentNullException.ThrowIfNull(trainers);
        ArgumentNullException.ThrowIfNull(disabled);
        ArgumentNullException.ThrowIfNull(distance);

        if (attainedLevel <= 0) return null;
        if (bankableAboveAttained <= Math.Max(0, keepLevels)) return null;
        if (ceiling > 0 && attainedLevel >= ceiling) return null;

        TrainerShop? next = TrainerCatalog.SelectTrainer(
            trainers, attainedLevel, classNumber, disabled,
            t => distance(current, new RoomKey(t.Map, t.Room)), by);

        if (next is not { } t) return null;
        return current.Equals(new RoomKey(t.Map, t.Room)) ? null : t;
    }

    public static long TotalCost(IReadOnlyList<TrainSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        long total = 0;
        foreach (TrainSegment s in segments) total += s.CostCopper;
        return total;
    }
}
