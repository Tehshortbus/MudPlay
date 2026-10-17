using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Game.Light;
using MudPlay.Game.Map;
using MudPlay.Game.Train;
using MudPlay.Services;
using Xunit;
using static MudPlay.Tests.ErrandLightWorld;

namespace MudPlay.Tests;

// An errand's own walks provision their light (ErrandLightStop): the training walk,
// a funding leg, a stash transfer, a sell detour and a spell-shop leg. Each errand
// here is the real one on the real walker, sent to 1/3 past the dark 1/2 (see
// ErrandLightWorld). With no light for the dark room it walks to the light shop at
// 1/4 first, buys, and walks on; in spell-only mode, with a light carried, with a
// room-light buff bright enough or with Auto-Light off, it walks straight there.
public sealed class ErrandLightStopTests : IDisposable
{
    private readonly ErrandLightWorld _w = new();

    public void Dispose() => _w.Dispose();

    public enum Errand { Training, Funding, StashTransfer, SellDetour, SpellShops }

    public static TheoryData<Errand> Errands() => new(Enum.GetValues<Errand>());

    // A started errand: whether it is still under way.
    private sealed record Run(Func<bool> Busy);

    private Run Begin(Errand errand) => errand switch
    {
        Errand.Training => BeginTraining(),
        Errand.Funding => BeginFunding(),
        Errand.StashTransfer => BeginStashTransfer(),
        Errand.SellDetour => BeginSellDetour(),
        Errand.SpellShops => BeginSpellShops(),
        _ => throw new ArgumentOutOfRangeException(nameof(errand)),
    };

    // Train Now: the walk to the trainer at 1/3.
    private Run BeginTraining()
    {
        TrainerWalkManager trainer = _w.NewTrainer();
        trainer.TrainNow();
        return new Run(() => trainer.IsBusy);
    }

    // A train the purse can't pay: the funding errand's leg to the bank at 1/3, the
    // walk on to the trainer back at the start.
    private Run BeginFunding()
    {
        TrainFundingRouter funding = new(
            currentRoom: () => _w.Tracker.State.CurrentRoom?.Key,
            onHandCopper: () => _w.Inventory.Snapshot.Currency.TotalCopperValue,
            sources: () => new[] { new TrainFundingSource(TrainFundingSourceKind.Bank, Far, "Far Bank", 100_000) },
            newDistanceLookup: () => (a, b) => _w.Bfs.DistanceBetween(a, b),
            walkTo: k => _w.Walker.WalkTo(k, planThroughAcquirableGates: true),
            send: _w.Sent.Add,
            armTimer: _w.Schedule,
            reconcileStash: (_, _) => { },
            autoGetCash: () => true,
            setAutoGetCash: _ => { },
            lightStop: _w.NewStop("the funding errand"));
        _w.Walker.Event += funding.OnWalkEvent;
        Assert.Equal(TrainFundingStart.Collecting, funding.Begin(10_000, Start));
        return new Run(() => funding.IsBusy);
    }

    // The map's Transfer Stash to Bank: the stash at 1/3, the bank at the start.
    private Run BeginStashTransfer()
    {
        StashTransferRunner runner = new(
            currentRoom: () => _w.Tracker.State.CurrentRoom?.Key,
            onHandCopper: () => _w.Inventory.Snapshot.Currency.TotalCopperValue,
            walkTo: k => _w.Walker.WalkTo(k, planThroughAcquirableGates: true),
            send: _w.Sent.Add,
            armTimer: _w.Schedule,
            limitCollection: _ => { },
            surveyedCopper: () => 0,
            collectSurveyed: _ => { },
            forceAutoGetCash: _ => { },
            reconcileStash: (_, _) => { },
            notice: _ => { },
            lightStop: _w.NewStop("the stash transfer"));
        _w.Walker.Event += runner.OnWalkEvent;
        Assert.Null(runner.Start(Far, Start, "Bank"));
        return new Run(() => runner.IsBusy);
    }

    // A walk to Far West, a dagger due to sell at the shop at 1/3: the detour from
    // West turns aside to it.
    private Run BeginSellDetour()
    {
        List<string> carried = new() { "dagger", "dagger" };
        AutoSellManager sell = new(_w.Router,
            carriedItems: () => carried,
            resolve: e => e.Trim() == "dagger" ? new AutoSellManager.ResolvedSell(1, "dagger", true, 0) : null,
            shopTradesItem: (_, _) => false,
            isEnabled: () => true);
        sell.SetWireSender(_ => { });
        SellDetourManager detour = new(
            candidates: () => new[] { new SellDetourManager.Candidate(1, "dagger", carried.Count, 0, 0, new[] { Far }) },
            distance: (a, b) => _w.Bfs.DistanceBetween(a, b),
            tracker: _w.Tracker, walker: _w.Walker, loops: _w.Loop, lair: _w.Lair, sell: sell,
            coordinator: _w.Coordinator,
            isEnabled: () => true,
            blocked: () => false,
            nearestLoopRoom: (_, _) => null,
            nearBankSteps: () => 25,
            post: a => a(),
            lightStop: _w.NewStop("the sell detour"));
        _w.Keep(detour);
        _w.Keep(sell);
        Assert.True(_w.Walker.WalkTo(FarWest));
        detour.Evaluate();               // mid-step: the walk is held at the room it enters
        _w.Arrive(West);
        detour.Evaluate();
        Assert.True(detour.IsDetouring);
        return new Run(() => detour.IsDetouring);
    }

    // After a level, a scroll to buy at the shop at 1/3, the trip back to the start.
    private Run BeginSpellShops()
    {
        ShopSpellPurchase scroll = new(1, "magic missile", 1, 900, "magic missile scroll", 10);
        ShopSpellErrand spells = new(
            currentRoom: () => _w.Tracker.State.CurrentRoom?.Key,
            plan: (_, _, _, visited, _) => visited.Contains(Far)
                ? ShopSpellPlan.Empty
                : new ShopSpellPlan(new[] { new ShopSpellStop(Far, "Far Shop", new[] { scroll }) }, 10,
                    Array.Empty<string>(), Array.Empty<string>()),
            carriedScrolls: _ => Array.Empty<ShopSpellPurchase>(),
            carriedCount: _ => 0,
            isObtained: _ => false,
            walkTo: k => _w.Walker.WalkTo(k, planThroughAcquirableGates: true),
            send: _w.Sent.Add,
            armTimer: _w.Schedule,
            lightStop: _w.NewStop("the spell-shop trip"));
        _w.Walker.Event += spells.OnWalkEvent;
        Assert.True(spells.Begin(5, Start));
        _w.RunTimers();   // the `sp` read's window: the trip sets out
        return new Run(() => spells.IsBusy);
    }

    [Theory]
    [MemberData(nameof(Errands))]
    public void TheWalkRunsDark_ItBuysALightFirst_ThenWalksOn(Errand errand)
    {
        Run run = Begin(errand);
        Assert.Equal(Shop, _w.Walker.Destination);

        _w.WalkThere();
        Assert.Equal("buy torch", Assert.Single(_w.Buys));
        _w.TorchBought();

        Assert.Equal(Far, _w.Walker.Destination);
        Assert.True(run.Busy());
    }

    [Theory]
    [MemberData(nameof(Errands))]
    public void SpellOnlyMode_NothingIsBought(Errand errand)
    {
        _w.LightSettings.UseRoomLightSpellOnly = true;
        Begin(errand);
        Assert.Equal(Far, _w.Walker.Destination);
        Assert.Empty(_w.Buys);
    }

    [Theory]
    [MemberData(nameof(Errands))]
    public void ALightIsCarried_NothingIsBought(Errand errand)
    {
        _w.Carry("torch");
        Begin(errand);
        Assert.Equal(Far, _w.Walker.Destination);
        Assert.Empty(_w.Buys);
    }

    // The spell comes first: a room-light buff bright enough for the dark room
    // leaves nothing for an item to cover.
    [Theory]
    [MemberData(nameof(Errands))]
    public void TheRoomLightBuffCoversIt_NothingIsBought(Errand errand)
    {
        _w.Spell = new RoomLightSpell("star", "starlight", RoomLightSpellKind.Buff, 300, null);
        Begin(errand);
        Assert.Equal(Far, _w.Walker.Destination);
        Assert.Empty(_w.Buys);
    }

    [Theory]
    [MemberData(nameof(Errands))]
    public void AutoLightOff_NothingIsBought(Errand errand)
    {
        _w.AutoLightOn = false;
        Begin(errand);
        Assert.Equal(Far, _w.Walker.Destination);
        Assert.Empty(_w.Buys);
    }

    // A buy that never lands (no coin, sold out) walks on once its window passes.
    [Fact]
    public void TheBuyDoesNotLand_TheWindowPasses_ItWalksOnWithout()
    {
        Run run = BeginStashTransfer();
        _w.WalkThere();
        Assert.Single(_w.Buys);

        _w.RunTimers();
        Assert.Equal(Far, _w.Walker.Destination);
        Assert.True(run.Busy());
    }

    // A light picked up on the way to the shop covers the walk: nothing is bought.
    [Fact]
    public void ALightFoundOnTheWay_AtTheShopNothingIsBought()
    {
        BeginStashTransfer();
        _w.Carry("torch");
        _w.WalkThere();
        Assert.Empty(_w.Buys);
        Assert.Equal(Far, _w.Walker.Destination);
    }

    // The leg's own room sells the light: it walks straight there and buys on arrival,
    // alongside the errand's own work in the room.
    [Fact]
    public void TheLegsRoomSellsTheLight_ItBuysThereOnArrival()
    {
        _w.LightShopRooms.Clear();
        _w.LightShopRooms.Add(Far);
        BeginStashTransfer();
        Assert.Equal(Far, _w.Walker.Destination);

        _w.WalkThere();
        Assert.Contains("sea", _w.Sent);
        Assert.Equal("buy torch", Assert.Single(_w.Buys));
    }

    // A walk started while the stop buys is someone else's: the errand ends and the
    // walk is left to run; the buy's window passing walks nobody anywhere.
    [Fact]
    public void AWalkStartedDuringTheBuy_EndsTheErrand_AndKeepsTheWalk()
    {
        Run run = BeginStashTransfer();
        _w.Arrive(Shop);
        Assert.Single(_w.Buys);
        Assert.True(run.Busy());

        Assert.True(_w.Walker.WalkTo(FarWest));
        Assert.False(run.Busy());
        _w.RunTimers();
        Assert.Equal(FarWest, _w.Walker.Destination);
    }

    // Stopped on the way to the shop: the errand's walk was stopped, so the errand
    // ends as it would for its own walk.
    [Fact]
    public void AWalkStoppedBeforeTheShop_EndsTheErrand()
    {
        Run run = BeginFunding();
        Assert.Equal(Shop, _w.Walker.Destination);

        _w.Walker.Stop();
        Assert.False(run.Busy());
        Assert.Empty(_w.Buys);
    }

    // One stop a run: a stop that didn't buy isn't made again on the run's next leg,
    // dark as it is.
    [Fact]
    public void OneStopARun_TheNextLegWalksStraight()
    {
        BeginStashTransfer();
        _w.WalkThere();
        _w.RunTimers();                  // the buy's window passes without a torch
        Assert.Equal(Far, _w.Walker.Destination);
        _w.WalkThere();                  // at the stash: the search goes out
        _w.RunTimers();                  // it shows nothing: the purse goes to the bank

        Assert.Equal(Start, _w.Walker.Destination);
        Assert.Single(_w.Buys);
    }
}
