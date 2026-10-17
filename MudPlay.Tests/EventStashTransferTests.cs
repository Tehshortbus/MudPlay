using System.Collections.Generic;
using System.Text;
using MudPlay.Game.Cash;
using MudPlay.Game.Events;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// An Event's stash-transfer action: it hands the saved stash and bank rooms to the
// transfer, and its Then runs when the transfer reports it is over.
public sealed class EventStashTransferTests
{
    private sealed class Rig
    {
        public readonly EventManager Manager = EventEngineHarness.ManagerWithoutGameData();
        public readonly List<string> Sent = new();
        public readonly List<(RoomKey Stash, RoomKey Bank)> Started = new();
        public string? Refusal;
        public int Stops;
        public readonly ScheduledEvent Transfer;

        public Rig()
        {
            Manager.SetWireSender(b => Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
            Manager.SetStashTransferHooks(
                (stash, bank) => { Started.Add((stash, bank)); return Refusal; },
                () => Stops++);
            Transfer = new ScheduledEvent
            {
                Name = "bank the stash",
                ActionType = EventActionType.StashTransfer,
                TransferStash = new RoomRef(9, 413),
                TransferBank = new RoomRef(1, 297),
                Then = EventThenType.Event,
                ThenEventName = "after",
            };
            Manager.Events.Add(Transfer);
            Manager.Events.Add(new ScheduledEvent
            {
                Name = "after",
                ActionType = EventActionType.Command,
                CommandText = "stat",
                Then = EventThenType.Nothing,
            });
        }
    }

    [Fact]
    public void StartsTheTransferBetweenTheSavedRooms_AndRunsThenWhenItIsDone()
    {
        Rig r = new();
        r.Manager.Fire(r.Transfer);

        Assert.Equal((new RoomKey(9, 413), new RoomKey(1, 297)), Assert.Single(r.Started));
        Assert.Empty(r.Sent);                             // Then waits for the transfer

        r.Manager.NoteStashTransferEnded(StashTransferOutcome.Done);
        Assert.Equal("stat", Assert.Single(r.Sent));
    }

    [Fact]
    public void ATransferThatGaveUp_StillRunsThen()
    {
        Rig r = new();
        r.Manager.Fire(r.Transfer);
        r.Manager.NoteStashTransferEnded(StashTransferOutcome.Failed);

        Assert.Equal("stat", Assert.Single(r.Sent));
    }

    [Fact]
    public void ATransferTheUserStopped_DropsThen()
    {
        Rig r = new();
        r.Manager.Fire(r.Transfer);
        r.Manager.NoteStashTransferEnded(StashTransferOutcome.Stopped);

        Assert.Empty(r.Sent);
    }

    [Fact]
    public void ARefusedStart_RunsThenStraightAway()
    {
        Rig r = new() { Refusal = "no route to the stash room 9/413" };
        r.Manager.Fire(r.Transfer);

        Assert.Equal("stat", Assert.Single(r.Sent));
    }

    // A transfer started from the map menu ends through the same signal; with no
    // transfer event running it must not complete some other run.
    [Fact]
    public void AnEndWithNoTransferEventRunning_IsIgnored()
    {
        Rig r = new();
        r.Manager.NoteStashTransferEnded(StashTransferOutcome.Done);

        Assert.Empty(r.Sent);
    }

    // An event that fires mid-transfer waits for it: the transfer isn't stopped.
    [Fact]
    public void AnotherEventFiring_WaitsForTheTransfer()
    {
        Rig r = new();
        ScheduledEvent wait = new() { Name = "wait", ActionType = EventActionType.Wait, WaitSeconds = 5 };
        r.Manager.Events.Add(wait);
        r.Manager.Fire(r.Transfer);
        r.Manager.Fire(wait);

        Assert.Equal(0, r.Stops);
        Assert.Contains("'bank the stash' StashTransfer", r.Manager.RunSummary);

        r.Manager.NoteStashTransferEnded(StashTransferOutcome.Done);
        Assert.Equal("stat", Assert.Single(r.Sent));       // the transfer's Then ran first
        Assert.Contains("'wait' Wait", r.Manager.RunSummary);
    }

    // A Logoff event doesn't wait, and a transfer is between walks while it searches
    // or deposits, so it is told to stop.
    [Fact]
    public void ALogoffEventTakingOver_StopsTheTransfer()
    {
        Rig r = new();
        r.Manager.Fire(r.Transfer);
        r.Manager.Fire(new ScheduledEvent
        {
            Name = "wait", TriggerType = EventTriggerType.Logoff, ActionType = EventActionType.Wait, WaitSeconds = 5,
        });

        Assert.Equal(1, r.Stops);
        Assert.Contains("'wait' Wait", r.Manager.RunSummary);
    }

    [Fact]
    public void MissingRooms_DoNotStartATransfer()
    {
        Rig r = new();
        r.Transfer.TransferBank = null;
        r.Manager.Fire(r.Transfer);

        Assert.Empty(r.Started);
        Assert.Equal("stat", Assert.Single(r.Sent));      // not done; Then runs anyway
    }
}
