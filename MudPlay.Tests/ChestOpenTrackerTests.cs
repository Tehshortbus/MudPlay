using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game.Inventory;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class ChestOpenTrackerTests : IDisposable
{
    private readonly InventoryManager _inv = new(log: null, itemWeightResolver: null, slotResolver: null);
    // The game's output, fed the way the client receives it.
    private readonly ServerFeed _feed = new();
    private readonly ProfileService _profile = TestProfiles.Service();
    private readonly OutboundOpenObserver _typed = new();
    private readonly List<string> _sent = new();
    private readonly List<Action> _scheduled = new();
    private ChestOpenTracker _tracker;

    public ChestOpenTrackerTests()
    {
        _inv.AttachLineExtractor(_feed.Lines);
        _profile.LoadDefaultProfile();
        _tracker = NewTracker();
        _tracker.SayLootToRoom = true;   // most tests read the room line; off is the default
    }

    private ChestOpenTracker NewTracker() => new(
        _inv, _profile, _typed,
        isContainer: name => name.EndsWith("chest", StringComparison.OrdinalIgnoreCase),
        send: Send,
        schedule: (_, a) => _scheduled.Add(a),
        post: a => a(),
        runicName: () => "runic");

    public void Dispose()
    {
        _tracker.Dispose();
        _inv.Dispose();
    }

    // As in the client: whatever the tracker sends passes the outbound observers,
    // its own `open` included.
    private void Send(string command)
    {
        _sent.Add(command);
        _typed.ObserveOutbound(Encoding.Latin1.GetBytes(command + "\r"));
    }

    private void Feed(string text) => _feed.Line(text);

    private void Inventory(string carrying, int gold)
    {
        Feed($"You are carrying {gold} gold crowns, {carrying}");
        Feed("You have no keys.");
        Feed($"Wealth:    {gold * 100} copper farthings");
        Feed("Encumbrance:    36/2880  -  Light  [1%]");
    }

    private void RunScheduled()
    {
        Action[] due = _scheduled.ToArray();
        _scheduled.Clear();
        foreach (Action a in due) a();
    }

    // The Open button flow: `i`, then `open`, then `i` again after the loot spills.
    private void OpenOakChest()
    {
        Inventory("oak chest, 2 rusty dagger", gold: 10);   // the fight's daggers are already in the pack
        _tracker.Open("oak chest");
        Inventory("oak chest, 2 rusty dagger", gold: 10);   // the before-read
        RunScheduled();                                      // settle delay → the after `i`
        Inventory("2 rusty dagger, 2 moonstone, ruby", gold: 15);
    }

    [Fact]
    public void Open_ListsOnlyWhatThatOpenAdded_AndSaysItToTheRoom()
    {
        OpenOakChest();

        Assert.Equal(new[] { "i", "open oak chest", "i", ".oak chest dropped: 2 moonstone, ruby, 5 gold" }, _sent);
        Assert.Equal(new[] { ("moonstone", 2), ("ruby", 1) },
                     _tracker.Loot(_inv.Snapshot.CarriedItems));
        Assert.Equal(5, _tracker.Coin.Gold);
    }

    // Out of the box nothing is said to the room: the list and the coin are kept all
    // the same.
    [Fact]
    public void SayingLootToTheRoom_IsOffUntilTicked()
    {
        _profile.LoadDefaultProfile();
        Assert.False(_tracker.SayLootToRoom);

        OpenOakChest();

        Assert.Equal(new[] { "i", "open oak chest", "i" }, _sent);
        Assert.Equal(new[] { ("moonstone", 2), ("ruby", 1) }, _tracker.Loot(_inv.Snapshot.CarriedItems));
        Assert.Equal(5, _tracker.Coin.Gold);

        _tracker.SayLootToRoom = true;
        Assert.True(_profile.Current!.SayChestLootToRoom);   // kept on the character
    }

    [Fact]
    public void List_OutlivesTheWindow_AndReloadsFromTheProfile()
    {
        OpenOakChest();
        _tracker.Dispose();

        _tracker = NewTracker();   // a fresh tracker reads the saved list back

        Assert.Equal(new[] { ("moonstone", 2), ("ruby", 1) },
                     _tracker.Loot(_inv.Snapshot.CarriedItems));
        Assert.Equal(5, _tracker.Coin.Gold);
    }

    [Fact]
    public void ConfirmedSale_RemoveAndClear_TakeItemsOff()
    {
        OpenOakChest();

        Feed("You sold 2 moonstone for 400 copper farthings.");
        Assert.Equal(new[] { ("ruby", 1) }, _tracker.Loot(new[] { "2 moonstone", "ruby" }));

        _tracker.RemoveItem("ruby");
        Assert.Empty(_tracker.Loot(new[] { "ruby" }));

        OpenOakChestAgain();
        _tracker.Clear();
        Assert.Empty(_tracker.Loot(_inv.Snapshot.CarriedItems));
        Assert.Equal(0, _tracker.Coin.TotalCopperValue);
        Assert.Null(_profile.Current!.ChestLoot);
    }

    // A Drop from the tab goes out as `hide` under "Hide items when discarding", and
    // the game then answers "You hid …" instead of "You dropped …".
    [Fact]
    public void ConfirmedHide_TakesItemsOff_LikeADrop()
    {
        OpenOakChest();

        Feed("You hid 2 moonstone.");   // Paradigm's counted echo
        Assert.Equal(new[] { ("ruby", 1) }, _tracker.Loot(new[] { "2 moonstone", "ruby" }));

        Feed("You hid ruby.");
        Assert.Empty(_tracker.Loot(new[] { "ruby" }));
        Assert.Null(_profile.Current!.ChestLoot);   // the last item gone, the tally goes with it
        Assert.Equal(0, _tracker.Coin.TotalCopperValue);
    }

    private void OpenOakChestAgain()
    {
        _tracker.Open("oak chest");
        Inventory("oak chest", gold: 15);
        RunScheduled();
        Inventory("sapphire", gold: 15);
    }

    [Fact]
    public void TwoChestsBackToBack_AddUp_WithoutDoubleCounting()
    {
        // The first chest gives 2 moonstone; the second 1 moonstone and a ruby. The
        // second open's before already holds the first chest's loot, so the list ends
        // at 3 moonstone and a ruby — not 5.
        Inventory("oak chest, iron chest", gold: 10);
        _tracker.Open("oak chest");
        Inventory("oak chest, iron chest", gold: 10);
        RunScheduled();
        Inventory("iron chest, 2 moonstone", gold: 10);

        _tracker.Open("iron chest");
        Inventory("iron chest, 2 moonstone", gold: 10);
        RunScheduled();
        Inventory("3 moonstone, ruby", gold: 12);

        Assert.Equal(new[] { ("moonstone", 3), ("ruby", 1) }, _tracker.Loot(_inv.Snapshot.CarriedItems));
        Assert.Contains(".oak chest dropped: 2 moonstone", _sent);
        Assert.Contains(".iron chest dropped: moonstone, ruby, 2 gold", _sent);
    }

    [Fact]
    public void SecondOpenClick_WhileTheFirstIsInFlight_WaitsItsTurn()
    {
        Inventory("oak chest, iron chest", gold: 10);
        _tracker.Open("oak chest");
        _tracker.Open("iron chest");                      // clicked straight away — queued
        Inventory("oak chest, iron chest", gold: 10);    // oak's before-read
        Assert.Equal(new[] { "i", "open oak chest" }, _sent);

        RunScheduled();
        Inventory("iron chest, 2 moonstone", gold: 10);  // oak's after-read → iron starts
        Assert.Equal("i", _sent.Last());
        Inventory("iron chest, 2 moonstone", gold: 10);  // iron's before-read
        RunScheduled();
        Inventory("3 moonstone, ruby", gold: 10);

        Assert.Contains("open iron chest", _sent);
        Assert.Equal(new[] { ("moonstone", 3), ("ruby", 1) }, _tracker.Loot(_inv.Snapshot.CarriedItems));
    }

    [Fact]
    public void ChestTypedOpenWhileTheButtonsReadIsOut_IsCountedOnce()
    {
        // The iron chest's loot lands before the oak chest's before-read parses. The
        // before is taken from the moment it was typed, so its loot still counts.
        Inventory("oak chest, iron chest", gold: 10);
        _tracker.Open("oak chest");
        _typed.ObserveOutbound(Encoding.Latin1.GetBytes("open iron chest\r\n"));
        Inventory("oak chest, ruby", gold: 10);          // before-read already shows the ruby
        RunScheduled();
        Inventory("ruby, 2 moonstone", gold: 10);

        Assert.Equal(new[] { ("ruby", 1), ("moonstone", 2) }, _tracker.Loot(_inv.Snapshot.CarriedItems));
        Assert.Contains(_sent, l => l.StartsWith(".oak chest + iron chest dropped: "));
    }

    [Fact]
    public void ChestTypedOpenAfterTheAfterReadWentOut_GetsAFollowUpRead()
    {
        Inventory("oak chest, iron chest", gold: 10);
        _tracker.Open("oak chest");
        Inventory("oak chest, iron chest", gold: 10);
        RunScheduled();                                  // oak's after `i` is out
        _typed.ObserveOutbound(Encoding.Latin1.GetBytes("open iron chest\r\n"));
        Inventory("iron chest, 2 moonstone", gold: 10);  // oak's after-read, before iron's spill
        RunScheduled();                                  // the follow-up after `i`
        Inventory("2 moonstone, ruby", gold: 10);

        Assert.Equal(new[] { ("moonstone", 2), ("ruby", 1) }, _tracker.Loot(_inv.Snapshot.CarriedItems));
        Assert.Contains(".iron chest dropped: ruby", _sent);
    }

    [Fact]
    public void TypedOpen_IsTrackedToo()
    {
        Inventory("oak chest, 2 rusty dagger", gold: 10);

        _typed.ObserveOutbound(Encoding.Latin1.GetBytes("open chest\r\n"));
        RunScheduled();
        Inventory("2 rusty dagger, sapphire", gold: 10);

        Assert.Equal(new[] { ("sapphire", 1) }, _tracker.Loot(_inv.Snapshot.CarriedItems));
        Assert.Contains(".oak chest dropped: sapphire", _sent);
    }

    // With the master switch off a typed open draws no `i` and no loot line from
    // the client ("stop those too"; user, 2026-10-10). No read went out, so the
    // open ends as not read (never as having given nothing), no read is counted
    // as out for it, and the tracker is free: the next open, with the switch back
    // on, reads its own reply and lists only its own loot.
    [Fact]
    public void TypedOpen_MasterSwitchOff_SendsNothing_EndsAsNotRead_AndLeavesTheTrackerFree()
    {
        bool off = true;
        _tracker.MasterSwitchOff = () => off;
        List<bool> read = new();
        _tracker.OpenSettled += result => read.Add(result.Read);
        Inventory("oak chest, pine chest, 2 rusty dagger", gold: 10);

        _typed.ObserveOutbound(Encoding.Latin1.GetBytes("open oak chest\r\n"));
        RunScheduled();

        Assert.Empty(_sent);
        Assert.Empty(_scheduled);                                   // no timeout left waiting on a read never sent
        Assert.Equal(new[] { false }, read);                        // not read, not "gave nothing"

        Inventory("pine chest, 2 rusty dagger, sapphire", gold: 10);   // the user's own `i`: in the pack, unlisted
        Assert.Empty(_tracker.Loot(_inv.Snapshot.CarriedItems));
        Assert.Empty(_sent);                                        // and nothing said to the room

        off = false;
        _typed.ObserveOutbound(Encoding.Latin1.GetBytes("open pine chest\r\n"));
        RunScheduled();
        Assert.Equal(new[] { "i" }, _sent);
        Inventory("2 rusty dagger, sapphire, ruby", gold: 10);

        Assert.Equal(new[] { ("ruby", 1) }, _tracker.Loot(_inv.Snapshot.CarriedItems));
        Assert.Equal(new[] { false, true }, read);
    }

    // The window's Open button with the master switch off sent `i` and
    // `open oak chest`, then skipped the read after it and listed nothing. The
    // press is refused before anything goes out; with the switch back on the
    // button works as it did.
    [Fact]
    public void OpenButton_Refused_SendsNothing_AndLeavesTheTrackerFree()
    {
        bool off = true;
        List<string> asked = new();
        _tracker.RefuseOpen = name => { asked.Add(name); return off; };
        Inventory("oak chest, 2 rusty dagger", gold: 10);

        _tracker.Open("oak chest");

        Assert.Equal(new[] { "oak chest" }, asked);
        Assert.Empty(_sent);
        Assert.Empty(_scheduled);
        Assert.False(_tracker.IsOpening);

        off = false;
        _tracker.Open("oak chest");

        Assert.Equal(new[] { "i" }, _sent);
        Assert.True(_tracker.IsOpening);
    }

    // Pressed with the switch on; it went off while the button's first `i` was
    // still out. The `open` that read was for is held back like a press made now,
    // whether the read comes back or times out, and the tracker is free again.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OpenButton_SwitchGoesOffWhileItsFirstReadIsOut_TheOpenIsHeldBack(bool readComesBack)
    {
        bool off = false;
        List<string> asked = new();
        _tracker.RefuseOpen = name => { asked.Add(name); return off; };
        List<bool> settled = new();
        _tracker.OpenSettled += result => settled.Add(result.Read);
        Inventory("oak chest, 2 rusty dagger", gold: 10);
        _tracker.Open("oak chest");
        Assert.Equal(new[] { "i" }, _sent);

        off = true;
        if (readComesBack) Inventory("oak chest, 2 rusty dagger", gold: 10);
        else RunScheduled();                                 // the read's timeout

        Assert.Equal(new[] { "i" }, _sent);                  // no `open oak chest`
        Assert.Equal(new[] { "oak chest", "oak chest" }, asked);
        Assert.False(_tracker.IsOpening);
        Assert.Empty(settled);                               // no open was made to settle
        RunScheduled();
        Assert.Equal(new[] { "i" }, _sent);

        off = false;
        _tracker.Open("oak chest");
        Inventory("oak chest, 2 rusty dagger", gold: 10);
        Assert.Equal(new[] { "i", "i", "open oak chest" }, _sent);
    }

    [Fact]
    public void TypedOpen_OfSomethingThatIsntACarriedChest_IsIgnored()
    {
        Inventory("oak chest", gold: 10);

        _typed.ObserveOutbound(Encoding.Latin1.GetBytes("open door\r\n"));

        Assert.Empty(_scheduled);
    }

    // `open s` is the south door, to the game and so to the list, whatever in the
    // pack has a word starting with s.
    [Fact]
    public void TypedOpen_OfADirection_IsADoor_NotAContainer()
    {
        Inventory("small chest", gold: 10);

        _typed.ObserveOutbound(Encoding.Latin1.GetBytes("open s\r\n"));

        Assert.Empty(_scheduled);
    }

    // The game takes `op` and `ope` for `open`.
    [Theory]
    [InlineData("op oak chest")]
    [InlineData("ope chest")]
    [InlineData("OPEN oak chest")]
    public void TypedOpen_Abbreviated_IsTracked(string typed)
    {
        Inventory("oak chest, 2 rusty dagger", gold: 10);

        _typed.ObserveOutbound(Encoding.Latin1.GetBytes(typed + "\r\n"));
        RunScheduled();
        Inventory("2 rusty dagger, sapphire", gold: 10);

        Assert.Equal(new[] { ("sapphire", 1) }, _tracker.Loot(_inv.Snapshot.CarriedItems));
    }

    [Theory]
    [InlineData("o oak chest")]     // nothing to the game
    [InlineData("opened chest")]    // runs past the command's spelling
    public void TypedOpen_NotAnOpenWord_IsIgnored(string typed)
    {
        Inventory("oak chest", gold: 10);

        _typed.ObserveOutbound(Encoding.Latin1.GetBytes(typed + "\r\n"));

        Assert.Empty(_scheduled);
    }

    // An inventory read is `i`, or `inve` and longer; `in` and `inv` are nothing.
    [Theory]
    [InlineData("i", true)]
    [InlineData("inve", true)]
    [InlineData("INVENTORY", true)]
    [InlineData("in", false)]
    [InlineData("inv", false)]
    [InlineData("invite bob", false)]
    [InlineData("i am here", true)]     // only the first word is looked up
    public void InventoryRequest_IsSeenByTheGamesOwnSpellings(string typed, bool isRead)
    {
        OutboundOpenObserver observer = new();
        int reads = 0;
        observer.InventoryRequested += () => reads++;

        observer.ObserveOutbound(Encoding.Latin1.GetBytes(typed + "\r"));

        Assert.Equal(isRead ? 1 : 0, reads);
    }

    // The button's own read of the pack is still unanswered when its `open` goes
    // out on a timeout; that reply then comes after the open and is not the read
    // after it.
    [Fact]
    public void TheButtonsOwnLateBeforeRead_IsNotTakenForTheAfterRead()
    {
        Inventory("oak chest", gold: 10);
        _tracker.Open("oak chest");                  // `i` out, no reply yet
        RunScheduled();                              // 3 s: opens on the cached pack
        RunScheduled();                              // 900 ms: the after `i`
        Inventory("oak chest", gold: 10);            // the before-read's reply, late
        Assert.Empty(_tracker.Loot(_inv.Snapshot.CarriedItems));
        Assert.True(_tracker.IsOpening);

        Inventory("2 moonstone", gold: 10);

        Assert.Equal(new[] { ("moonstone", 2) }, _tracker.Loot(_inv.Snapshot.CarriedItems));
    }

    [Fact]
    public void AnnounceLines_SplitLongListsAcrossLines()
    {
        var items = Enumerable.Range(1, 12).Select(i => ($"long item name number {i}", 1)).ToList();

        IReadOnlyList<string> lines = ChestOffloadPlanner.AnnounceLines("oak chest", items, new[] { "5 gold" }, 120);

        Assert.True(lines.Count > 1);
        Assert.All(lines, l => Assert.True(l.Length <= 120, l));
        Assert.StartsWith("oak chest dropped: ", lines[0]);
        Assert.StartsWith("oak chest (more): ", lines[1]);
        Assert.EndsWith("5 gold", lines[^1]);
    }

    [Fact]
    public void AnnounceLines_NothingGiven_SaysNothing()
        => Assert.Empty(ChestOffloadPlanner.AnnounceLines("oak chest", Array.Empty<(string, int)>(), Array.Empty<string>(), 120));
}
