using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game.Inventory;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// The real InventoryManager and the real AutoDiscardManager wired as AppServices
// wires them, so a confirmation line re-enters the evaluation through
// Inventory.Changed with the pack already changed under it. AutoDiscardManagerTests
// drives the engine by hand and can't show that an answer never sets off a second
// discard for the same copy.
public sealed class AutoDiscardInventoryWiringTests
{
    private sealed class Rig : IDisposable
    {
        private readonly MessageRouter _router = new();
        // The game's output, fed the way the client receives it.
        private readonly ServerFeed _feed = new();
        private readonly Dictionary<string, (int Number, bool Discard, int Keep)> _map =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly List<(DateTimeOffset Due, Action Run)> _timers = new();

        public InventoryManager Inventory { get; }
        public AutoDiscardManager Discard { get; }
        public List<string> Sent { get; } = new();
        // The real pacer, when a test asks for one: Sent is then what it let out.
        public BulkCommandPacer? Pacer { get; }
        public DateTimeOffset Now { get; private set; } = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

        // The live client dispatches a line to the router before the inventory
        // reads it. Both orders are run: neither may over-send.
        // With weights known a line that takes a copy off the character changes the
        // carried weight too, a second patch under the same line.
        public Rig(bool routerFirst = true, bool paradigm = false, bool pacer = false, bool weights = false)
        {
            Inventory = new InventoryManager(log: null, itemWeightResolver: weights ? _ => 10 : null, slotResolver: null);
            DefaultPatterns.Seed(_router);
            if (routerFirst) _feed.Lines.LineEmitted += l => _router.Dispatch(l);
            Inventory.AttachLineExtractor(_feed.Lines);
            if (!routerFirst) _feed.Lines.LineEmitted += l => _router.Dispatch(l);

            Discard = new AutoDiscardManager(_router,
                carriedItems: () => Inventory.Snapshot.CarriedItems,
                resolve: Resolve,
                isEnabled: () => true,
                isParadigm: () => paradigm,
                wornItems: () => Inventory.Snapshot.EquippedItems.Select(e => e.Name),
                litLight: () => Inventory.Snapshot.ReadiedLight?.Name,
                now: () => Now);
            Discard.SetWireSender(b => Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
            if (pacer)
            {
                Pacer = new BulkCommandPacer(Sent.Add, (delay, run) => _timers.Add((Now + delay, run)), () => Now);
                Discard.PacedSender = (commands, owner, mayGo) => Pacer.Enqueue(commands, owner, mayGo);
                Discard.RecallQueued = (owner, take) => Pacer.CancelOwned(owner, take);
                Discard.SendsQueued = () => Pacer.Pending > 0;
                Discard.CancelQueuedSends = () => Pacer.Cancel();
            }
            Inventory.Changed += Discard.OnInventoryChanged;
            Inventory.FullInventoryParsed += Discard.OnFullInventoryRead;
            Inventory.ItemHidden += item => Discard.TryConsumeSuppressedHide(item);
            Inventory.ItemSold += (name, count, _) => Discard.ReleaseHeld(name, count);
        }

        // Move the clock on, running the pacer's timers as they fall due.
        public void Advance(TimeSpan by)
        {
            DateTimeOffset end = Now + by;
            while (Now < end)
            {
                Now += TimeSpan.FromMilliseconds(10);
                while (_timers.FirstOrDefault(t => t.Due <= Now) is { Run: not null } due)
                {
                    _timers.Remove(due);
                    due.Run();
                }
            }
        }

        // The game answers one command: its line, then the prompt the pacer counts.
        public void Answer(string line)
        {
            Feed(line);
            Pacer?.NotePrompt();
            Advance(TimeSpan.FromMilliseconds(200));
        }

        public int Worn(string name)
            => Inventory.Snapshot.EquippedItems.Count(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

        public void Map(string name, int number, bool discard, int keep = 0) => _map[name] = (number, discard, keep);

        private AutoDiscardManager.ResolvedDiscard? Resolve(string entry)
        {
            string name = CountedCommand.SplitLeadingCount(entry.Trim()).Name;
            return _map.TryGetValue(name, out (int Number, bool Discard, int Keep) v)
                ? new AutoDiscardManager.ResolvedDiscard(v.Number, name, v.Discard, v.Keep)
                : null;
        }

        public void Feed(string text) => _feed.Line(text);

        public void FullInventory(string items)
        {
            Feed($"You are carrying {items}, 5 copper farthings.");
            Feed("You have no keys.");
            Feed("Wealth:    5 copper farthings");
            Feed("Encumbrance:    36/2880  -  Light  [1%]");
        }

        public int Carried(string name)
        {
            foreach (string entry in Inventory.Snapshot.CarriedItems)
            {
                (int count, string item) = CountedCommand.SplitLeadingCount(entry);
                if (string.Equals(item, name, StringComparison.OrdinalIgnoreCase)) return count;
            }
            return 0;
        }

        public void Dispose()
        {
            Discard.Dispose();
            Inventory.Dispose();
        }
    }

    public static TheoryData<bool, bool, bool, bool> EveryPileSetup()
    {
        TheoryData<bool, bool, bool, bool> setups = new();
        bool[] both = { true, false };
        foreach (bool routerFirst in both)
            foreach (bool paradigm in both)
                foreach (bool hide in both)
                    foreach (bool pacer in both)
                        setups.Add(routerFirst, paradigm, hide, pacer);
        return setups;
    }

    // A pile of ten with two to keep. Through the real pacer the first six go and
    // each answer lets one more out; without it all eight go at once. Either way
    // the answers come back one at a time, each changing the pack under the
    // engine, and the total is eight with two left, whichever of the two listeners
    // hears an answer first.
    [Theory]
    [MemberData(nameof(EveryPileSetup))]
    public void PileWithAKeepAmount_GoesOutOnce_AndTheAnswersSendNothingMore(bool routerFirst, bool paradigm, bool hide, bool pacer)
    {
        using Rig r = new(routerFirst, paradigm, pacer);
        r.Discard.HideMode = hide;
        r.Map("dagger", 1, discard: true, keep: 2);
        string verb = hide ? "hide" : "drop";
        string past = hide ? "hid" : "dropped";

        r.FullInventory("10 dagger");
        r.Advance(TimeSpan.FromSeconds(1));

        if (paradigm)
        {
            Assert.Equal(new[] { $"{verb} 8 dagger" }, r.Sent);
            r.Answer($"You {past} 8 dagger.");
        }
        else
        {
            Assert.Equal(pacer ? BulkCommandPacer.Window : 8, r.Sent.Count);
            for (int answered = 1; answered <= r.Sent.Count && answered <= 50; answered++)
            {
                r.Answer($"You {past} dagger.");
                Assert.Equal(10 - answered, r.Carried("dagger"));
            }
            Assert.All(r.Sent, s => Assert.Equal($"{verb} dagger", s));
        }

        int sent = paradigm ? 1 : 8;
        Assert.Equal(sent, r.Sent.Count);
        Assert.Equal(2, r.Carried("dagger"));
        Assert.Equal(0, r.Pacer?.Pending ?? 0);
        Assert.Equal(0, r.Discard.UnansweredDrops + r.Discard.UnansweredHides);

        // The two kept stay through a later change of the pack and a later read.
        r.Feed("You picked up 3 copper farthings");
        r.FullInventory("2 dagger");
        r.Advance(BulkCommandPacer.AnswerTimeout);
        Assert.Equal(sent, r.Sent.Count);
    }

    // The keep amount counts what is worn, read off the real `i` listing, and the
    // worn copy is never the one sent for.
    [Theory]
    [InlineData("gold ring (Finger), gold ring", 1, 1)]
    [InlineData("gold ring (Finger), 2 gold ring", 1, 2)]
    [InlineData("gold ring (Finger), 2 gold ring", 2, 1)]
    [InlineData("gold ring (Finger)", 0, 0)]
    [InlineData("2 gold ring", 1, 1)]
    public void WornCopiesInTheListing_CountTowardTheKeepAmount(string carrying, int keep, int discarded)
    {
        using Rig r = new(routerFirst: true);
        r.Map("gold ring", 5, discard: true, keep: keep);

        r.FullInventory(carrying);

        Assert.Equal(discarded, r.Sent.Count);
        Assert.All(r.Sent, s => Assert.Equal("drop gold ring", s));
    }

    // The listing sets the lit light apart, but to the game it is one more copy in a
    // pack slot: it is counted with the spares, and it is not protected. Which copy
    // a drop takes is the game's choice.
    [Theory]
    [InlineData("torch (Readied/200), torch", 1, 1)]
    [InlineData("torch (Readied/200), 2 torch", 1, 2)]
    [InlineData("torch (Readied/200)", 1, 0)]
    [InlineData("torch (Readied/200)", 0, 1)]     // flagged with none to keep: the lit one goes
    public void LitLightInTheListing_IsCountedAsAPackCopy(string carrying, int keep, int discarded)
    {
        using Rig r = new(routerFirst: true);
        r.Map("torch", 3, discard: true, keep: keep);

        r.FullInventory(carrying);

        Assert.Equal(discarded, r.Sent.Count);
        Assert.All(r.Sent, s => Assert.Equal("drop torch", s));
    }

    // Chest Offload's Drop on Stock in hide mode: one hide per copy of the pile, each
    // answer taking a copy out of the pack, and no second hide for any of them.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ByHandHidePile_OnStock_EachCopyAnsweredOnce(bool routerFirst)
    {
        using Rig r = new(routerFirst);
        r.Discard.HideMode = true;
        r.Map("moonstone", 7, discard: false);
        r.FullInventory("3 moonstone");
        List<string> byHand = new();

        Assert.Equal(3, r.Discard.EmitDiscard(byHand.Add, "moonstone", 3).Sent);
        Assert.Equal(0, r.Discard.EmitDiscard(byHand.Add, "moonstone", 3).Sent);

        r.Feed("You hid moonstone.");
        r.Feed("You hid moonstone.");
        r.Feed("There is no room to hide moonstone here.");

        Assert.Equal(3, byHand.Count);
        Assert.Empty(r.Sent);
        Assert.Equal(1, r.Carried("moonstone"));
        Assert.Equal(0, r.Discard.UnansweredHides);
        Assert.Equal(1, r.Discard.HeldFor("moonstone"));
    }

    // The same count serves a by-hand discard: the lit light is a copy the game
    // holds in the pack, so a discard for it is one the game will answer.
    [Fact]
    public void ByHandDiscard_CountsTheLitLightAsAPackCopy()
    {
        using Rig r = new(routerFirst: true);
        r.Map("torch", 3, discard: false);
        r.FullInventory("torch (Readied/200)");
        List<string> byHand = new();

        Assert.Equal("torch", r.Inventory.Snapshot.ReadiedLight?.Name);
        Assert.Equal(1, r.Discard.EmitDiscard(byHand.Add, "torch", 1).Sent);
        Assert.Equal(new[] { "drop torch" }, byHand);
    }

    // The lit light is the only copy and is discarded. Left listed as lit until the
    // next read, it was counted again and a second discard went out for a torch no
    // longer held: on Stock a drop then takes another item whose name matches in
    // part, and a hide of nothing is said aloud.
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void LitLightDiscarded_IsNotCountedAgain(bool routerFirst, bool hide, bool weights)
    {
        using Rig r = new(routerFirst, weights: weights);
        r.Discard.HideMode = hide;
        r.Map("torch", 3, discard: true, keep: 0);
        r.FullInventory("torch (Readied/200)");
        Assert.Single(r.Sent);

        r.Feed(hide ? "You hid torch." : "You dropped torch.");
        r.Feed("You picked up 3 copper farthings");

        Assert.Single(r.Sent);
        Assert.Null(r.Inventory.Snapshot.ReadiedLight);
        Assert.Equal(0, r.Discard.UnansweredDrops + r.Discard.UnansweredHides);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LitLightAndASpareDiscarded_SendNoThird(bool routerFirst)
    {
        using Rig r = new(routerFirst, weights: true);
        r.Map("torch", 3, discard: true, keep: 0);
        r.FullInventory("torch (Readied/200), torch");
        Assert.Equal(2, r.Sent.Count);

        r.Feed("You dropped torch.");
        r.Feed("You dropped torch.");
        r.Feed("You picked up 3 copper farthings");

        Assert.Equal(2, r.Sent.Count);
    }

    // Taking the lit light off puts it back in the pack as the one torch held:
    // with one to keep, nothing is discarded.
    [Theory]
    [InlineData("You have removed torch and extinguished it.")]
    [InlineData("You have removed torch.")]
    public void LitLightTakenOff_IsStillOneCopy_AndIsKept(string line)
    {
        using Rig r = new();
        r.Map("torch", 3, discard: true, keep: 1);
        r.FullInventory("torch (Readied/200)");

        r.Feed(line);

        Assert.Empty(r.Sent);
        Assert.Equal(1, r.Carried("torch"));
        Assert.Null(r.Inventory.Snapshot.ReadiedLight);
    }

    // ----- putting a piece on, taking it off --------------------------------

    // A wear or a wield moves the piece from the pack to the worn set. Told of it
    // half-way, the engine saw the piece in both, took the keep amount as covered
    // by the worn copy and sent a discard for the pack copy that was no longer
    // there: the game then took the worn one off and dropped it.
    [Theory]
    [InlineData("gold ring", "You are now wearing gold ring.", true, false)]
    [InlineData("gold ring", "You are now wearing gold ring.", false, false)]
    [InlineData("gold ring", "You are now wearing gold ring.", true, true)]
    [InlineData("dagger", "You are now holding dagger.", true, false)]
    [InlineData("dagger", "You are now holding dagger.", false, false)]
    [InlineData("dagger", "You are now holding dagger.", true, true)]
    public void PuttingOnTheOnlyCopy_WithOneToKeep_DiscardsNothing(string item, string line, bool routerFirst, bool hide)
    {
        using Rig r = new(routerFirst);
        r.Discard.HideMode = hide;
        r.Map(item, 5, discard: true, keep: 1);
        r.FullInventory(item);
        Assert.Empty(r.Sent);

        r.Feed(line);

        Assert.Empty(r.Sent);
        Assert.Equal((0, 1), (r.Carried(item), r.Worn(item)));
    }

    [Fact]
    public void PuttingOnOneOfTwo_WithTwoToKeep_DiscardsNothing()
    {
        using Rig r = new();
        r.Map("gold ring", 5, discard: true, keep: 2);
        r.FullInventory("2 gold ring");

        r.Feed("You are now wearing gold ring.");

        Assert.Empty(r.Sent);
        Assert.Equal((1, 1), (r.Carried("gold ring"), r.Worn("gold ring")));
    }

    [Fact]
    public void TakingOffTheOnlyCopy_WithOneToKeep_DiscardsNothing()
    {
        using Rig r = new();
        r.Map("gold ring", 5, discard: true, keep: 1);
        r.FullInventory("gold ring (Finger)");

        r.Feed("You have removed gold ring.");

        Assert.Empty(r.Sent);
        Assert.Equal((1, 0), (r.Carried("gold ring"), r.Worn("gold ring")));
    }

    // A gear swap between two copies of one piece: off, then on. Neither line
    // changes how many the character holds, and neither sends anything.
    [Fact]
    public void SwappingOneCopyForAnother_DiscardsNothing()
    {
        using Rig r = new();
        r.Map("gold ring", 5, discard: true, keep: 2);
        r.FullInventory("gold ring (Finger), gold ring");

        r.Feed("You have removed gold ring.");
        r.Feed("You are now wearing gold ring.");

        Assert.Empty(r.Sent);
        Assert.Equal((1, 1), (r.Carried("gold ring"), r.Worn("gold ring")));
    }

    // A worn piece coming off while a pile of the same item waits: the keep amount
    // is now the pack's to hold, and the pile still leaves exactly that.
    [Fact]
    public void TakingAPieceOff_WhileItsPileWaits_StillLeavesTheKeepAmount()
    {
        using Rig r = new(pacer: true);
        r.Map("gold ring", 5, discard: true, keep: 1);
        r.FullInventory("gold ring (Finger), 9 gold ring");
        r.Advance(TimeSpan.FromSeconds(1));

        r.Feed("You have removed gold ring.");
        r.Advance(TimeSpan.FromSeconds(1));
        for (int answered = 0; answered < r.Sent.Count; answered++) r.Answer("You dropped gold ring.");

        Assert.Equal(9, r.Sent.Count);
        Assert.Equal(1, r.Carried("gold ring"));
    }

    // ----- a pile and the real pacer ----------------------------------------

    // Two listings asked for before a pile went out land before its first answer.
    // Taken for a quiet stretch they had the pile forgotten, and the first answer
    // then sent it a second time: five daggers, two to keep, none left.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ListingsLandingBeforeTheAnswers_DoNotSendThePileAgain(bool pacer)
    {
        using Rig r = new(pacer: pacer);
        r.Map("dagger", 1, discard: true, keep: 2);
        r.FullInventory("5 dagger");
        r.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, r.Sent.Count);

        r.FullInventory("5 dagger");
        r.FullInventory("5 dagger");
        for (int i = 0; i < 3; i++) r.Answer("You dropped dagger.");

        Assert.Equal(3, r.Sent.Count);
        Assert.Equal(2, r.Carried("dagger"));
    }

    [Fact]
    public void PickupsThenListingsBeforeTheAnswers_DoNotSendThePileAgain()
    {
        using Rig r = new(pacer: true);
        r.Map("dagger", 1, discard: true, keep: 2);
        r.FullInventory("2 dagger");
        for (int i = 0; i < 3; i++) r.Feed("You took dagger.");
        r.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, r.Sent.Count);

        r.FullInventory("5 dagger");
        r.FullInventory("5 dagger");
        for (int answered = 0; answered < r.Sent.Count; answered++) r.Answer("You dropped dagger.");

        Assert.Equal(3, r.Sent.Count);
        Assert.Equal(2, r.Carried("dagger"));
    }

    // A pile that truly got no answer is still let go in the end: once the answers
    // have had their time, two reads with nothing between them forget it.
    [Fact]
    public void PileNeverAnswered_IsForgottenAfterTheSettleTimeAndTwoReads()
    {
        using Rig r = new(pacer: true);
        r.Map("dagger", 1, discard: true, keep: 2);
        r.FullInventory("5 dagger");
        r.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, r.Discard.UnansweredDrops);

        r.Advance(AutoDiscardManager.AnswerSettle);
        r.FullInventory("5 dagger");
        Assert.Equal(3, r.Discard.UnansweredDrops);
        r.FullInventory("5 dagger");

        Assert.Equal(0, r.Discard.UnansweredDrops);
    }

    // Hide mode, a pile of eight, the third refused for want of room: what was on
    // the wire is refused too and held, what was waiting comes back and is held
    // with it, and nothing stays counted as out.
    [Fact]
    public void PacedHidePile_RefusedPartWay_HoldsTheRest()
    {
        using Rig r = new(pacer: true);
        r.Discard.HideMode = true;
        r.Map("dagger", 1, discard: true, keep: 2);
        r.FullInventory("10 dagger");
        r.Advance(TimeSpan.FromSeconds(1));

        r.Answer("You hid dagger.");
        r.Answer("You hid dagger.");
        r.Answer("There is no room to hide dagger here.");
        int onTheWire = r.Sent.Count;
        for (int i = 3; i < onTheWire; i++) r.Answer("There is no room to hide dagger here.");

        Assert.Equal(onTheWire, r.Sent.Count);
        Assert.Equal(8, r.Carried("dagger"));
        Assert.Equal(6, r.Discard.HeldFor("dagger"));
        Assert.Equal(0, r.Discard.UnansweredHides);
    }

    // The character walks into the dark with part of a hide pile still waiting. The
    // waiting hides would get no answer on Stock and stay counted: they are checked
    // as each comes up, and do not go.
    [Fact]
    public void PacedHidePile_StopsWhenSightIsLost()
    {
        using Rig r = new(pacer: true);
        bool canSee = true;
        r.Discard.CanSeeToHide = () => canSee;
        r.Discard.HideMode = true;
        r.Map("dagger", 1, discard: true);
        r.FullInventory("10 dagger");
        r.Advance(TimeSpan.FromSeconds(1));
        int sentWhileSeeing = r.Sent.Count;

        canSee = false;
        for (int i = 0; i < sentWhileSeeing; i++) r.Answer("You hid dagger.");

        Assert.Equal(sentWhileSeeing, r.Sent.Count);
        Assert.Equal(0, r.Pacer!.Pending);
        Assert.Equal(0, r.Discard.UnansweredHides);

        // Sight back: the four that never went are counted afresh and go.
        canSee = true;
        r.Feed("You picked up 3 copper farthings");
        r.Advance(BulkCommandPacer.AnswerTimeout);
        Assert.Equal(10, r.Sent.Count);
    }

    // After a reset the pack is the old one until it is read again. A coin picked up
    // first raises Changed and must not set the engine off; the read itself does.
    [Fact]
    public void AfterAReset_ACoinPickupSendsNothing_AndTheReadEvaluates()
    {
        using Rig r = new(routerFirst: true);
        r.Map("dagger", 1, discard: true);
        r.FullInventory("dagger");
        Assert.Single(r.Sent);

        r.Discard.Reset("disconnected", keepHeld: true);
        r.Feed("You picked up 3 copper farthings");
        Assert.Single(r.Sent);

        r.FullInventory("dagger");                // the drop was lost with the link
        Assert.Equal(2, r.Sent.Count);
    }
}
