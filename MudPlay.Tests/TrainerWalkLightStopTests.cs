using System;
using System.Collections.Generic;
using MudPlay.Game;
using MudPlay.Game.Map;
using Xunit;
using static MudPlay.Tests.ErrandLightWorld;

namespace MudPlay.Tests;

// The training walk's light stop at its edges, driven through TrainNow on the
// ErrandLightWorld map: the trainer at 1/3 past the dark 1/2. The plain buy-first,
// spell-only and lit cases are ErrandLightStopTests', for every errand.
public sealed class TrainerWalkLightStopTests : IDisposable
{
    private readonly ErrandLightWorld _w = new();

    public void Dispose() => _w.Dispose();

    // The shop is the room the run starts in: no walk, the buy goes out at once.
    [Fact]
    public void TheShopIsWhereTheRunStarts_ItBuysAtOnce()
    {
        _w.LightShopRooms[0] = Start;
        TrainerWalkManager trainer = _w.NewTrainer();
        trainer.TrainNow();
        Assert.Equal("buy torch", Assert.Single(_w.Buys));

        _w.TorchBought();
        Assert.Equal(Far, _w.Walker.Destination);
        Assert.Equal(WalkState.Walking, _w.Walker.State);
    }

    // The shop is a stop-before boss room one hop away: the walk to it finishes in
    // the start room, inside WalkTo. That counts as not reaching it: on to the
    // trainer, no buy, no run left hanging.
    [Fact]
    public void TheWalkToTheShopEndsShortOfIt_TheRunWalksOnToTheTrainer()
    {
        _w.Walker.SetBossStopRooms(() => new HashSet<RoomKey> { Shop });
        TrainerWalkManager trainer = _w.NewTrainer();
        trainer.TrainNow();
        Assert.Empty(_w.Buys);
        Assert.Equal(Far, _w.Walker.Destination);
        Assert.Equal(WalkState.Walking, _w.Walker.State);
        Assert.True(trainer.IsBusy);
    }

    // A walk the user starts while the stop buys is theirs: the stop is dropped and
    // the run ends, instead of the buy's end walking them back to the trainer.
    [Fact]
    public void AWalkStartedDuringTheBuy_EndsTheRun_AndKeepsTheUsersWalk()
    {
        _w.LightShopRooms[0] = Start;
        TrainerWalkManager trainer = _w.NewTrainer();
        trainer.TrainNow();
        Assert.True(trainer.IsBusy);

        Assert.True(_w.Walker.WalkTo(Shop));
        Assert.False(trainer.IsBusy);
        _w.RunTimers();
        Assert.Equal(Shop, _w.Walker.Destination);
    }

    // The run's walk to the shop is stopped: someone took over, so the run ends.
    [Fact]
    public void TheWalkToTheShopIsStopped_TheRunEnds()
    {
        TrainerWalkManager trainer = _w.NewTrainer();
        trainer.TrainNow();
        Assert.Equal(Shop, _w.Walker.Destination);

        _w.Walker.Stop();
        Assert.False(trainer.IsBusy);
    }
}
