using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MudPlay.Game;
using MudPlay.Game.GameData;
using MudPlay.Game.Train;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A party train stop goes on, or hands back to the engine, only once the party it
// arrived with has re-formed: every train turns its trainer out of the party, and the
// reform's own hold ran out on "If leading, wait only" with the loop then walking off
// from members still coming back (report paradigm-20261101-033147).
public sealed class PartyTrainStopRejoinTests : IDisposable
{
    private readonly ErrandLightWorld _w = new();
    private readonly PartyState _party = new();
    private readonly List<string> _orders = new();
    private readonly List<(TimeSpan After, Action Fire)> _timers = new();
    private readonly ContinuationPump _pump = new();
    private readonly LogService _log = new();
    private readonly TrainerWalkManager _trainer;
    private readonly PartyTrainCoordinator _coordinator;
    private Task _running = Task.CompletedTask;

    public PartyTrainStopRejoinTests()
    {
        _trainer = _w.NewTrainer(level: 10);
        _party.Members.Add(new PartyMember { Name = "Lead", IsSelf = true });
        _party.Members.Add(new PartyMember { Name = "Bob" });
        _party.Members.Add(new PartyMember { Name = "Cid" });
        _party.IsInParty = true;
        _coordinator = new PartyTrainCoordinator(
            _party, _trainer,
            expPerHour: () => 0,
            holdings: () => null,
            keepOnHandCopper: () => 0,
            largestDeposit: () => (null, 0),
            runicName: () => "runic",
            currentRoom: () => _w.Tracker.State.CurrentRoom?.Key,
            distance: (a, b) => Math.Abs(a.Room - b.Room),
            trainers: () => new[] { new TrainerShop(1, "Far Trainer", 1, 3, "A", 1, 50, ClassRest: 0, Markup: 0) },
            nearestBankBranch: (_, _) => null,
            walkTo: key => _w.Walker.WalkTo(key, planThroughAcquirableGates: true),
            send: _orders.Add,
            recordedVersion: _ => (null, null),
            reformParty: _ => { },
            armTimer: (after, fire) => _timers.Add((after, fire)),
            inCombat: () => false,
            selfLevel: () => 10,
            selfExp: () => 0,
            recordedLevel: _ => null,
            selfTimeToLevel: () => null,
            telepathsPending: () => false,
            log: _log)
        { RejoinWindow = TimeSpan.FromSeconds(60) };
        _w.Walker.Event += e => _coordinator.OnWalkEvent(e.Kind);
        _w.Tracker.SetLocated(ErrandLightWorld.Far);
    }

    // The trip stands at the trainer the party is in, Bob told to train there; Bob's
    // train takes him out of the party, and his `done` never comes.
    private void BobTrainsAndHisDoneNeverComes()
    {
        PartyTrainStatus waiting = new(PartyTrainReadiness.Waiting, 10, 1, 0, 0, 0, 0, 0, null, 0);
        PartyTrainStatus ready = new(PartyTrainReadiness.Ready, Level: 5, ClassNumber: 1, LevelsToTrain: 1,
            CostCopper: 0, CashCopper: 0, SpareCopper: 0, BankCopper: 0, BankName: null, EtaSeconds: 0);
        _timers.Clear();   // the coordinator's own ticks, not the trip's
        _running = _pump.Run(() => _coordinator.RunTripAsync(
            new PartyTrainDecision(PartyTrainVerdict.Fire, new[] { "Bob" }, Array.Empty<string>(), Array.Empty<string>(), "test"),
            new List<PartyTrainParticipant>
            {
                new("Lead", IsLeader: true, waiting),
                new("Bob", IsLeader: false, ready),
                new("Cid", IsLeader: false, waiting with { Level = 9 }),
            }));
        Assert.Contains("/Bob @ptrain train 6 1/3", _orders);

        _party.Members.Remove(_party.Members.Single(m => m.Name == "Bob"));
        foreach ((_, Action fire) in _timers.ToArray()) fire();   // the wait for his `done` runs out
        _timers.Clear();
        _pump.Drain();
    }

    [Fact]
    public void TheTripWaitsForAMemberStillComingBack_ThenEnds()
    {
        BobTrainsAndHisDoneNeverComes();

        Assert.True(_coordinator.TripRunning);
        Assert.True(_trainer.PartyTripActive);   // the engine stays paused
        Assert.Equal(new[] { "Bob" }, _coordinator.AwaitingRejoin);
        Assert.True(_coordinator.TripCollects("Bob"));
        Assert.DoesNotContain("/Bob @ptrain trip off", _orders);

        _party.Members.Add(new PartyMember { Name = "Bob" });
        _pump.Drain();

        Assert.True(_running.IsCompletedSuccessfully);
        Assert.False(_coordinator.TripRunning);
        Assert.False(_trainer.PartyTripActive);
        Assert.False(_coordinator.TripCollects("Bob"));
        Assert.Contains(_log.Snapshot(), e => e.Source == PartyTrainCoordinator.LogCategory && e.Message == "Bob is back in the party.");
    }

    // Past "If leading, wait only" the trip goes on without whoever isn't back.
    [Fact]
    public void TheTripGoesOnWithoutAMemberNotBackWithinTheWindow()
    {
        BobTrainsAndHisDoneNeverComes();
        (TimeSpan after, Action window) = Assert.Single(_timers);
        Assert.Equal(TimeSpan.FromSeconds(60), after);

        window();
        _pump.Drain();

        Assert.True(_running.IsCompletedSuccessfully);
        Assert.False(_coordinator.TripRunning);
        Assert.Contains(_log.Snapshot(), e => e.Source == PartyTrainCoordinator.LogCategory
            && e.Message == "Bob didn't rejoin within 60s — going on without them.");
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        _w.Dispose();
    }
}
