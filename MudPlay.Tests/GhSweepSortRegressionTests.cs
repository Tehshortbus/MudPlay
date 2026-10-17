using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;
using static MudPlay.Tests.RoombaHouse;

namespace MudPlay.Tests;

// How the sort uses the two records recon keeps of a room, the stacks in plain sight
// and the hidden ones, against the game's own rules for a pickup: the visible copies
// of an item are taken before a hidden stack of it, a hidden stack only once a search
// has found it, Stock takes one copy a `get`, and Paradigm refuses a counted `get`
// for more than is there. Each case here once lost items or their record. Played out
// in RoombaHouse against the real sweep manager.
public sealed class GhSweepSortRegressionTests : IDisposable
{
    private readonly RoombaHouse _house = new();

    public void Dispose() => _house.Dispose();

    private GhSweepManager Sweep => _house.Sweep;

    private int MovedItems() => Sweep.MovedSoFar.Sum(m => m.Count);

    // A pack with room for five. A chain shirt is picked up on the way, so the
    // armour room is reached with room for four: its five maces in plain sight don't
    // fit, its two hidden ones do. Sent alone, the pickup of the hidden two was
    // served from the five: the room showed three and still hid two, the hidden
    // stack was marked taken, and the later pickup of five ran out at three.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AHiddenStack_IsNotPickedUp_WhileVisibleCopiesOfTheItemLieThere(bool paradigm)
    {
        _house.Paradigm = paradigm;
        _house.Labels.SetSearchForHidden(true);
        _house.PackCarries(5);
        _house.Floor[C]["chain shirt"] = 1;
        _house.Floor[B]["mace"] = 5;
        _house.Stash[B]["mace"] = 2;
        Assert.True(Sweep.Start());
        _house.PlayOn();

        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Empty(Sweep.LeftInPlace);
        Assert.Equal(7, _house.Floor[A].GetValueOrDefault("mace"));
        Assert.Empty(_house.Stash[B]);
        Assert.Equal(8, MovedItems());
        Assert.Equal(new Dictionary<string, int> { ["mace"] = 7 }, _house.LoggedAt(A));
        Assert.Equal(new Dictionary<string, int> { ["chain shirt"] = 1 }, _house.LoggedAt(B));
    }

    // The same through a stack split for weight: eight in plain sight go as five and
    // three, and with the five in the pack neither the three nor anything else fits,
    // but on the next visit the three and the hidden two both would.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AHiddenStack_WaitsForEveryLoadOfASplitVisibleStack(bool paradigm)
    {
        _house.Paradigm = paradigm;
        _house.Labels.SetSearchForHidden(true);
        _house.PackCarries(5);
        _house.Floor[B]["mace"] = 8;
        _house.Stash[B]["mace"] = 2;
        Assert.True(Sweep.Start());
        _house.PlayOn();

        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Empty(Sweep.LeftInPlace);
        Assert.Equal(10, _house.Floor[A].GetValueOrDefault("mace"));
        Assert.Empty(_house.Stash[B]);
        Assert.Equal(new Dictionary<string, int> { ["mace"] = 10 }, _house.LoggedAt(A));
        Assert.Empty(_house.LoggedAt(B));
    }

    // Stock forgets what a searcher found when they leave the room, so the sort
    // searches again, and that search can miss. The copies in plain sight are taken
    // all the same; the hidden ones are left as not found, which is not gone: they
    // stay on the room's record and in the item-location log.
    [Fact]
    public void Stock_AHiddenStackTheSortsSearchMisses_IsLeftAsNotFound_AndStaysOnRecord()
    {
        _house.Paradigm = false;
        _house.Labels.SetSearchForHidden(true);
        _house.Floor[B]["mace"] = 3;
        _house.Stash[B]["mace"] = 2;
        _house.SearchFinds = () => Sweep.Phase == GhSweepManager.SweepPhase.Reconning;
        Assert.True(Sweep.Start());
        _house.PlayOn();

        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Equal(3, _house.Floor[A].GetValueOrDefault("mace"));
        Assert.Equal(2, _house.Stash[B].GetValueOrDefault("mace"));
        GhSweepItemFound left = Assert.Single(Sweep.LeftInPlace);
        Assert.Equal((B, "mace", GhLeftReason.NotFoundBySearch), (left.Room, left.ItemName, left.Reason));
        Assert.Equal(new Dictionary<string, int> { ["mace"] = 3 }, _house.LoggedAt(A));
        Assert.Equal(new Dictionary<string, int> { ["mace"] = 2 }, _house.LoggedAt(B));
    }

    // On Paradigm a stack once found stays gettable, so the same miss costs nothing.
    [Fact]
    public void Paradigm_AStackReconFound_IsPickedUp_ThoughTheSortsOwnSearchMisses()
    {
        _house.Labels.SetSearchForHidden(true);
        _house.Floor[B]["mace"] = 3;
        _house.Stash[B]["mace"] = 2;
        _house.SearchFinds = () => Sweep.Phase == GhSweepManager.SweepPhase.Reconning;
        Assert.True(Sweep.Start());
        _house.PlayOn();

        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Empty(Sweep.LeftInPlace);
        Assert.Equal(5, _house.Floor[A].GetValueOrDefault("mace"));
    }

    // One line of the game's lands between a search's echo and its reply. The reply
    // is then no longer shown to be a reply, and must not be read as the room's
    // display: the hidden stack would be queued with no search, refused, written off
    // as gone, and dropped from the item-location log while still in the room.
    [Fact]
    public void AReplyKnockedAwayFromItsEcho_StillRecordsTheStackAsHidden()
    {
        _house.Labels.SetSearchForHidden(true);
        _house.Stash[B]["mace"] = 2;
        _house.LineBetweenEchoAndReply = "A bronze guardian peers at you.";
        Assert.True(Sweep.Start());
        _house.PlayOn(beforeEach: () =>
        {
            if (Sweep.Phase != GhSweepManager.SweepPhase.Reconning) _house.LineBetweenEchoAndReply = null;
            if (Sweep.Phase == GhSweepManager.SweepPhase.Sorting && Sweep.PendingMoveCount > 0)
                Assert.Equal(1, Sweep.HiddenPendingCount);
        });

        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Empty(Sweep.LeftInPlace);
        Assert.Equal(2, _house.Floor[A].GetValueOrDefault("mace"));
        Assert.Equal(new Dictionary<string, int> { ["mace"] = 2 }, _house.LoggedAt(A));
        Assert.Single(_house.SweepLog("a list read at "));
    }

    // A statline that keeps "[HP=…]:" and puts text of its own after it: the row
    // splits, and what follows the prompt is that text with the command on its end,
    // so no search is ever seen echoed. Every reply is then of unknown origin, and
    // while Roomba's own search is out it is still taken for what it most likely is.
    [Fact]
    public void AStatlineWithTextAfterThePrompt_StillRecordsHiddenStacks()
    {
        _house.Prompt = "[HP=611/MA=720]: Exp=1234 >";
        _house.Labels.SetSearchForHidden(true);
        _house.Stash[B]["mace"] = 2;
        Assert.True(Sweep.Start());
        _house.PlayOn();

        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Empty(Sweep.LeftInPlace);
        Assert.Equal(2, _house.Floor[A].GetValueOrDefault("mace"));
        Assert.Single(_house.SweepLog("a list read at "));
    }

    // Paradigm refuses a counted `get` for more than is there and takes nothing
    // (`You don't see 5 mace here.`). Someone took two of five between recon and the
    // sort: the room is read once more and the three that are there are picked up.
    [Fact]
    public void Paradigm_ACountedGetRefused_RereadsTheRoom_AndTakesWhatIsThere()
    {
        _house.Floor[B]["mace"] = 5;
        Assert.True(Sweep.Start());
        bool taken = false;
        _house.PlayOn(beforeEach: () =>
        {
            if (taken || Sweep.Phase != GhSweepManager.SweepPhase.Sorting) return;
            taken = true;
            _house.Floor[B]["mace"] = 3;
        });

        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Equal(new[] { "get 5 mace", "l", "get 3 mace" },
            _house.Sent.Where(c => c == "l" || c.StartsWith("get ", StringComparison.Ordinal)));
        Assert.Empty(Sweep.LeftInPlace);
        Assert.Equal(3, _house.Floor[A].GetValueOrDefault("mace"));
        Assert.Equal(3, MovedItems());
        Assert.Single(_house.SweepLog("1/2 holds 3 mace, not 5"));
        // The two that went missing were found out by the sort, not by the final lap.
        Assert.Empty(Sweep.RoomsChangedAfterSort);
    }

    // The whole stack gone: the second read lists nothing, and the stack is left as gone.
    [Fact]
    public void Paradigm_ACountedGetRefused_WithNothingThere_IsLeftAsGone()
    {
        _house.Floor[B]["mace"] = 5;
        _house.Floor[C]["chain shirt"] = 1;
        Assert.True(Sweep.Start());
        bool taken = false;
        _house.PlayOn(beforeEach: () =>
        {
            if (taken || Sweep.Phase != GhSweepManager.SweepPhase.Sorting) return;
            taken = true;
            _house.Floor[B].Remove("mace");
        });
        // The second read, the room display the look draws, lists nothing; the
        // sweep settles on it without waiting anything out.
        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Equal(1, _house.Sends("get 5 mace"));
        GhSweepItemFound left = Assert.Single(Sweep.LeftInPlace);
        Assert.Equal((B, "mace", GhLeftReason.GoneBySortTime), (left.Room, left.ItemName, left.Reason));
        Assert.Equal(1, _house.Floor[B].GetValueOrDefault("chain shirt"));
    }

    // Stock takes a stack one `get` a copy. Five were recorded and three are there:
    // three pickups land and the fourth is refused. The three are in the pack, so
    // they are delivered and reported; only the two that weren't there are left.
    [Fact]
    public void Stock_AStackThatRunsOutPartWay_KeepsTheCopiesItTook()
    {
        _house.Paradigm = false;
        _house.Floor[B]["mace"] = 5;
        Assert.True(Sweep.Start());
        bool taken = false;
        _house.PlayOn(beforeEach: () =>
        {
            if (taken || Sweep.Phase != GhSweepManager.SweepPhase.Sorting) return;
            taken = true;
            _house.Floor[B]["mace"] = 3;
        });

        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Equal(3, _house.Floor[A].GetValueOrDefault("mace"));
        Assert.Equal(3, MovedItems());
        Assert.Equal(3, _house.Sends("drop mace"));
        GhSweepItemFound left = Assert.Single(Sweep.LeftInPlace);
        Assert.Equal((B, "mace", GhLeftReason.GoneBySortTime), (left.Room, left.ItemName, left.Reason));
        Assert.Equal(new Dictionary<string, int> { ["mace"] = 3 }, _house.LoggedAt(A));
        Assert.Empty(_house.LoggedAt(B));
    }

    // The middle room is displayed twice on recon's out-and-back lap. A display
    // never rolls, so the second one is the floor: three maces, then two, is two.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheLatestDisplay_IsTheVisibleFloor_NotTheHighestCountSeen(bool paradigm)
    {
        _house.Paradigm = paradigm;
        _house.Floor[C]["mace"] = 3;
        Assert.True(Sweep.Start());
        int walks = 0;
        _house.PlayOn(beforeEach: () =>
        {
            if (Sweep.Phase != GhSweepManager.SweepPhase.Reconning || _house.NextCommand is not ("n" or "s")) return;
            if (++walks == 3) _house.Floor[C]["mace"] = 2;   // ahead of the second pass through C
        });

        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Empty(Sweep.LeftInPlace);
        Assert.Equal(2, MovedItems());
        Assert.Equal(2, _house.Floor[A].GetValueOrDefault("mace"));
        Assert.Empty(Sweep.RoomsChangedAfterSort);
    }

    // A room's picked-up and delivered stacks are one program-log line for the room.
    [Fact]
    public void WhatWasDoneInARoom_IsOneLogLine()
    {
        _house.Floor[C]["war hammer"] = 1;
        _house.Floor[C]["mace"] = 4;
        _house.Floor[C]["chain shirt"] = 1;
        Assert.True(Sweep.Start());
        _house.PlayOn();

        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Equal(new[] { "at 1/3: picked up war hammer, 4x mace, chain shirt" }, _house.SweepLog("at 1/3:"));
        Assert.Equal(new[] { "at 1/1: delivered war hammer, 4x mace" }, _house.SweepLog("at 1/1:"));
        Assert.Equal(new[] { "at 1/2: delivered chain shirt" }, _house.SweepLog("at 1/2:"));
        Assert.Empty(_house.SweepLog("picked up "));
        Assert.Empty(_house.SweepLog("delivered "));
        Assert.Empty(_house.SweepLog("recon search "));
    }

    // Keeping the floors a sort expects costs the same per copy however much a room
    // holds: a list per room searched once per stack was 530 ms for 1,000 stacks
    // delivered to one room, on the UI thread, in the handler of the last drop.
    [Fact]
    public void FloorCounts_CostTheSamePerStack_HoweverManyARoomHolds()
    {
        ItemNameStore names = new(new GameDataCache());

        long thousand = AllocatedAdding(names, 1000);
        long fourThousand = AllocatedAdding(names, 4000);

        Assert.True(fourThousand < thousand * 6,
            $"1,000 stacks allocated {thousand:N0} bytes, 4,000 stacks {fourThousand:N0}");
    }

    private static long AllocatedAdding(ItemNameStore names, int stacks)
    {
        List<string> entries = HugeFloor.Entries(stacks).ToList();
        GhFloorCounts counts = new(names);
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (string entry in entries)
        {
            counts.Add(A, entry, -CountedCommand.SplitLeadingCount(entry).Count);
            counts.Add(B, entry, CountedCommand.SplitLeadingCount(entry).Count);
        }
        Assert.Equal(stacks, counts.EntriesAt(B).Count);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
