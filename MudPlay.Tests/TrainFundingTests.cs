using System.Collections.Generic;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Game.Train;
using MudPlay.Models.Profile;
using Xunit;


namespace MudPlay.Tests;

// The two decisions auto-train funding rests on: how many trainers a banked run
// actually needs (and so what it costs), and where the money comes from. Both are
// pure, so the whole decision table is pinned here rather than discovered live at
// a trainer with an empty purse.
public sealed class TrainFundingTests
{
    private static readonly IReadOnlyCollection<string> NoneDisabled = System.Array.Empty<string>();

    private static TrainerShop Trainer(int number, int map, int room, int min, int max, int markup = 0, int classRest = 0)
        => new(number, $"Trainer {number}", map, room, $"Room {room}", min, max, classRest, markup);

    // Flat metric: every room is 1 hop from every other unless overridden. Keeps
    // the itinerary tests about level bands, not geography.
    private static int? Flat(RoomKey a, RoomKey b) => a.Equals(b) ? 0 : 1;

    // ----- Itinerary: trainer chaining ----------------------------------------

    [Fact]
    public void Itinerary_ChainsToASecondTrainerAtTheBandCeiling()
    {
        // Level 9 with two levels banked, and a 1-10 trainer. It can take us to
        // 10 and no further (ServesLevel is false once MaxLVL <= level), so the
        // run must walk on to the 11+ trainer to finish.
        var trainers = new[] { Trainer(1, 1, 100, 1, 10), Trainer(2, 1, 200, 11, 20) };

        IReadOnlyList<TrainSegment> plan = TrainItineraryPlanner.Build(
            trainers, currentLevel: 9, levelsToTrain: 2, classNumber: 0,
            NoneDisabled, new RoomKey(1, 1), Flat, TrainerChoice.Closest);

        Assert.Equal(2, plan.Count);

        Assert.Equal(1, plan[0].Trainer.Number);
        Assert.Equal(9, plan[0].FromLevel);
        Assert.Equal(10, plan[0].ToLevel);
        Assert.Equal(1, plan[0].Levels);

        Assert.Equal(2, plan[1].Trainer.Number);
        Assert.Equal(10, plan[1].FromLevel);
        Assert.Equal(11, plan[1].ToLevel);
    }

    [Fact]
    public void Itinerary_StaysOnOneTrainerWhenTheBandCoversTheRun()
    {
        var trainers = new[] { Trainer(1, 1, 100, 1, 20) };

        IReadOnlyList<TrainSegment> plan = TrainItineraryPlanner.Build(
            trainers, currentLevel: 9, levelsToTrain: 3, classNumber: 0,
            NoneDisabled, new RoomKey(1, 1), Flat, TrainerChoice.Closest);

        Assert.Single(plan);
        Assert.Equal(3, plan[0].Levels);
    }

    [Fact]
    public void Itinerary_StopsShortRatherThanPricingLevelsNoTrainerServes()
    {
        // Nothing covers 11+. Train what we can; the rest stays banked.
        var trainers = new[] { Trainer(1, 1, 100, 1, 10) };

        IReadOnlyList<TrainSegment> plan = TrainItineraryPlanner.Build(
            trainers, currentLevel: 9, levelsToTrain: 5, classNumber: 0,
            NoneDisabled, new RoomKey(1, 1), Flat, TrainerChoice.Closest);

        Assert.Single(plan);
        Assert.Equal(10, plan[^1].ToLevel);
    }

    [Fact]
    public void Itinerary_PricesEachSegmentAtItsOwnTrainersMarkup()
    {
        // Same level, different markup → different bill. This is why the chain has
        // to be known before the money question can be answered at all.
        var cheap = new[] { Trainer(1, 1, 100, 1, 20, markup: 0) };
        var dear  = new[] { Trainer(1, 1, 100, 1, 20, markup: 100) };

        long cheapCost = TrainItineraryPlanner.TotalCost(TrainItineraryPlanner.Build(
            cheap, 9, 1, 0, NoneDisabled, new RoomKey(1, 1), Flat, TrainerChoice.Closest));
        long dearCost = TrainItineraryPlanner.TotalCost(TrainItineraryPlanner.Build(
            dear, 9, 1, 0, NoneDisabled, new RoomKey(1, 1), Flat, TrainerChoice.Closest));

        Assert.True(dearCost > cheapCost, $"markup ignored: {dearCost} vs {cheapCost}");
    }

    [Fact]
    public void Itinerary_SkipsDisabledTrainers()
    {
        var trainers = new[] { Trainer(1, 1, 100, 1, 10), Trainer(2, 1, 200, 1, 10) };
        var disabled = new HashSet<string> { trainers[0].RowKey };

        IReadOnlyList<TrainSegment> plan = TrainItineraryPlanner.Build(
            trainers, 9, 1, 0, disabled, new RoomKey(1, 1), Flat, TrainerChoice.Closest);

        Assert.Single(plan);
        Assert.Equal(2, plan[0].Trainer.Number);
    }

    // ----- Chain re-target: what happens on "progressed too far" ----------------

    private static TrainerShop? Chain(
        IReadOnlyList<TrainerShop> trainers, int attained, int bankable, int keep = 0,
        int ceiling = 0, RoomKey? at = null)
        => TrainItineraryPlanner.NextTrainerInChain(
            trainers, attained, bankable, keep, ceiling, classNumber: 0,
            NoneDisabled, at ?? new RoomKey(1, 1), Flat, TrainerChoice.Closest);

    [Fact]
    public void Chain_WalksOnToTheTrainerCoveringTheNextLevel()
    {
        var trainers = new[] { Trainer(1, 1, 100, 1, 10), Trainer(2, 1, 200, 11, 20) };
        TrainerShop? next = Chain(trainers, attained: 10, bankable: 1, at: new RoomKey(1, 100));

        Assert.NotNull(next);
        Assert.Equal(2, next!.Value.Number);
    }

    [Fact]
    public void Chain_StopsWhenTheReserveIsAllThatIsLeft()
    {
        var trainers = new[] { Trainer(1, 1, 100, 1, 10), Trainer(2, 1, 200, 11, 20) };
        Assert.Null(Chain(trainers, attained: 10, bankable: 2, keep: 2, at: new RoomKey(1, 100)));
    }

    [Fact]
    public void Chain_StopsAtTheCeiling()
    {
        var trainers = new[] { Trainer(1, 1, 100, 1, 10), Trainer(2, 1, 200, 11, 20) };
        Assert.Null(Chain(trainers, attained: 10, bankable: 5, ceiling: 10, at: new RoomKey(1, 100)));
    }

    [Fact]
    public void Chain_NeverReturnsTheTrainerThatJustRefusedUs()
    {
        // The guard that stops the run spinning: a trainer whose band still covers
        // the level would otherwise be re-picked while we stand in its room, and the
        // run would "walk on" to where it already is, forever.
        var trainers = new[] { Trainer(1, 1, 100, 1, 20) };
        Assert.Null(Chain(trainers, attained: 10, bankable: 3, at: new RoomKey(1, 100)));
    }

    [Fact]
    public void Chain_StopsWhenNoAllowedTrainerServesTheNextLevel()
    {
        var trainers = new[] { Trainer(1, 1, 100, 1, 10) };
        Assert.Null(Chain(trainers, attained: 10, bankable: 3, at: new RoomKey(1, 100)));
    }

    [Fact]
    public void Chain_StopsOnAnUnknownLevel()
        => Assert.Null(Chain(new[] { Trainer(1, 1, 200, 1, 20) }, attained: 0, bankable: 3));

    // ----- ShouldFire: when an armed run trips ----------------------------------

    [Theory]
    // Default threshold (0 / 1) reproduces the plain "anything above the reserve".
    [InlineData(1, 0, 0, true)]
    [InlineData(0, 0, 0, false)]
    [InlineData(1, 0, 1, true)]
    // A stacking threshold holds off until it's met.
    [InlineData(2, 0, 3, false)]
    [InlineData(3, 0, 3, true)]
    [InlineData(4, 0, 3, true)]
    // The reserve raises the bar: keep 2 means 3 banked before anything trains.
    [InlineData(2, 2, 0, false)]
    [InlineData(3, 2, 0, true)]
    // A threshold at or below the reserve could never train anything, so it floors
    // at keep + 1 rather than deadlocking.
    [InlineData(3, 3, 2, false)]
    [InlineData(4, 3, 2, true)]
    public void ShouldFire_HonoursThresholdAndReserve(int banked, int keep, int fireAt, bool expected)
        => Assert.Equal(expected, MudPlay.Game.Calculators.TrainBudgetCalculator.ShouldFire(banked, keep, fireAt));

    [Fact]
    public void ShouldFire_TreatsNegativesAsZero()
    {
        Assert.True(MudPlay.Game.Calculators.TrainBudgetCalculator.ShouldFire(1, -5, -5));
        Assert.False(MudPlay.Game.Calculators.TrainBudgetCalculator.ShouldFire(0, -5, -5));
    }

    // ----- Funding: where the money comes from ---------------------------------

    private static TrainFundingSource Stash(int room, long copper)
        => new(TrainFundingSourceKind.Stash, new RoomKey(1, room), $"Stash {room}", copper);

    private static TrainFundingSource Bank(int room, long copper)
        => new(TrainFundingSourceKind.Bank, new RoomKey(1, room), $"Bank {room}", copper);

    [Fact]
    public void Funding_PurseCoversIt_NoDetour()
    {
        TrainFundingPlan plan = TrainFundingPlanner.Plan(
            cost: 500, onHandCopper: 500, new[] { Bank(50, 99999) },
            new RoomKey(1, 1), new RoomKey(1, 9), Flat);

        Assert.True(plan.Affordable);
        Assert.Empty(plan.Legs);
        Assert.Equal(0, plan.ShortfallCopper);
    }

    [Fact]
    public void Funding_PrefersAStashOverACloserBank()
    {
        // Stash-before-bank is the user's stated order, not a distance result: the
        // bank is nearer the trainer here and must still lose.
        int? Distance(RoomKey a, RoomKey b)
        {
            if (a.Equals(b)) return 0;
            return b.Room switch { 20 => 9, 50 => 1, _ => 5 };
        }

        TrainFundingPlan plan = TrainFundingPlanner.Plan(
            cost: 1000, onHandCopper: 0,
            new[] { Stash(20, 1000), Bank(50, 1000) },
            new RoomKey(1, 1), new RoomKey(1, 9), Distance);

        Assert.True(plan.Affordable);
        Assert.Single(plan.Legs);
        Assert.Equal(TrainFundingSourceKind.Stash, plan.Legs[0].Kind);
    }

    [Fact]
    public void Funding_PicksTheBankNearestTheTrainer_NotNearestUs()
    {
        // Both cover it. The withdraw is a stop on the way to train, so what
        // matters is the walk still owed afterwards.
        RoomKey trainer = new(1, 9);
        int? Distance(RoomKey a, RoomKey b)
        {
            if (a.Equals(b)) return 0;
            if (a.Equals(trainer) || b.Equals(trainer)) return b.Room == 60 || a.Room == 60 ? 1 : 20;
            return b.Room == 50 ? 1 : 15;      // bank 50 is nearest US
        }

        TrainFundingPlan plan = TrainFundingPlanner.Plan(
            cost: 1000, onHandCopper: 0,
            new[] { Bank(50, 1000), Bank(60, 1000) },
            new RoomKey(1, 1), trainer, Distance);

        Assert.Single(plan.Legs);
        Assert.Equal(new RoomKey(1, 60), plan.Legs[0].Room);
    }

    [Fact]
    public void Funding_CombinesPurseStashAndBankWhenNoSingleSourceCovers()
    {
        TrainFundingPlan plan = TrainFundingPlanner.Plan(
            cost: 1000, onHandCopper: 400,
            new[] { Stash(20, 300), Bank(50, 500) },
            new RoomKey(1, 1), new RoomKey(1, 9), Flat);

        Assert.True(plan.Affordable);
        Assert.Equal(2, plan.Legs.Count);
        Assert.Equal(TrainFundingSourceKind.Stash, plan.Legs[0].Kind);   // stashes first
        Assert.Equal(300, plan.Legs[0].DrawCopper);
        Assert.Equal(300, plan.Legs[1].DrawCopper);                      // only the remainder
        Assert.Equal(0, plan.ShortfallCopper);
    }

    [Fact]
    public void Funding_ReportsTheShortfallWhenEverythingCombinedFallsShort()
    {
        // The terminal case: log how far short we are and stay armed. Nothing is
        // walked, so the run must not claim to be affordable.
        TrainFundingPlan plan = TrainFundingPlanner.Plan(
            cost: 5000, onHandCopper: 100,
            new[] { Stash(20, 300), Bank(50, 600) },
            new RoomKey(1, 1), new RoomKey(1, 9), Flat);

        Assert.False(plan.Affordable);
        Assert.Equal(4000, plan.ShortfallCopper);
    }

    [Fact]
    public void Funding_IgnoresUnreachableAndEmptySources()
    {
        int? Distance(RoomKey a, RoomKey b) => b.Room == 20 ? null : 1;   // stash unreachable

        TrainFundingPlan plan = TrainFundingPlanner.Plan(
            cost: 1000, onHandCopper: 0,
            new[] { Stash(20, 99999), Bank(50, 0), Bank(60, 1000) },
            new RoomKey(1, 1), new RoomKey(1, 9), Distance);

        Assert.True(plan.Affordable);
        Assert.Single(plan.Legs);
        Assert.Equal(new RoomKey(1, 60), plan.Legs[0].Room);
    }

    [Fact]
    public void Funding_MarksAStashFundedPlanAsSpeculative()
    {
        // Purse and bank are certain; a stash is a belief. A plan leaning on one
        // has to say so, because it can arrive to find the room already looted.
        TrainFundingPlan stashed = TrainFundingPlanner.Plan(
            cost: 1000, onHandCopper: 0, new[] { Stash(20, 1000) },
            new RoomKey(1, 1), new RoomKey(1, 9), Flat);

        Assert.True(stashed.Affordable);
        Assert.True(stashed.DependsOnStash);
        Assert.Equal(1000, stashed.SpeculativeCopper);
    }

    [Fact]
    public void Funding_BankOnlyPlanIsNotSpeculative()
    {
        TrainFundingPlan banked = TrainFundingPlanner.Plan(
            cost: 1000, onHandCopper: 250, new[] { Bank(50, 1000) },
            new RoomKey(1, 1), new RoomKey(1, 9), Flat);

        Assert.True(banked.Affordable);
        Assert.False(banked.DependsOnStash);
        Assert.Equal(0, banked.SpeculativeCopper);
    }

    [Fact]
    public void Funding_CombinedPlanCountsOnlyTheStashPortionAsSpeculative()
    {
        TrainFundingPlan plan = TrainFundingPlanner.Plan(
            cost: 1000, onHandCopper: 400,
            new[] { Stash(20, 300), Bank(50, 500) },
            new RoomKey(1, 1), new RoomKey(1, 9), Flat);

        Assert.True(plan.DependsOnStash);
        Assert.Equal(300, plan.SpeculativeCopper);   // only the stash leg is a guess
    }

    // ----- Forecast: when does a shortfall close? -------------------------------

    [Fact]
    public void Forecast_ProjectsFromTheEarnRate()
    {
        System.TimeSpan? eta = TrainFundingForecast.TimeToAfford(3000, copperPerHour: 1500);
        Assert.NotNull(eta);
        Assert.Equal(2.0, eta!.Value.TotalHours, precision: 3);
    }

    [Fact]
    public void Forecast_DeclinesToGuessOnANegligibleEarnRate()
    {
        // A fresh session or a loop killing nothing. Dividing by ~0 yields an absurd
        // horizon, so it must return null rather than a number the user would plan
        // around.
        Assert.Null(TrainFundingForecast.TimeToAfford(3000, copperPerHour: 0));
        Assert.Null(TrainFundingForecast.TimeToAfford(3000, copperPerHour: 0.01));
        Assert.Null(TrainFundingForecast.TimeToAfford(3000, copperPerHour: double.NaN));
    }

    [Fact]
    public void Forecast_RefusesHorizonsBeyondADay()
        => Assert.Null(TrainFundingForecast.TimeToAfford(long.MaxValue / 2, copperPerHour: 1));

    [Fact]
    public void Forecast_ZeroShortfallIsAlreadyAffordable()
        => Assert.Equal(System.TimeSpan.Zero, TrainFundingForecast.TimeToAfford(0, 1000));

    [Fact]
    public void Forecast_ConvertsToLapsOfTheRunningLoop()
    {
        double? laps = TrainFundingForecast.LapsToAfford(
            3000, copperPerHour: 1500, averageLap: System.TimeSpan.FromMinutes(30));
        Assert.NotNull(laps);
        Assert.Equal(4.0, laps!.Value, precision: 3);   // 2 h ÷ 30 min
    }

    [Fact]
    public void Forecast_NoLapTimeStillDescribesTheGap()
    {
        string text = TrainFundingForecast.Describe(3000, 1500, System.TimeSpan.Zero);
        Assert.Contains("short 3,000 copper", text);
        Assert.DoesNotContain("lap(s)", text);
    }

    [Fact]
    public void RetryDelay_NeverCollapsesToImmediate()
    {
        // The whole point of the back-off: without a floor, a broke character
        // re-prices the entire run on every exp gain. "Don't know" is the dangerous
        // case — it must not mean "retry now".
        Assert.Equal(TrainFundingForecast.UnknownRetry, TrainFundingForecast.RetryDelay(null));
        Assert.Equal(TrainFundingForecast.MinRetry,
            TrainFundingForecast.RetryDelay(System.TimeSpan.Zero));
        Assert.Equal(TrainFundingForecast.MinRetry,
            TrainFundingForecast.RetryDelay(System.TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void RetryDelay_CapsLongProjectionsSoOtherIncomeIsNoticed()
    {
        // A two-hour projection shouldn't blind us for two hours — a manual
        // withdrawal or a sold item can close the gap sooner, and re-checking is
        // only a couple of BFS sweeps.
        Assert.Equal(TrainFundingForecast.MaxRetry,
            TrainFundingForecast.RetryDelay(System.TimeSpan.FromHours(2)));
    }

    [Fact]
    public void RetryDelay_PassesSensibleProjectionsThrough()
    {
        System.TimeSpan projected = System.TimeSpan.FromMinutes(7);
        Assert.Equal(projected, TrainFundingForecast.RetryDelay(projected));
    }

    [Fact]
    public void Forecast_SaysSoWhenItCannotProject()
        => Assert.Contains("too low to project",
            TrainFundingForecast.Describe(3000, 0, System.TimeSpan.FromMinutes(30)));

    [Fact]
    public void Funding_NeverPlansMoreLegsThanTheCap()
    {
        var many = new List<TrainFundingSource>();
        for (int i = 0; i < 10; i++) many.Add(Stash(20 + i, 100));

        TrainFundingPlan plan = TrainFundingPlanner.Plan(
            cost: 10_000, onHandCopper: 0, many,
            new RoomKey(1, 1), new RoomKey(1, 9), Flat);

        Assert.False(plan.Affordable);
        Assert.True(plan.Legs.Count <= TrainFundingPlanner.MaxLegs);
    }
}
