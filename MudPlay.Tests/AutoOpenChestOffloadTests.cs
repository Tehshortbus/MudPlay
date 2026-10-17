using System.Text;
using MudPlay.Game.Inventory;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The auto-open engine and the Chest Offload tracker together, on a real
// InventoryManager fed the game's own lines: a flagged container that arrives is
// opened, what it gave is listed once, and the list follows the pack down as the
// items are sold, dropped, hidden or given away with no window open.
public sealed class AutoOpenChestOffloadTests : IDisposable
{
    private readonly InventoryManager _inv = new();
    // The game's output, fed the way the client receives it.
    private readonly ServerFeed _feed = new();
    private readonly ProfileService _profile = TestProfiles.Service();
    private readonly OutboundOpenObserver _outbound = new();
    private readonly List<string> _sent = new();
    private readonly List<Action> _scheduled = new();
    private readonly ChestOpenTracker _tracker;
    private readonly AutoOpenManager _engine;

    private bool _enabled = true;
    private bool _flagged = true;
    private bool _inCombat;
    private bool _resting;
    // The death record of the last death is still Active or Partial.
    private bool _deathpileOpen = true;
    // The send gate is up: an `i` sent now never reaches the wire.
    private bool _readsDropped;
    // The tracker's clock, for how long a read has gone unanswered.
    private DateTimeOffset _clock = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public AutoOpenChestOffloadTests()
    {
        _inv.AttachLineExtractor(_feed.Lines);
        _profile.LoadDefaultProfile();
        _tracker = new ChestOpenTracker(
            _inv, _profile, _outbound,
            isContainer: IsChest,
            send: Send,
            schedule: (_, a) => _scheduled.Add(a),
            post: a => a(),
            runicName: () => "runic",
            now: () => _clock);
        _engine = new AutoOpenManager(
            carriedItems: () => _inv.Snapshot.CarriedItems,
            resolve: name => IsChest(name)
                ? new AutoOpenManager.ResolvedOpen(name.Length, name, AutoOpen: _flagged)
                : null,
            isEnabled: () => _enabled,
            isLoaded: () => _inv.IsLoaded,
            open: _tracker.TryOpenNow)
        {
            InCombat = () => _inCombat,
            Resting = () => _resting,
            DeathpileOpenSince = _ => _deathpileOpen,
        };
        _inv.Changed += _engine.OnInventoryChanged;
        _inv.FullInventoryParsed += _engine.OnFullInventoryRead;
        _tracker.OpenSettled += _engine.OnOpenSettled;
        _tracker.PlayerOpened += _engine.OnPlayerOpened;
    }

    public void Dispose()
    {
        _tracker.Dispose();
        _inv.Dispose();
    }

    private static bool IsChest(string name) => name.EndsWith("chest", StringComparison.OrdinalIgnoreCase);

    // As in the client, where an engine's send passes the outbound observers just
    // as a typed line does.
    private void Send(string command)
    {
        if (_readsDropped && command == "i") return;
        _sent.Add(command);
        _outbound.ObserveOutbound(Encoding.Latin1.GetBytes(command + "\r"));
    }

    private void Feed(string text) => _feed.Line(text);

    private void Inventory(string carrying, int gold)
    {
        Feed($"You are carrying {gold} gold crowns, {carrying}");
        Feed("You have no keys.");
        Feed($"Wealth:    {gold * 100} copper farthings");
        Feed("Encumbrance:    36/2880  -  Light  [1%]");
    }

    // The settle delay after an open, or the read timeout after its `i`.
    private void RunScheduled()
    {
        Action[] due = _scheduled.ToArray();
        _scheduled.Clear();
        foreach (Action a in due) a();
    }

    private IReadOnlyList<(string Name, int Count)> Listed => _tracker.Loot(_inv.Snapshot.CarriedItems);

    // An oak chest arrives, is opened, and gives 2 moonstone, a ruby and 5 gold.
    private void TakeAndOpenOakChest()
    {
        Inventory("2 rusty dagger", gold: 10);
        Feed("You took oak chest.");
        RunScheduled();
        Inventory("2 rusty dagger, 2 moonstone, ruby", gold: 15);
    }

    [Fact]
    public void ArrivingContainer_IsOpened_AndItsContentsListedOnce()
    {
        TakeAndOpenOakChest();

        Assert.Equal(new[] { "open oak chest", "i" }, _sent);   // one read, as a typed open costs
        Assert.Equal(new[] { ("moonstone", 2), ("ruby", 1) }, Listed);
        Assert.Equal(5, _tracker.Coin.Gold);
        Assert.Equal(2, _profile.Current!.ChestLoot!.Items.Count);   // saved with the character
        Assert.StartsWith("oak chest at ", _engine.LastOpen);
    }

    [Fact]
    public void ItsContentsAreSaidToTheRoom_OnlyWhenTheBoxIsTicked_AndUnderOneName()
    {
        _tracker.SayLootToRoom = true;

        TakeAndOpenOakChest();

        Assert.Equal(
            new[] { "open oak chest", "i", ".oak chest dropped: 2 moonstone, ruby, 5 gold" }, _sent);
    }

    // A kill's other loot is still being picked up while the chest opens. What the
    // game confirms line by line is not the chest's.
    [Fact]
    public void PickupsWhileTheOpenIsRead_AreNotListedAsTheChests()
    {
        Inventory("2 rusty dagger", gold: 10);
        Feed("You took oak chest.");
        Feed("You took silver ring.");
        Feed("You picked up 30 gold crowns");
        RunScheduled();
        Inventory("2 rusty dagger, silver ring, 2 moonstone", gold: 45);

        Assert.Equal(new[] { ("moonstone", 2) }, Listed);
        Assert.Equal(5, _tracker.Coin.Gold);
    }

    [Fact]
    public void ItemsSoldDroppedHiddenOrGivenByHand_LeaveTheList_WithNoWindowOpen()
    {
        Inventory("torch", gold: 10);
        Feed("You took oak chest.");
        RunScheduled();
        Inventory("torch, 2 moonstone, ruby, sapphire, opal", gold: 15);
        var left = new List<(string, int, ChestOpenTracker.Departure)>();
        _tracker.ItemLeft += (name, count, how) => left.Add((name, count, how));

        Feed("You sold 2 moonstone for 400 copper farthings.");
        Feed("You dropped ruby.");
        Feed("You hid sapphire.");
        Assert.Equal(new[] { ("opal", 1) }, Listed);

        Feed("You just gave opal to Bob.");

        Assert.Empty(Listed);
        Assert.Equal(0, _tracker.Coin.TotalCopperValue);   // the tally goes with the last item
        Assert.Null(_profile.Current!.ChestLoot);          // nothing left saved
        Assert.Equal(
            new[]
            {
                ("moonstone", 2, ChestOpenTracker.Departure.Sold),
                ("ruby", 1, ChestOpenTracker.Departure.Dropped),
                ("sapphire", 1, ChestOpenTracker.Departure.Hidden),
                ("opal", 1, ChestOpenTracker.Departure.Given),
            },
            left);
    }

    [Fact]
    public void PartOfAListedPile_LeavesTheRestListed()
    {
        Inventory("torch", gold: 10);
        Feed("You took oak chest.");
        RunScheduled();
        Inventory("torch, 5 moonstone", gold: 10);

        Feed("You sold moonstone for 200 copper farthings.");
        Feed("You dropped 2 moonstone.");

        Assert.Equal(new[] { ("moonstone", 2) }, Listed);
        Assert.Equal(2, Assert.Single(_profile.Current!.ChestLoot!.Items).Count);
    }

    // Auto-discard takes a whole pile of what the chest gave right after the read:
    // one counted line on Paradigm, a line a copy on Stock. Either way the pile's
    // row goes and the rest of the list stays.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APileDiscardedInABatchRightAfterTheRead_LeavesTheList(bool counted)
    {
        Inventory("torch", gold: 10);
        Feed("You took oak chest.");
        RunScheduled();
        Inventory("torch, 3 moonstone, ruby", gold: 10);

        if (counted) Feed("You dropped 3 moonstone.");
        else for (int i = 0; i < 3; i++) Feed("You dropped moonstone.");

        Assert.Equal(new[] { ("ruby", 1) }, Listed);
        Assert.Equal("ruby", Assert.Single(_profile.Current!.ChestLoot!.Items).Name);
    }

    // A full read that no longer shows a listed item is as good as a confirmation:
    // it was used up, or left while nothing was watching.
    [Fact]
    public void AFullRead_ThatNoLongerShowsTheItems_EmptiesTheList()
    {
        TakeAndOpenOakChest();

        Inventory("2 rusty dagger", gold: 15);

        Assert.Empty(Listed);
        Assert.Null(_profile.Current!.ChestLoot);
    }

    // The coin tally is one figure for the list: it clears when the list no longer
    // holds any chest's items, and not before.

    [Fact]
    public void TheCoinTally_ClearsWhenTheLastListedItemLeaves()
    {
        Inventory("torch", gold: 10);
        Feed("You took oak chest.");
        RunScheduled();
        Inventory("torch, ruby", gold: 40);
        Assert.Equal(30, _tracker.Coin.Gold);

        Feed("You dropped ruby.");

        Assert.Empty(Listed);
        Assert.Equal(0, _tracker.Coin.TotalCopperValue);
        Assert.Null(_profile.Current!.ChestLoot);
    }

    [Fact]
    public void OneChestsItemsLeaving_WhileAnothersAreStillListed_ClearsNoCoin()
    {
        Inventory("torch", gold: 10);
        Feed("You took oak chest.");
        RunScheduled();
        Inventory("torch, ruby", gold: 40);          // the oak chest: a ruby and 30 gold
        Feed("You took iron chest.");
        RunScheduled();
        Inventory("torch, ruby, opal", gold: 45);    // the iron chest: an opal and 5 gold

        Feed("You dropped ruby.");
        Assert.Equal(new[] { ("opal", 1) }, Listed);
        Assert.Equal(35, _tracker.Coin.Gold);

        Feed("You dropped opal.");
        Assert.Equal(0, _tracker.Coin.TotalCopperValue);
    }

    // A chest that gave only coin has no row to leave, so its coin stays in the
    // tally until the list next empties.
    [Fact]
    public void ACoinOnlyChest_KeepsItsCoinInTheTally_UntilTheListEmpties()
    {
        Inventory("torch", gold: 10);
        Feed("You took oak chest.");
        RunScheduled();
        Inventory("torch", gold: 40);                // coin only
        Inventory("torch", gold: 40);                // a later read: nothing listed, nothing cleared
        Assert.Equal(30, _tracker.Coin.Gold);

        Feed("You took oak chest.");
        RunScheduled();
        Inventory("torch, ruby", gold: 45);
        Assert.Equal(35, _tracker.Coin.Gold);

        Feed("You dropped ruby.");
        Assert.Equal(0, _tracker.Coin.TotalCopperValue);
    }

    // Ticking Auto-open on a container that is already in the pack is not a copy
    // arriving, whatever changes in the pack next.
    [Theory]
    [InlineData("oak chest, torch")]
    [InlineData("3 oak chest, torch")]
    public void FlaggingAContainerAlreadyCarried_OpensNothing(string carrying)
    {
        _flagged = false;
        Inventory(carrying, gold: 10);

        _flagged = true;                            // ticked in Game Data
        Feed("You picked up 3 gold crowns");        // any unrelated pack change
        Feed("You took rusty dagger.");
        RunScheduled();

        Assert.Empty(_sent);
        Assert.Empty(_engine.Owed);
    }

    [Fact]
    public void AFlaggedContainerArrivingBesideUnflaggedOnes_OpensOnlyItself()
    {
        _flagged = false;
        Inventory("2 oak chest, torch", gold: 10);
        _flagged = true;

        Feed("You took oak chest.");

        Assert.Equal(new[] { "open oak chest" }, _sent);
        Assert.Empty(_engine.Owed);
    }

    [Fact]
    public void UntickedWhileItWaited_TheOpenIsDropped()
    {
        Inventory("torch", gold: 10);
        _inCombat = true;
        Feed("You took oak chest.");

        _flagged = false;
        _inCombat = false;
        _engine.Recheck();

        Assert.Empty(_sent);
        Assert.Empty(_engine.Owed);
    }

    // `open` stands a resting character up, so it waits the rest out as the door
    // and trap tries do.
    [Fact]
    public void Resting_TheOpenWaits_ForTheRestToEnd()
    {
        Inventory("torch", gold: 10);
        _resting = true;

        Feed("You took oak chest.");
        Assert.Empty(_sent);
        Assert.Equal("resting", _engine.HeldFor);

        _resting = false;
        _engine.Recheck();

        Assert.Equal(new[] { "open oak chest" }, _sent);
    }

    // Another engine's `i` went out just before the chest arrived, and its reply
    // lands after the tracker's own `i` was sent. That reply shows the pack as it
    // was before the open: it is not the read after it.
    [Fact]
    public void AnInventoryReplyAskedForBeforeTheOpen_IsNotTakenForTheReadAfterIt()
    {
        Inventory("torch", gold: 10);
        Send("i");                                   // asked for before the open
        Feed("You took oak chest.");
        RunScheduled();                              // 900 ms: the tracker's own `i`
        Inventory("torch, oak chest", gold: 10);     // the earlier read's reply
        Assert.Empty(Listed);
        Assert.Equal("oak chest", _engine.Opening);  // still waiting for its own

        Inventory("torch, 2 moonstone, ruby", gold: 15);

        Assert.Equal(new[] { ("moonstone", 2), ("ruby", 1) }, Listed);
        Assert.Equal(5, _tracker.Coin.Gold);
    }

    // The read after the open never reaches the wire, or never comes back. The
    // open ends as not read, not as having given nothing, and nothing is listed
    // from a pack that was never read.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithNoReadAfterTheOpen_ItEndsAsNotRead(bool readDropped)
    {
        Inventory("torch", gold: 10);
        Feed("You took oak chest.");
        _readsDropped = readDropped;
        RunScheduled();                              // the `i` goes out, or is dropped
        RunScheduled();                              // 3 s: nothing came back

        Assert.Null(_engine.Opening);
        Assert.Contains("not read", _engine.LastOpen);
        Assert.DoesNotContain("gave nothing", _engine.LastOpen);
        Assert.Empty(Listed);
        Assert.Equal(new[] { "open oak chest" }, _sent.Where(s => s.StartsWith("open")));
    }

    [Fact]
    public void SwitchedOff_NothingIsSent_AndNothingOpensLater()
    {
        Inventory("torch", gold: 10);
        _enabled = false;

        Feed("You took oak chest.");
        Assert.Empty(_sent);

        _enabled = true;
        Feed("You took rusty dagger.");   // any later change
        _engine.Recheck();

        Assert.Empty(_sent);
    }

    [Fact]
    public void AnOpenThatGivesNothing_IsNotTriedAgain()
    {
        Inventory("torch", gold: 10);
        Feed("You took oak chest.");
        RunScheduled();
        Inventory("torch, oak chest", gold: 10);   // still there, nothing new

        Inventory("torch, oak chest", gold: 10);
        _engine.Recheck();

        Assert.Equal(new[] { "open oak chest", "i" }, _sent);
        Assert.Empty(Listed);
        Assert.Empty(_engine.Owed);
    }

    [Fact]
    public void TwoChestsArrivingTogether_OpenInTurn_AndAddUpWithoutDoubleCounting()
    {
        Inventory("torch", gold: 10);
        Feed("You took oak chest.");
        Feed("You took iron chest.");
        Assert.Equal(new[] { "open oak chest" }, _sent);          // the second waits its turn
        Assert.Equal(new[] { "iron chest" }, _engine.Owed);

        RunScheduled();
        Inventory("torch, iron chest, 2 moonstone", gold: 10);    // oak's read → iron goes
        RunScheduled();
        Inventory("torch, 3 moonstone, ruby", gold: 12);

        Assert.Equal(new[] { "open oak chest", "i", "open iron chest", "i" }, _sent);
        Assert.Equal(new[] { ("moonstone", 3), ("ruby", 1) }, Listed);
        Assert.Equal(2, _tracker.Coin.Gold);
    }

    [Fact]
    public void ACountedPickup_OpensEachCopy()
    {
        Inventory("torch", gold: 10);

        Feed("You took 2 oak chest.");            // Paradigm's one line for two
        RunScheduled();
        Inventory("torch, oak chest, ruby", gold: 10);
        RunScheduled();
        Inventory("torch, ruby, opal", gold: 10);

        Assert.Equal(new[] { "open oak chest", "i", "open oak chest", "i" }, _sent);
        Assert.Equal(new[] { ("ruby", 1), ("opal", 1) }, Listed);
    }

    // The pack keeps a second copy under a count ("2 oak chest"); it is an arrival
    // all the same, and the one carried since connect is the one left shut.
    [Fact]
    public void ASecondIdenticalContainer_JoiningAPile_IsOpened()
    {
        Inventory("oak chest", gold: 10);
        Assert.Empty(_sent);                       // carried at connect: left shut

        Feed("You took oak chest.");

        Assert.Equal(new[] { "2 oak chest" }, _inv.Snapshot.CarriedItems);
        Assert.Equal(new[] { "open oak chest" }, _sent);
    }

    [Fact]
    public void InAFight_TheOpenWaits_ForTheFightToEnd()
    {
        Inventory("torch", gold: 10);
        _inCombat = true;

        Feed("You took oak chest.");
        Assert.Empty(_sent);
        Assert.Equal("in a fight", _engine.HeldFor);

        _inCombat = false;
        _engine.Recheck();

        Assert.Equal(new[] { "open oak chest" }, _sent);
    }

    [Fact]
    public void AChestSoldBeforeItsTurn_IsNotOpened()
    {
        Inventory("torch", gold: 10);
        _inCombat = true;
        Feed("You took oak chest.");

        Feed("You sold oak chest for 50 copper farthings.");
        _inCombat = false;
        _engine.Recheck();

        Assert.Empty(_sent);
    }

    // What the character held unopened when it died was kept that way on purpose.
    [Fact]
    public void AChestRecoveredAfterADeath_IsNotAnArrival()
    {
        Inventory("oak chest", gold: 10);
        _engine.OnPlayerDied();
        Inventory("torch", gold: 0);

        Feed("You took oak chest.");
        Assert.Empty(_sent);

        Feed("You took oak chest.");               // one more than was lost
        Assert.Equal(new[] { "open oak chest" }, _sent);
    }

    // The pile was never got back and its record is closed (found missing, or
    // cleared): the next chest off a kill is a new one.
    [Fact]
    public void AChestLostAtADeath_WhosePileIsNoLongerOpen_DoesNotSwallowAFreshOne()
    {
        Inventory("oak chest", gold: 10);
        _engine.OnPlayerDied();
        Inventory("torch", gold: 0);
        Assert.Equal(1, _engine.AwaitedFromDeath);

        _deathpileOpen = false;
        Feed("You took oak chest.");

        Assert.Equal(new[] { "open oak chest" }, _sent);
        Assert.Equal(0, _engine.AwaitedFromDeath);
    }

    // The read after the death is what says a container went. One that is still
    // in the pack then, having stayed or come straight back, is not awaited.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AChestStillInThePackAtTheReadAfterADeath_LeavesNothingAwaited(bool cameBackFirst)
    {
        Inventory("oak chest", gold: 10);
        _engine.OnPlayerDied();
        if (cameBackFirst) Feed("You took oak chest.");   // the corpse, before any read
        Inventory("oak chest", gold: 0);
        Assert.Empty(_sent);
        Assert.Equal(0, _engine.AwaitedFromDeath);

        Feed("You took oak chest.");               // a new one

        Assert.Equal(new[] { "open oak chest" }, _sent);
    }

    // Picked up mid-fight, so its open was waiting; then the death. Recovered, it
    // is opened: it was never one the player kept shut.
    [Fact]
    public void AChestOwedAnOpenAtDeath_IsOpenedWhenRecovered()
    {
        Inventory("oak chest, torch", gold: 10);    // one kept shut since connect
        _inCombat = true;
        Feed("You took oak chest.");
        Assert.Equal(new[] { "oak chest" }, _engine.Owed);

        _engine.OnPlayerDied();
        _inCombat = false;
        Inventory("torch", gold: 0);
        Assert.Empty(_sent);
        Feed("You took 2 oak chest.");              // corpse recovery: both come back

        Assert.Equal(new[] { "open oak chest" }, _sent);   // the owed one, not the kept one
        Assert.Empty(_engine.Owed);
    }

    [Fact]
    public void AChestOwedAnOpenAtDeath_ButUntickedSince_StaysShut()
    {
        Inventory("torch", gold: 10);
        _inCombat = true;
        Feed("You took oak chest.");
        _engine.OnPlayerDied();
        _inCombat = false;
        Inventory("torch", gold: 0);

        _flagged = false;
        Feed("You took oak chest.");

        Assert.Empty(_sent);
        Assert.Empty(_engine.Owed);
    }

    // The death didn't take it (or it was back before the pack was read): still in
    // the pack and still owed, it is opened once the read says so.
    [Fact]
    public void AChestOwedAnOpenAtDeath_ThatNeverLeft_IsOpenedAfterTheRead()
    {
        Inventory("torch", gold: 10);
        _inCombat = true;
        Feed("You took oak chest.");
        _engine.OnPlayerDied();
        _inCombat = false;
        Assert.Empty(_sent);

        Inventory("torch, oak chest", gold: 0);

        Assert.Equal(new[] { "open oak chest" }, _sent);
    }

    [Fact]
    public void AnOpenFromTheWindow_WhileOneIsOwed_GoesFirst()
    {
        Inventory("iron chest", gold: 10);
        _tracker.Open("iron chest");               // the window's button: its read is out
        Feed("You took oak chest.");
        Assert.Equal(new[] { "i" }, _sent);
        Assert.Equal("another open is being read", _engine.HeldFor);

        Inventory("iron chest, oak chest", gold: 10);
        RunScheduled();
        Inventory("oak chest, ruby", gold: 10);    // iron's read → the owed oak goes
        RunScheduled();
        Inventory("ruby, opal", gold: 10);

        Assert.Equal(new[] { "i", "open iron chest", "i", "open oak chest", "i" }, _sent);
        Assert.Equal(new[] { ("ruby", 1), ("opal", 1) }, Listed);
    }

    // An inventory read sent by another engine that the game never answers (its
    // rate limiter dropped it, say). The first open to pass over its own reply as
    // that read's ends unread; the count of reads out is then put right, so the
    // chests after it are read and listed.
    [Fact]
    public void OneUnansweredRead_CostsOneOpenItsRead_NotEveryOpenAfter()
    {
        Inventory("torch", gold: 10);
        _outbound.ObserveOutbound(Encoding.Latin1.GetBytes("i\r"));   // never answered
        _clock += TimeSpan.FromSeconds(5);

        Feed("You took oak chest.");
        Feed("You took iron chest.");                // owed behind it
        RunScheduled();                              // oak: its own `i` goes out
        Inventory("torch, iron chest, ruby", gold: 15);   // the reply to that `i`, passed over
        Assert.Empty(Listed);
        _clock += TimeSpan.FromSeconds(3);
        RunScheduled();                              // 3 s: oak ends unread, iron goes
        Assert.Contains("not read", _engine.LastOpen);
        Assert.Contains("open iron chest", _sent);

        _clock += TimeSpan.FromSeconds(1);
        RunScheduled();                              // iron: its own `i`
        Inventory("torch, ruby, opal", gold: 20);
        Assert.StartsWith("iron chest", _engine.LastOpen);
        Assert.Equal(new[] { ("opal", 1) }, Listed);

        // Ordinary traffic from other engines, each read answered in turn.
        _clock += TimeSpan.FromSeconds(15);
        _outbound.ObserveOutbound(Encoding.Latin1.GetBytes("i\r"));
        Inventory("torch, ruby, opal", gold: 20);

        Feed("You took steel chest.");
        _clock += TimeSpan.FromSeconds(1);
        RunScheduled();
        Inventory("torch, ruby, opal, sapphire", gold: 30);
        Assert.StartsWith("steel chest", _engine.LastOpen);
        Assert.Equal(new[] { ("opal", 1), ("sapphire", 1) }, Listed);
    }

    // A read that is never answered is given up 20 s on, when another read is asked
    // for or answered by anyone, not only when the tracker asks for its own: left,
    // it would take the reply of the next read that is answered.
    [Fact]
    public void AnUnansweredRead_IsGivenUp_WhenAnotherEngineAsksForOneLater()
    {
        Inventory("torch", gold: 10);
        _outbound.ObserveOutbound(Encoding.Latin1.GetBytes("i\r"));   // never answered
        _clock += TimeSpan.FromSeconds(25);
        _outbound.ObserveOutbound(Encoding.Latin1.GetBytes("i\r"));
        Inventory("torch", gold: 10);                // answers that one
        _clock += TimeSpan.FromSeconds(1);

        Feed("You took oak chest.");
        RunScheduled();
        Inventory("torch, ruby", gold: 10);

        Assert.Equal(new[] { ("ruby", 1) }, Listed);
    }

    // The link went with a read still out: its reply never comes, and the first
    // open after the reconnect must not wait for it.
    [Fact]
    public void ReadsStillOut_WhenTheLinkDrops_AreNotCountedAfterIt()
    {
        Inventory("torch", gold: 10);
        _outbound.ObserveOutbound(Encoding.Latin1.GetBytes("i\r"));
        _tracker.NoteDisconnected();

        Feed("You took oak chest.");
        RunScheduled();
        Inventory("torch, ruby", gold: 10);

        Assert.Equal(new[] { ("ruby", 1) }, Listed);
    }

    // A chest owed an open, taken by a hang-up penalty and picked up again by hand,
    // is opened as the new copy it is. The entry kept for its return is spent by
    // that, so a later copy that was kept shut stays shut when a death gives it back.
    [Fact]
    public void AStaleOwedIfReturnedEntry_DoesNotOpenACopyThatWasKeptShut()
    {
        Inventory("torch", gold: 10);
        _inCombat = true;
        Feed("You took oak chest.");                 // owed, held for the fight
        _engine.OnEnteredGame();                     // the link dropped and came back
        _inCombat = false;
        Inventory("torch", gold: 10);                // the hang-up penalty took it
        Assert.Equal(1, _engine.OwedIfReturned);

        Feed("You took oak chest.");                 // found and picked up by hand, later
        RunScheduled();
        Inventory("torch, ruby", gold: 10);
        Assert.Equal(1, _sent.Count(s => s == "open oak chest"));
        Assert.Equal(0, _engine.OwedIfReturned);

        _enabled = false;
        Feed("You took oak chest.");                 // arrives with Auto Get Items off: stays shut
        _enabled = true;
        _engine.Recheck();

        _engine.OnPlayerDied();
        Inventory("torch, ruby", gold: 0);           // the read after: it went with the pile
        Feed("You took oak chest.");                 // recovered

        Assert.Equal(1, _sent.Count(s => s == "open oak chest"));
    }

    // The same entry is spent when the player opens another copy of that container.
    [Fact]
    public void AnOwedIfReturnedEntry_IsSpentByAnOpenTheCharacterTypes()
    {
        Inventory("oak chest, torch", gold: 10);
        _inCombat = true;
        Feed("You took oak chest.");                 // owed (held for the fight)
        _engine.OnPlayerDied();
        Assert.Equal(1, _engine.OwedIfReturned);

        _outbound.ObserveOutbound(Encoding.Latin1.GetBytes("open oak chest\r"));   // the one kept shut

        Assert.Equal(0, _engine.OwedIfReturned);
    }

    // Before the first read of a stay in the game, a container the character opens
    // by hand has gone into that open: it is not one a hang-up took.
    [Fact]
    public void BeforeTheEntryRead_AnOwedChestOpenedByHand_LeavesNothingOwedIfReturned()
    {
        Inventory("torch", gold: 10);
        _inCombat = true;
        Feed("You took oak chest.");                 // owed
        _engine.OnEnteredGame();                     // entered by hand: no `i` of ours
        _outbound.ObserveOutbound(Encoding.Latin1.GetBytes("open oak chest\r"));
        RunScheduled();
        Inventory("torch, ruby", gold: 10);          // the tracker's own read is the first one

        Assert.Equal(0, _engine.OwedIfReturned);
        Assert.Empty(_engine.Owed);
        Assert.Equal(new[] { ("ruby", 1) }, Listed);
    }
}
