using System.Text;
using MudPlay.Game;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// A weakness or frail (Encum% cut) can push a character over max encumbrance: "You
// are too heavy to move". Not a hold — our own movement waits, a follower tells the
// leader to @wait, and both end once a fresh `i` shows the weight back under the max
// (reports paradigm-20260926-195517, paradigm-20261003-201253).
public sealed class TooHeavyWaitSignalTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public PartyState Party { get; } = new();
        public PartyRestSync Rest { get; }
        public InventoryManager Inv { get; } = new(log: null);
        // The game's output, fed the way the client receives it.
        public ServerFeed Server { get; } = new();
        public MovementCoordinator Coordinator { get; } = new();
        public TooHeavyWaitSignal Signal { get; }
        // What Signal put on the wire, through the sender the client binds.
        public List<byte[]> SignalWire { get; } = new();
        public bool Held => Coordinator.AssertedGates.Contains(MovementCoordinator.TooHeavyGate);
        public int InventoryReads => SignalWire.Count(b => Encoding.Latin1.GetString(b) == "i\r");
        public List<string> RestWire { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Inv.AttachLineExtractor(Server.Lines);
            Rest = new PartyRestSync(Party);
            Rest.SetWireSender(b => RestWire.Add(Encoding.Latin1.GetString(b)));
            Signal = new TooHeavyWaitSignal(Router, Inv, Rest, Coordinator);
            Signal.SetWireSender(SignalWire.Add);
            Party.IsInParty = true;
            Party.LeaderName = "Boss";
        }

        public void Game(string text) =>
            Router.Dispatch(new LineExtractor.EmittedLine(text, new CellAttributes[text.Length], DateTimeOffset.UtcNow, IsPromptLine: false));

        public void Inventory(int cur, int max)
        {
            Server.Rows(new[]
            {
                "You are carrying 5 copper farthings.",
                "You have no keys.",
                "Wealth:    5 copper farthings",
                $"Encumbrance:    {cur}/{max}  -  Heavy  [{cur * 100 / max}%]",
            });
        }

        public void Dispose()
        {
            Signal.Dispose();
            Inv.Dispose();
        }
    }

    [Fact]
    public void TooHeavy_TelepathsWaitWithTheReason_AndReadsInventory()
    {
        using Harness h = new();

        h.Game("You are too heavy to move!");

        Assert.Equal("/Boss @wait (too heavy to move)\r", h.RestWire[^1]);
        Assert.True(h.Signal.IsTooHeavy);
        Assert.Contains(h.SignalWire, b => Encoding.Latin1.GetString(b) == "i\r");
    }

    [Fact]
    public void StillOverTheLoweredMax_KeepsWaiting()
    {
        using Harness h = new();
        h.Game("You are too heavy to move!");

        h.Inventory(cur: 1300, max: 1200);

        Assert.True(h.Signal.IsTooHeavy);
        Assert.True(h.Rest.IsHoldingWait);
    }

    [Fact]
    public void BackUnderMax_SendsOk()
    {
        using Harness h = new();
        h.Game("You are too heavy to move!");
        h.Inventory(cur: 1300, max: 1200);

        h.Inventory(cur: 1300, max: 1290 + 60);   // the debuff wore off

        Assert.False(h.Signal.IsTooHeavy);
        Assert.Equal("/Boss @ok\r", h.RestWire[^1]);
    }

    [Fact]
    public void TooHeavy_HoldsOurOwnMovement_UntilBackUnderMax()
    {
        using Harness h = new();

        h.Game("You are too heavy to move!");
        Assert.True(h.Held);

        h.Inventory(cur: 1300, max: 1200);
        Assert.True(h.Held);

        h.Inventory(cur: 1100, max: 1200);        // the player dropped something
        Assert.False(h.Held);
    }

    // The debuff landing is when we find out: `i` shows the lowered max before any
    // move has been refused.
    [Fact]
    public void CapacityDebuff_ReadsInventory_AndHoldsOnlyWhenOverTheMax()
    {
        using Harness h = new();

        h.Signal.NoteCapacityDebuffApplied();
        h.Signal.NoteCapacityDebuffApplied();     // a second record on the same line
        Assert.Equal(1, h.InventoryReads);
        Assert.False(h.Held);

        h.Inventory(cur: 800, max: 1200);         // still under: nothing to wait for
        Assert.False(h.Signal.IsTooHeavy);
        Assert.False(h.Held);
        Assert.Empty(h.RestWire);

        h.Signal.NoteCapacityDebuffApplied();
        h.Inventory(cur: 1300, max: 1200);        // this one put us over
        Assert.True(h.Signal.IsTooHeavy);
        Assert.True(h.Held);
        Assert.Equal("/Boss @wait (too heavy to move)\r", h.RestWire[^1]);
    }

    [Fact]
    public void CapacityDebuffWearingOff_ReadsInventoryAtOnce_AndReleases()
    {
        using Harness h = new();
        h.Signal.NoteCapacityDebuffApplied();
        h.Inventory(cur: 1300, max: 1200);
        int reads = h.InventoryReads;

        h.Signal.NoteCapacityDebuffEnded();
        Assert.Equal(reads + 1, h.InventoryReads);
        Assert.True(h.Held);                      // not until the read comes back

        h.Inventory(cur: 1300, max: 1600);
        Assert.False(h.Held);
        Assert.Equal("/Boss @ok\r", h.RestWire[^1]);
    }

    [Fact]
    public void CapacityDebuffWearingOff_WhileNotOver_SendsNothing()
    {
        using Harness h = new();

        h.Signal.NoteCapacityDebuffEnded();

        Assert.Equal(0, h.InventoryReads);
    }

    [Fact]
    public void StaleReadingBeforeTheFreshI_DoesNotClear()
    {
        using Harness h = new();
        h.Inventory(cur: 1284, max: 3840);        // read before the debuff
        h.Game("You are too heavy to move!");

        h.Game("You picked up 5 copper farthings.");   // an incremental change, not a fresh `i`

        Assert.True(h.Signal.IsTooHeavy);
    }
}
