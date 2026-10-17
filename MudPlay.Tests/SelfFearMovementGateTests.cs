using MudPlay.Game.Conditions;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// While we're afraid our own navigation waits: fear runs us through obvious exits
// at random, so moves of ours only fight it. The hold lifts on the wear-off line.
public sealed class SelfFearMovementGateTests
{
    private static MessageRecord Fear() => new(
        Id: MessageRecord.ComputeId("terror", "", "", "", "You are afraid!", "The effects of fear wear off!"),
        Name: "terror",
        Flags: MessageFlags.Fear,
        RawFlagsHex: (ushort)MessageFlags.Fear,
        CasterMessage: string.Empty,
        TargetMessage: string.Empty,
        WitnessMessage: string.Empty,
        AppliedMessage: "You are afraid!",
        AppliedEndsWith: "The effects of fear wear off!");

    [Fact]
    public void Afraid_HoldsNavigation_UntilTheFearWearsOff()
    {
        LogService log = new();
        MessageStore messages = new();
        messages.Messages.Add(Fear());
        using ConditionTracker conditions = new(messages, log);
        ServerFeed feed = new();
        conditions.AttachLineExtractor(feed.Lines);
        MovementCoordinator coordinator = new(log);
        using SelfFearMovementGate gate = new(conditions, coordinator, log);

        feed.Line("You are afraid!");
        Assert.Contains(MovementCoordinator.FearGate, coordinator.AssertedGates);

        feed.Line("The effects of fear wear off!");
        Assert.DoesNotContain(MovementCoordinator.FearGate, coordinator.AssertedGates);
    }

}
