using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// AutoDiscardManager: on any inventory change, drops each carried item flagged
// AutoDiscard down to its keep floor — one `drop` per copy on Stock, one counted
// `drop N` on Paradigm. Drops it has sent but not yet seen confirmed are held
// in-flight and subtracted from the live count so the Changed events its own drops
// raise don't re-send; a self "You dropped ..." clears the in-flight count, another
// player's drop is ignored.
public sealed class AutoDiscardManagerTests
{
    // The pack in the shape InventoryManager keeps it: one entry per item, a pile's
    // count in front ("3 dagger"), a single copy bare ("dagger"). Tests feed this
    // shape so they can't pass on one the client never holds.
    private sealed class Pack
    {
        private readonly List<string> _entries = new();

        public IReadOnlyList<string> Entries => _entries;

        public int Count(string name)
        {
            int at = IndexOf(name);
            return at < 0 ? 0 : CountedCommand.SplitLeadingCount(_entries[at]).Count;
        }

        public void Set(string name, int copies)
        {
            int at = IndexOf(name);
            if (copies <= 0) { if (at >= 0) _entries.RemoveAt(at); return; }
            string entry = copies == 1 ? name : $"{copies} {name}";
            if (at < 0) _entries.Add(entry);
            else _entries[at] = entry;
        }

        public void Gain(string name, int copies = 1) => Set(name, Count(name) + copies);

        public void Lose(string name, int copies = 1) => Set(name, Count(name) - copies);

        public void Clear() => _entries.Clear();

        private int IndexOf(string name) => _entries.FindIndex(e =>
            string.Equals(CountedCommand.SplitLeadingCount(e).Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public AutoDiscardManager Discard { get; }
        public List<byte[]> Sent { get; } = new();
        public Pack Carried { get; } = new();
        // What is worn: one name per copy.
        public List<string> Worn { get; } = new();
        // The lit light, as the last full read listed it apart from the pack.
        public string? Lit { get; set; }
        public bool Enabled { get; set; } = true;
        public bool Paradigm { get; set; }
        public DateTimeOffset Now { get; set; } = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        // Copies of an item the trip under way carries for the party, by item Number.
        public Dictionary<int, int> ForJourney { get; } = new();

        // A command waiting in the stand-in pacer: whose it is, and the check its
        // sender wants made just before it goes.
        public sealed record Waiting(string Command, object? Owner, Func<string, bool>? MayGo);

        // The stand-in pacer's queue, and what it has let out.
        public List<Waiting> Queued { get; } = new();
        public List<string> Wire { get; } = new();

        // name -> (Number, Discard, Keep)
        private readonly Dictionary<string, (int Number, bool Discard, int Keep)> _map =
            new(StringComparer.OrdinalIgnoreCase);

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Discard = new AutoDiscardManager(Router,
                carriedItems: () => Carried.Entries,
                resolve: Resolve,
                isEnabled: () => Enabled,
                log: Log,
                isParadigm: () => Paradigm,
                wornItems: () => Worn,
                litLight: () => Lit,
                now: () => Now,
                carriedForJourney: id => ForJourney.GetValueOrDefault(id));
            Discard.SetWireSender(b => Sent.Add(b));
        }

        // Long enough for the game to have answered whatever went out.
        public void Settle() => Now += AutoDiscardManager.AnswerSettle;

        // Stand a pacer in that sends nothing until told to: commands wait in
        // Queued, and each sender can take its own back.
        public void UsePacer()
        {
            Discard.PacedSender = (commands, owner, mayGo) =>
                Queued.AddRange(commands.Select(c => new Waiting(c, owner, mayGo)));
            Discard.SendsQueued = () => Queued.Count > 0;
            Discard.RecallQueued = (owner, take) =>
            {
                List<string> taken = new();
                for (int i = 0; i < Queued.Count;)
                {
                    if (ReferenceEquals(Queued[i].Owner, owner) && take(Queued[i].Command))
                    {
                        taken.Add(Queued[i].Command);
                        Queued.RemoveAt(i);
                    }
                    else i++;
                }
                return taken;
            };
        }

        // The pacer reaches the next `count` waiting commands: each goes out if its
        // sender still wants it to.
        public void Release(int count)
        {
            for (int i = 0; i < count && Queued.Count > 0; i++)
            {
                Waiting next = Queued[0];
                Queued.RemoveAt(0);
                if (next.MayGo is null || next.MayGo(next.Command)) Wire.Add(next.Command);
            }
        }

        public void Map(string name, int number, bool discard, int keep = 0)
            => _map[name] = (number, discard, keep);

        // Like the live resolver, reads past a stack's leading count ("3 moonstone").
        private AutoDiscardManager.ResolvedDiscard? Resolve(string entry)
        {
            string name = CountedCommand.SplitLeadingCount(entry.Trim()).Name;
            return _map.TryGetValue(name, out (int Number, bool Discard, int Keep) v)
                ? new AutoDiscardManager.ResolvedDiscard(v.Number, name, v.Discard, v.Keep)
                : null;
        }

        // The game's "You hid …" as both listeners see it: the router line, and the
        // transaction-history forwarder asking whether the hide was a discard.
        public bool Hid(string token)
        {
            Feed($"You hid {token}.");
            return Discard.TryConsumeSuppressedHide(token);
        }

        public void Feed(string line) => Router.Dispatch(new LineExtractor.EmittedLine(
            line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        public List<string> SentText => Sent
            .Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

        public void Dispose() => Discard.Dispose();
    }

    [Fact]
    public void DuringRoombaSweep_NoDrop_EvenWhenFlagged()
    {
        // A Roomba sweep is sorting the house: auto-discard is held off so it can't
        // bin an item Roomba is relocating. Roomba sorts flagged items itself;
        // normal discard resumes when the sweep ends.
        using Harness h = new();
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 3);
        h.Discard.SuppressDuringSweep = () => true;

        h.Discard.OnInventoryChanged();

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Paradigm_CountedConfirmation_ClearsInFlight_NoResend()
    {
        using Harness h = new() { Paradigm = true };
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 3);

        h.Discard.OnInventoryChanged();
        Assert.Single(h.SentText);                 // one batched "drop 3 dagger"

        // The batched confirmation clears all three in-flight at once, so the
        // Changed re-evaluation it triggers doesn't re-drop.
        h.Feed("You dropped 3 dagger.");
        h.Carried.Clear();                          // dropped items gone from the pack
        h.Discard.OnInventoryChanged();

        Assert.Single(h.SentText);                 // still just the one command
    }

    // A rope bought for a party member is kept until the trip hands it over, over a
    // keep amount of 1 (report paradigm-20261016-031735).
    [Fact]
    public void KeepFloor_RaisedByWhatTheTripCarriesForTheParty()
    {
        using Harness h = new();
        h.Map("rope and grapple", 191, discard: true, keep: 1);
        h.Carried.Set("rope and grapple", 2);
        h.ForJourney[191] = 2;

        h.Discard.OnInventoryChanged();
        Assert.Empty(h.SentText);

        h.ForJourney.Clear();                // handed over: the trip's count is gone
        h.Discard.OnInventoryChanged();
        Assert.Single(h.SentText);
    }

    // The engine counts copies, not pack entries: a pile is one entry however many
    // it holds, and the keep amount is taken off the copies.
    [Theory]
    [InlineData(1, 0, 1)]
    [InlineData(3, 0, 3)]
    [InlineData(10, 0, 10)]
    [InlineData(1, 1, 0)]
    [InlineData(3, 1, 2)]
    [InlineData(10, 1, 9)]
    [InlineData(1, 2, 0)]
    [InlineData(3, 2, 1)]
    [InlineData(10, 2, 8)]
    public void Stock_DiscardsAPileDownToTheKeepAmount_OneCommandPerCopy(int pile, int keep, int discarded)
    {
        using Harness h = new();
        h.Map("dagger", 1, discard: true, keep: keep);
        h.Carried.Set("dagger", pile);

        h.Discard.OnInventoryChanged();

        Assert.Equal(discarded, h.SentText.Count);
        Assert.All(h.SentText, s => Assert.Equal("drop dagger", s));
    }

    [Theory]
    [InlineData(1, 0, "drop dagger")]
    [InlineData(3, 0, "drop 3 dagger")]
    [InlineData(10, 0, "drop 10 dagger")]
    [InlineData(1, 1, null)]
    [InlineData(3, 1, "drop 2 dagger")]
    [InlineData(10, 1, "drop 9 dagger")]
    [InlineData(1, 2, null)]
    [InlineData(3, 2, "drop dagger")]
    [InlineData(10, 2, "drop 8 dagger")]
    public void Paradigm_DiscardsAPileDownToTheKeepAmount_InOneCommand(int pile, int keep, string? command)
    {
        using Harness h = new() { Paradigm = true };
        h.Map("dagger", 1, discard: true, keep: keep);
        h.Carried.Set("dagger", pile);

        h.Discard.OnInventoryChanged();

        if (command is null) Assert.Empty(h.Sent);
        else Assert.Equal(command, Assert.Single(h.SentText));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PileGrowingWhileDiscardsAreOut_DiscardsOnlyTheNewCopies(bool paradigm)
    {
        using Harness h = new() { Paradigm = paradigm };
        h.Map("dagger", 1, discard: true, keep: 1);
        h.Carried.Set("dagger", 3);
        h.Discard.OnInventoryChanged();              // two go out, none answered yet

        h.Carried.Gain("dagger", 2);                 // a chest pours two more in
        h.Discard.OnInventoryChanged();
        h.Discard.OnInventoryChanged();

        string[] expected = paradigm
            ? new[] { "drop 2 dagger", "drop 2 dagger" }
            : new[] { "drop dagger", "drop dagger", "drop dagger", "drop dagger" };
        Assert.Equal(expected, h.SentText);

        // All four land: one is left, the keep amount, and nothing more goes out.
        if (paradigm) { h.Feed("You dropped 2 dagger."); h.Feed("You dropped 2 dagger."); }
        else for (int i = 0; i < 4; i++) h.Feed("You dropped dagger.");
        h.Carried.Set("dagger", 1);
        h.Discard.OnInventoryChanged();
        Assert.Equal(expected.Length, h.Sent.Count);
    }

    // One per copy on Stock is a burst: it goes to the pacer the sweeps use, in one
    // batch, not out one copy per inventory change.
    [Fact]
    public void Stock_PileGoesToThePacerInOneBatch()
    {
        using Harness h = new();
        List<IReadOnlyList<string>> batches = new();
        h.Discard.PacedSender = (commands, _, _) => batches.Add(commands);
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 10);

        h.Discard.OnInventoryChanged();
        h.Discard.OnInventoryChanged();

        Assert.Empty(h.Sent);
        Assert.Equal(10, Assert.Single(batches).Count);
    }

    [Fact]
    public void HideMode_PileIsHiddenDownToTheKeepAmount()
    {
        using Harness h = new() { Paradigm = true };
        h.Discard.HideMode = true;
        h.Map("dagger", 1, discard: true, keep: 2);
        h.Carried.Set("dagger", 10);

        h.Discard.OnInventoryChanged();

        Assert.Equal("hide 8 dagger", Assert.Single(h.SentText));
    }

    // ----- worn copies count toward the keep amount -------------------------

    // What may go is pack + worn - keep, and never more than the pack holds: the
    // worn copy is kept by being worn, and is never the one a discard is sent for.
    [Theory]
    [InlineData(1, 1, 1, 1)]   // one worn covers the keep: the spare in the pack goes
    [InlineData(2, 0, 1, 1)]   // nothing worn: one of the two is kept
    [InlineData(2, 1, 1, 2)]
    [InlineData(2, 1, 2, 1)]
    [InlineData(1, 1, 2, 0)]
    [InlineData(1, 2, 1, 1)]   // two worn, keep 1: still only the one in the pack
    [InlineData(1, 1, 0, 1)]
    [InlineData(0, 1, 0, 0)]   // only the worn one: nothing to send for
    public void WornCopies_CountTowardKeep_AndAreNeverDiscarded(int pack, int worn, int keep, int discarded)
    {
        using Harness h = new() { Paradigm = true };
        h.Map("gold ring", 5, discard: true, keep: keep);
        h.Carried.Set("gold ring", pack);
        for (int i = 0; i < worn; i++) h.Worn.Add("gold ring");

        h.Discard.OnInventoryChanged();

        string[] expected = discarded == 0 ? Array.Empty<string>()
            : new[] { discarded == 1 ? "drop gold ring" : $"drop {discarded} gold ring" };
        Assert.Equal(expected, h.SentText);
    }

    // With the spare already gone while the ring was worn, taking the ring off
    // leaves exactly the keep amount in the pack: the piece just removed stays.
    [Fact]
    public void PieceJustTakenOff_IsNotDiscardedAsAnExtra()
    {
        using Harness h = new();
        h.Map("gold ring", 5, discard: true, keep: 1);
        h.Carried.Set("gold ring", 1);
        h.Worn.Add("gold ring");
        h.Discard.OnInventoryChanged();
        Assert.Equal("drop gold ring", Assert.Single(h.SentText));
        h.Feed("You dropped gold ring.");
        h.Carried.Set("gold ring", 0);

        h.Worn.Clear();                       // "You have removed gold ring."
        h.Carried.Set("gold ring", 1);
        h.Discard.OnInventoryChanged();

        Assert.Single(h.Sent);
    }

    // The lit light is a pack copy like any other, though the listing sets it apart:
    // it is counted with the spares, and a flagged light is discarded lit or not.
    // Which copy a drop takes is the game's choice, and it can be the lit one.
    [Theory]
    [InlineData(1, 1, 1)]   // lit + one spare, keep 1: two copies, one goes
    [InlineData(1, 0, 2)]   // keep none: both go, the lit one with them
    [InlineData(2, 2, 1)]
    [InlineData(0, 0, 1)]   // only the lit one, and it is flagged: it goes
    [InlineData(0, 1, 0)]   // only the lit one, one to keep: it stays
    public void LitLight_IsCountedAsAPackCopy_AndIsNotHeldBack(int spares, int keep, int discarded)
    {
        using Harness h = new();
        h.Map("torch", 3, discard: true, keep: keep);
        h.Carried.Set("torch", spares);
        h.Lit = "torch";

        h.Discard.OnInventoryChanged();

        Assert.Equal(discarded, h.Sent.Count);
        Assert.All(h.SentText, s => Assert.Equal("drop torch", s));
    }

    // ----- a pile still waiting in the pacer --------------------------------

    private static Harness QueuedPileOfTenDaggers(bool paradigm = false)
    {
        Harness h = new() { Paradigm = paradigm };
        h.UsePacer();
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 10);
        h.Discard.OnRoomEntered(RoomA);
        h.Discard.OnInventoryChanged();
        Assert.Equal(paradigm ? 1 : 10, h.Queued.Count);
        return h;
    }

    [Fact]
    public void QueuedPile_IsTakenBack_WhenTheSwitchGoesOff_ButNotWhatHasGoneOut()
    {
        using Harness h = QueuedPileOfTenDaggers();
        h.Queued.Add(new Harness.Waiting("drop moonstone", null, null));   // someone else's waiting command
        h.Release(3);                              // three are on the wire

        h.Enabled = false;
        h.Discard.OnRulesChanged();

        Assert.Equal("drop moonstone", Assert.Single(h.Queued).Command);
        Assert.Equal(3, h.Discard.UnansweredDrops);
    }

    [Fact]
    public void QueuedPile_IsTakenBack_WhenARoombaSweepStarts()
    {
        using Harness h = QueuedPileOfTenDaggers();

        h.Discard.SuppressDuringSweep = () => true;
        h.Discard.OnInventoryChanged();

        Assert.Empty(h.Queued);
        Assert.Equal(0, h.Discard.UnansweredDrops);
    }

    [Fact]
    public void QueuedPile_IsTakenBack_WhenTheFlagIsUnticked()
    {
        using Harness h = QueuedPileOfTenDaggers();

        h.Map("dagger", 1, discard: false);
        h.Discard.OnRulesChanged();

        Assert.Empty(h.Queued);
        Assert.Equal(0, h.Discard.UnansweredDrops);
    }

    [Fact]
    public void QueuedPile_GivesBackOnlyWhatARaisedKeepAmountAsksFor()
    {
        using Harness h = QueuedPileOfTenDaggers();

        h.Map("dagger", 1, discard: true, keep: 2);
        h.Discard.OnRulesChanged();

        Assert.Equal(8, h.Queued.Count);
        Assert.Equal(8, h.Discard.UnansweredDrops);
    }

    // Paradigm's pile is one counted command: it can't be taken back in part, so it
    // comes back whole and what the rules still ask for goes out again at once, with
    // no change of the pack to wait for.
    [Fact]
    public void QueuedCountedCommand_ComesBackWhole_AndIsResentForTheNewKeepAmount()
    {
        using Harness h = QueuedPileOfTenDaggers(paradigm: true);
        Assert.Equal("drop 10 dagger", h.Queued[0].Command);

        h.Map("dagger", 1, discard: true, keep: 2);
        h.Discard.OnRulesChanged();

        Assert.Equal("drop 8 dagger", Assert.Single(h.Queued).Command);
        Assert.Equal(8, h.Discard.UnansweredDrops);
    }

    // The same when nothing tells the engine the rule changed: the pacer asks before
    // the command goes, and the right one takes its place.
    [Fact]
    public void QueuedCountedCommand_IsCheckedAgainWhenItsTurnComes()
    {
        using Harness h = QueuedPileOfTenDaggers(paradigm: true);
        h.Map("dagger", 1, discard: true, keep: 2);

        h.Release(1);

        Assert.Empty(h.Wire);
        Assert.Equal("drop 8 dagger", Assert.Single(h.Queued).Command);
        h.Release(1);
        Assert.Equal(new[] { "drop 8 dagger" }, h.Wire);
        Assert.Equal(8, h.Discard.UnansweredDrops);
    }

    // The gates are checked when each command goes, not only when the pile was
    // queued: sight lost, the send gate up, the switch off or a sweep started while
    // commands wait, and the rest come back uncounted instead of going out blind.
    [Theory]
    [InlineData("sight")]
    [InlineData("gate")]
    [InlineData("switch")]
    [InlineData("sweep")]
    public void QueuedHidePile_IsNotSentOnceAGateCloses(string gate)
    {
        using Harness h = new();
        h.UsePacer();
        h.Discard.HideMode = true;
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 10);
        h.Discard.OnRoomEntered(RoomA);
        h.Discard.OnInventoryChanged();
        h.Release(6);
        Assert.Equal(6, h.Wire.Count);

        switch (gate)
        {
            case "sight": h.Discard.CanSeeToHide = () => false; break;
            case "gate": h.Discard.SendGateOpen = () => false; break;
            case "switch": h.Enabled = false; break;
            default: h.Discard.SuppressDuringSweep = () => true; break;
        }
        h.Release(4);

        Assert.Equal(6, h.Wire.Count);
        Assert.Empty(h.Queued);
        Assert.Equal(6, h.Discard.UnansweredHides);
        // No ledger claim outlives the four that never went.
        for (int i = 0; i < 6; i++) Assert.True(h.Hid("dagger"));
        Assert.False(h.Discard.TryConsumeSuppressedHide("dagger"));
    }

    // A held hide's retry waits in the pacer too, and is checked the same way: one
    // that can no longer be answered goes back to being held, not out.
    [Fact]
    public void QueuedRetry_IsHeldAgain_WhenSightIsLostBeforeItGoes()
    {
        using Harness h = new();
        h.UsePacer();
        h.Discard.HideMode = true;
        h.Map("moonstone", 7, discard: false);
        h.Carried.Set("moonstone", 2);
        h.Discard.OnRoomEntered(RoomA);
        List<string> byHand = new();
        h.Discard.EmitDiscard(byHand.Add, "moonstone", 2);
        h.Feed("There is no room to hide moonstone here.");
        h.Feed("There is no room to hide moonstone here.");
        h.Discard.OnRoomEntered(RoomB);
        Assert.Equal(2, h.Queued.Count);

        h.Discard.CanSeeToHide = () => false;
        h.Release(2);

        Assert.Empty(h.Wire);
        Assert.Empty(h.Queued);
        Assert.Equal(0, h.Discard.UnansweredHides);
        Assert.Equal(2, h.Discard.HeldFor("moonstone"));

        // Sight back in the same room: nothing here has refused them.
        h.Discard.CanSeeToHide = () => true;
        h.Discard.OnInventoryChanged();
        Assert.Equal(2, h.Queued.Count);
    }

    [Fact]
    public void QueuedPile_IsLeftAlone_WhileTheRulesStillAskForIt()
    {
        using Harness h = QueuedPileOfTenDaggers();
        h.Release(4);
        for (int i = 0; i < 4; i++)
        {
            h.Feed("You dropped dagger.");
            h.Carried.Lose("dagger");
            h.Discard.OnInventoryChanged();
        }

        Assert.Equal(6, h.Queued.Count);
        Assert.Equal(6, h.Discard.UnansweredDrops);
    }

    // The room's floor fills after four of ten. The rest of the pile would be
    // refused one command at a time: it comes back, the count ends right, and the
    // item is not dropped in that room again.
    [Fact]
    public void RefusalMidPile_TakesTheRestBack_AndDoesNotResendInThatRoom()
    {
        using Harness h = QueuedPileOfTenDaggers();
        h.Release(6);                              // six on the wire, four waiting
        for (int i = 0; i < 4; i++) { h.Feed("You dropped dagger."); h.Carried.Lose("dagger"); }

        h.Feed("There is no room to drop dagger here.");
        Assert.Empty(h.Queued);
        Assert.Equal(1, h.Discard.UnansweredDrops);    // the sixth, not answered yet
        h.Feed("There is no room to drop dagger here.");
        Assert.Equal(0, h.Discard.UnansweredDrops);

        h.Discard.OnInventoryChanged();
        Assert.Empty(h.Queued);

        h.Discard.OnRoomEntered(RoomB);
        Assert.Equal(6, h.Queued.Count);
    }

    [Fact]
    public void HideRefusalMidPile_HoldsTheRefusedCopyAndTheUnsentRest()
    {
        using Harness h = new();
        h.UsePacer();
        h.Discard.HideMode = true;
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 5);
        h.Discard.OnRoomEntered(RoomA);
        h.Discard.OnInventoryChanged();
        h.Release(2);
        h.Hid("dagger");
        h.Carried.Lose("dagger");

        h.Feed("There is no room to hide dagger here.");

        Assert.Empty(h.Queued);
        Assert.Equal(0, h.Discard.UnansweredHides);
        Assert.Equal(4, h.Discard.HeldFor("dagger"));
        Assert.False(h.Discard.TryConsumeSuppressedHide("dagger"));   // no ledger claim left over

        h.Discard.OnRoomEntered(RoomB);
        Assert.Equal(4, h.Queued.Count);
    }

    [Fact]
    public void UnflaggedItem_NoDrop()
    {
        using Harness h = new();
        h.Map("dagger", 1, discard: false);
        h.Carried.Set("dagger", 1);

        h.Discard.OnInventoryChanged();

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void DisabledMaster_NoDrop()
    {
        using Harness h = new() { Enabled = false };
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 1);

        h.Discard.OnInventoryChanged();

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void InFlight_SuppressesResendUntilConfirmed()
    {
        using Harness h = new();
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 3);

        h.Discard.OnInventoryChanged();      // sends 3 drops
        Assert.Equal(3, h.SentText.Count);

        // The three drops each raise a Changed while the server still reports the
        // copies carried — the in-flight guard must not re-send them.
        h.Discard.OnInventoryChanged();
        Assert.Equal(3, h.SentText.Count);
    }

    [Fact]
    public void SelfConfirmations_ClearInFlight_AllowFreshDrops()
    {
        using Harness h = new();
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 3);

        h.Discard.OnInventoryChanged();      // 3 drops, in-flight = 3
        h.Feed("You dropped dagger.");
        h.Feed("You dropped dagger.");
        h.Feed("You dropped dagger.");       // in-flight cleared

        // A fresh chest dump pours in two more; with in-flight clear they drop.
        h.Carried.Clear();
        h.Carried.Set("dagger", 2);
        h.Discard.OnInventoryChanged();

        Assert.Equal(5, h.SentText.Count);   // 3 + 2
    }

    [Fact]
    public void OtherPlayerDrop_DoesNotClearInFlight()
    {
        using Harness h = new();
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 1);

        h.Discard.OnInventoryChanged();      // 1 drop, in-flight = 1
        Assert.Single(h.Sent);

        h.Feed("Bob drops dagger.");          // another player's drop — ignored

        // In-flight still 1, so the same carried copy is not re-dropped.
        h.Discard.OnInventoryChanged();
        Assert.Single(h.Sent);
    }

    [Fact]
    public void HideMode_OffloadsWithHideNotDrop()
    {
        using Harness h = new();
        h.Discard.HideMode = true;
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 2);

        h.Discard.OnInventoryChanged();

        Assert.Equal(2, h.SentText.Count);
        Assert.All(h.SentText, s => Assert.Equal("hide dagger", s));
    }

    [Fact]
    public void HideMode_SelfHideConfirmation_ClearsInFlight()
    {
        using Harness h = new();
        h.Discard.HideMode = true;
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 2);

        h.Discard.OnInventoryChanged();      // 2 hides, in-flight = 2
        h.Feed("You hid dagger.");
        h.Feed("You hid dagger.");           // in-flight cleared

        // A fresh copy pours in; with in-flight clear it hides.
        h.Carried.Clear();
        h.Carried.Set("dagger", 1);
        h.Discard.OnInventoryChanged();

        Assert.Equal(3, h.SentText.Count);   // 2 + 1
    }

    [Fact]
    public void TryConsumeSuppressedHide_ClaimsEngineHideOncePerCopy()
    {
        using Harness h = new();
        h.Discard.HideMode = true;
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 2);

        h.Discard.OnInventoryChanged();      // 2 engine hides registered

        // The transaction-log forwarder claims each engine hide once — those two
        // are discards, not stashes, so they're kept out of the ledger.
        Assert.True(h.Discard.TryConsumeSuppressedHide("dagger"));
        Assert.True(h.Discard.TryConsumeSuppressedHide("dagger"));
        // A third hide of the same item was never an engine offload — it logs.
        Assert.False(h.Discard.TryConsumeSuppressedHide("dagger"));
    }

    [Fact]
    public void TryConsumeSuppressedHide_ManualHide_NotClaimed()
    {
        using Harness h = new();
        h.Map("dagger", 1, discard: true);

        // No engine offload has run, so a manual / stash-room hide is never
        // registered — the forwarder still records it.
        Assert.False(h.Discard.TryConsumeSuppressedHide("dagger"));
    }

    [Fact]
    public void DropMode_DoesNotRegisterSuppressedHides()
    {
        using Harness h = new();   // HideMode defaults off
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 1);

        h.Discard.OnInventoryChanged();      // sends `drop dagger`

        // Drop-mode offloads aren't hides, so nothing is suppressed from the log.
        Assert.False(h.Discard.TryConsumeSuppressedHide("dagger"));
    }

    // ----- discards sent by hand (Chest Offload's Drop buttons) ----------

    private static readonly RoomKey RoomA = new(1, 100);
    private static readonly RoomKey RoomB = new(1, 101);
    private static readonly RoomKey RoomC = new(1, 102);

    // Hide mode on, standing in RoomA, carrying `copies` moonstones that are not
    // flagged for auto-discard (chest loot never is).
    private static Harness HidingMoonstones(int copies)
    {
        Harness h = new();
        h.Discard.HideMode = true;
        h.Map("moonstone", 7, discard: false);
        h.Carried.Set("moonstone", copies);
        h.Discard.OnRoomEntered(RoomA);
        return h;
    }

    [Fact]
    public void EmitDiscard_DropMode_SendsDropPerCopy()
    {
        using Harness h = new();   // HideMode defaults off
        h.Map("moonstone", 7, discard: false);
        h.Carried.Set("moonstone", 2);
        List<string> sent = new();

        (string verb, int count, _, _) = h.Discard.EmitDiscard(sent.Add, "moonstone", 2);

        Assert.Equal(("drop", 2), (verb, count));
        Assert.Equal(new[] { "drop moonstone", "drop moonstone" }, sent);
        Assert.False(h.Discard.TryConsumeSuppressedHide("moonstone"));
    }

    [Fact]
    public void EmitDiscard_HideMode_SendsHide_ForAnItemNotFlaggedForDiscard()
    {
        // The reported miss: a Drop from Chest Offload went out as `drop` with
        // "Hide items when discarding" ticked. Chest loot isn't flagged for
        // auto-discard, and the engine's own switch can be off: neither is part of
        // the hide-or-drop choice.
        using Harness h = HidingMoonstones(2);
        h.Enabled = false;
        List<string> sent = new();

        (string verb, int count, _, _) = h.Discard.EmitDiscard(sent.Add, "moonstone", 2);

        Assert.Equal(("hide", 2), (verb, count));
        Assert.Equal(new[] { "hide moonstone", "hide moonstone" }, sent);
        Assert.Empty(h.Sent);   // nothing goes out on the engine's own sender
    }

    [Fact]
    public void EmitDiscard_HideMode_Paradigm_SendsOneCountedHide()
    {
        using Harness h = new();
        h.Paradigm = true;
        h.Discard.HideMode = true;
        h.Map("moonstone", 7, discard: false);
        h.Carried.Set("moonstone", 3);
        List<string> sent = new();

        h.Discard.EmitDiscard(sent.Add, "moonstone", 3);

        Assert.Equal(new[] { "hide 3 moonstone" }, sent);
        // Paradigm confirms the pile in one counted line; it claims all three.
        Assert.True(h.Hid("3 moonstone"));
        Assert.False(h.Discard.TryConsumeSuppressedHide("moonstone"));
    }

    [Fact]
    public void EmitDiscard_NoName_SendsNothing()
    {
        // A bare `hide` would hide the character.
        using Harness h = HidingMoonstones(1);
        List<string> sent = new();

        Assert.Equal(0, h.Discard.EmitDiscard(sent.Add, " ", 1).Sent);
        Assert.Empty(sent);
    }

    // Stock answers a hide for an item that isn't held with nothing at all, and the
    // line is then taken for speech. So a second Drop before the first is answered,
    // or a Drop for more copies than are carried, must not reach the wire.
    [Fact]
    public void EmitDiscard_SecondPressBeforeTheAnswer_SendsNothing()
    {
        using Harness h = HidingMoonstones(2);
        List<string> sent = new();

        Assert.Equal(2, h.Discard.EmitDiscard(sent.Add, "moonstone", 2).Sent);
        Assert.Equal(0, h.Discard.EmitDiscard(sent.Add, "moonstone", 2).Sent);

        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public void EmitDiscard_SendsOnlyForCopiesCarried()
    {
        using Harness h = HidingMoonstones(1);
        List<string> sent = new();

        Assert.Equal(1, h.Discard.EmitDiscard(sent.Add, "moonstone", 3).Sent);

        h.Hid("moonstone");
        h.Carried.Clear();
        Assert.Equal(0, h.Discard.EmitDiscard(sent.Add, "moonstone", 3).Sent);
        Assert.Equal(new[] { "hide moonstone" }, sent);
    }

    // A refused drop comes off the count with its refusal line, so the room that
    // refused it doesn't block the next press: that is the player's to try.
    [Fact]
    public void EmitDiscard_DropMode_ARefusedDropFreesTheNextPress()
    {
        using Harness h = new();
        h.Map("moonstone", 7, discard: false);
        h.Carried.Set("moonstone", 1);
        List<string> sent = new();

        h.Discard.EmitDiscard(sent.Add, "moonstone", 1);
        h.Feed("There is no room to drop moonstone here.");
        h.Discard.EmitDiscard(sent.Add, "moonstone", 1);

        Assert.Equal(new[] { "drop moonstone", "drop moonstone" }, sent);
    }

    // With the engine's switch off a by-hand drop is the only thing out for those
    // copies. Uncounted, the engine took them as still to discard the moment it
    // was switched on, and sent again on the first confirmation.
    [Fact]
    public void ByHandDrop_IsSpokenFor_WhenTheEngineComesOn()
    {
        using Harness h = new() { Enabled = false };
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 2);
        List<string> byHand = new();
        h.Discard.EmitDiscard(byHand.Add, "dagger", 2);
        Assert.Equal(2, byHand.Count);
        Assert.Equal(0, h.Discard.EmitDiscard(byHand.Add, "dagger", 2).Sent);   // a second press

        h.Enabled = true;
        h.Discard.OnInventoryChanged();
        h.Feed("You dropped dagger.");
        h.Carried.Lose("dagger");
        h.Discard.OnInventoryChanged();

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void StockHideModePile_FromChestOffload_IsOneHidePerCopy()
    {
        using Harness h = new();
        h.Discard.HideMode = true;
        h.Map("moonstone", 7, discard: false);
        h.Carried.Set("moonstone", 3);   // the pack lists a pile as one entry
        List<string> sent = new();

        Assert.Equal(3, h.Discard.EmitDiscard(sent.Add, "moonstone", 3).Sent);

        Assert.Equal(new[] { "hide moonstone", "hide moonstone", "hide moonstone" }, sent);
        Assert.Equal(3, h.Discard.UnansweredHides);
    }

    // ----- drops that are refused or never answered -------------------------

    [Fact]
    public void RefusedDrops_ComeOffTheCount_AndAreNotSentAgainInThatRoom()
    {
        using Harness h = new();
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 3);
        h.Discard.OnRoomEntered(RoomA);
        h.Discard.OnInventoryChanged();
        Assert.Equal(3, h.Discard.UnansweredDrops);

        for (int i = 0; i < 3; i++) h.Feed("There is no room to drop dagger here.");
        Assert.Equal(0, h.Discard.UnansweredDrops);
        h.Discard.OnInventoryChanged();
        Assert.Equal(3, h.Sent.Count);            // this room's floor is full

        h.Discard.OnRoomEntered(RoomB);
        Assert.Equal(6, h.Sent.Count);
    }

    [Fact]
    public void RefusedDropOfSomeoneElses_IsLeftAlone()
    {
        using Harness h = new();
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 1);
        h.Discard.OnRoomEntered(RoomA);

        h.Feed("There is no room to drop dagger here.");   // typed, or Roomba's
        h.Discard.OnInventoryChanged();

        Assert.Single(h.Sent);
    }

    // Paradigm answers a counted drop for as many as it took. The rest stayed
    // counted for the session, and the item was never discarded again.
    [Fact]
    public void CountedDropAnsweredForFewer_IsForgottenOnceReadsGoQuiet()
    {
        using Harness h = new() { Paradigm = true };
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 8);
        h.Discard.OnInventoryChanged();
        Assert.Equal("drop 8 dagger", Assert.Single(h.SentText));

        h.Feed("You dropped 5 dagger.");
        h.Carried.Lose("dagger", 5);
        h.Discard.OnInventoryChanged();
        Assert.Equal(3, h.Discard.UnansweredDrops);
        Assert.Single(h.Sent);

        h.Settle();
        h.Discard.OnFullInventoryRead();
        h.Discard.OnFullInventoryRead();
        Assert.Equal(0, h.Discard.UnansweredDrops);
        h.Discard.OnInventoryChanged();
        Assert.Equal(new[] { "drop 8 dagger", "drop 3 dagger" }, h.SentText);
    }

    [Fact]
    public void FullInventoryRead_CapsDropsOutAtWhatIsCarried()
    {
        using Harness h = new();
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 3);
        h.Discard.OnInventoryChanged();

        h.Carried.Lose("dagger", 2);   // two went with no line seen
        h.Discard.OnFullInventoryRead();

        Assert.Equal(1, h.Discard.UnansweredDrops);
    }

    [Fact]
    public void Drop_IsNotSentWhileTheSendGateIsUp()
    {
        using Harness h = new();
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 1);
        bool open = false;
        h.Discard.SendGateOpen = () => open;
        List<string> byHand = new();

        h.Discard.OnInventoryChanged();
        AutoDiscardManager.DiscardResult pressed = h.Discard.EmitDiscard(byHand.Add, "dagger", 1);
        Assert.Empty(h.Sent);
        Assert.Empty(byHand);
        Assert.NotNull(pressed.Blocked);
        Assert.Equal(0, h.Discard.UnansweredDrops);

        open = true;
        h.Discard.OnInventoryChanged();
        Assert.Equal("drop dagger", Assert.Single(h.SentText));
    }

    // ----- after a reset the pack is stale until it is read ------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AfterAReset_NothingGoesOutUntilThePackIsRead(bool hide)
    {
        using Harness h = new();
        h.Discard.HideMode = hide;
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 2);
        h.Discard.OnInventoryChanged();
        Assert.Equal(2, h.Sent.Count);

        h.Discard.Reset("disconnected", keepHeld: true);
        h.Discard.OnInventoryChanged();           // a coin picked up before the login `i`
        Assert.Equal(2, h.Sent.Count);
        AutoDiscardManager.DiscardResult pressed = h.Discard.EmitDiscard(_ => { }, "dagger", 2);
        Assert.Equal(0, pressed.Sent);
        Assert.Contains("hasn't been read", pressed.Blocked);

        // The read's own Changed came first and was passed over; the read looks.
        h.Discard.OnFullInventoryRead();
        Assert.Equal(4, h.Sent.Count);
        h.Discard.OnInventoryChanged();
        Assert.Equal(4, h.Sent.Count);
    }

    // The retry is on the wire and the row is off the list: a fresh Drop for the
    // same copy must not send a second hide ahead of the answer.
    [Fact]
    public void CallOff_WhileARetryIsOut_AFreshPressSendsNothingUntilItIsAnswered()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");
        h.Discard.OnRoomEntered(RoomB);
        h.Discard.ReleaseHeld("moonstone", int.MaxValue, byHandOnly: true);

        Assert.Equal(0, h.Discard.EmitDiscard(_ => { }, "moonstone", 1).Sent);

        h.Feed("There is no room to hide moonstone here.");   // answered: refused
        Assert.Empty(h.Discard.HeldHides);
        Assert.Equal(1, h.Discard.EmitDiscard(_ => { }, "moonstone", 1).Sent);
    }

    // ----- a hide the room has no room for ---------------------------------

    [Fact]
    public void StockPartial_TwoHidden_OneRefused_HoldsOne_NoLedgerResidue()
    {
        using Harness h = HidingMoonstones(3);
        List<string> refused = new();
        h.Discard.HideRefused += refused.Add;

        h.Discard.EmitDiscard(_ => { }, "moonstone", 3);
        Assert.True(h.Hid("moonstone"));
        Assert.True(h.Hid("moonstone"));
        h.Carried.Lose("moonstone", 2);
        h.Feed("There is no room to hide moonstone here.");

        Assert.Equal(new[] { "moonstone" }, refused);
        Assert.Equal(1, h.Discard.HeldFor("moonstone"));
        Assert.Equal(0, h.Discard.UnansweredHides);
        // The refused hide's ledger claim went with it: a stash of the item now
        // would still record.
        Assert.False(h.Discard.TryConsumeSuppressedHide("moonstone"));
        // The held copy is spoken for: another press in this room sends nothing.
        Assert.Equal(0, h.Discard.EmitDiscard(_ => { }, "moonstone", 1).Sent);
    }

    [Fact]
    public void RefusedHide_IsSentAgainOnceInTheNextRoom_AndSettlesWhenItLands()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");
        Assert.Empty(h.Sent);

        h.Discard.OnRoomEntered(RoomB);
        h.Discard.OnRoomEntered(RoomB);   // the room shown again

        Assert.Equal(new[] { "hide moonstone" }, h.SentText);
        Assert.Equal(1, h.Discard.HeldFor("moonstone"));   // not over until it lands
        Assert.Equal(1, h.Discard.UnansweredHides);

        // It lands: still a discard to the ledger, and nothing is left counted.
        List<string> ended = new();
        h.Discard.HeldHideEnded += ended.Add;
        Assert.True(h.Hid("moonstone"));
        h.Carried.Clear();
        Assert.Equal(0, h.Discard.UnansweredHides);
        Assert.Empty(h.Discard.HeldHides);
        Assert.Equal(new[] { "moonstone" }, ended);
        Assert.Contains(h.Log.Snapshot(), e => e.Message.Contains("held hide of moonstone landed"));
    }

    [Fact]
    public void HeldHide_RefusedAgain_WaitsForTheRoomAfter()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");

        h.Discard.OnRoomEntered(RoomB);
        h.Feed("There is no room to hide moonstone here.");
        Assert.Equal(1, h.Discard.HeldFor("moonstone"));
        Assert.Single(h.Sent);            // one try per room

        h.Discard.OnRoomEntered(RoomC);
        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public void HeldHide_IsNotRetriedOnARedisplayOfTheSameRoom()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");

        h.Discard.OnRoomEntered(RoomA);

        Assert.Empty(h.Sent);
        Assert.Equal(1, h.Discard.HeldFor("moonstone"));
    }

    [Fact]
    public void HeldHide_NoLongerCarried_IsForgotten()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");

        h.Carried.Clear();   // sold, dropped or given away meanwhile
        h.Discard.OnRoomEntered(RoomB);

        Assert.Empty(h.Sent);
        Assert.Equal(0, h.Discard.HeldFor("moonstone"));
    }

    [Fact]
    public void RefusalOfAHideNeverRegistered_HoldsNothing_AndLeavesAnotherItemsClaimAlone()
    {
        using Harness h = HidingMoonstones(1);
        h.Map("ruby", 8, discard: false);
        h.Carried.Set("ruby", 1);
        List<string> refused = new();
        h.Discard.HideRefused += refused.Add;
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);

        h.Feed("There is no room to hide ruby here.");   // a typed hide, or Hide All's

        Assert.Empty(refused);
        Assert.Equal(0, h.Discard.HeldFor("ruby"));
        Assert.Equal(1, h.Discard.UnansweredHides);
        Assert.True(h.Discard.TryConsumeSuppressedHide("moonstone"));
    }

    [Fact]
    public void HeldHide_WaitsOutARoombaSweep()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");

        bool sweeping = true;
        h.Discard.SuppressDuringSweep = () => sweeping;
        h.Discard.OnRoomEntered(RoomB);
        Assert.Empty(h.Sent);
        Assert.Equal(1, h.Discard.HeldFor("moonstone"));

        sweeping = false;
        h.Discard.OnRoomEntered(RoomC);
        Assert.Equal(new[] { "hide moonstone" }, h.SentText);
    }

    // A held hide's retry answers to neither the engine's switch nor the item's
    // flag, since a by-hand hide never did. It is still the client's own doing,
    // so the master switch stops it, and it goes out once the switch is back on.
    [Fact]
    public void HeldHide_WaitsOutTheMasterSwitch()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");

        bool off = true;
        h.Discard.MasterSwitchOff = () => off;
        h.Discard.OnRoomEntered(RoomB);
        Assert.Empty(h.Sent);
        Assert.Equal(1, h.Discard.HeldFor("moonstone"));

        off = false;
        h.Discard.RecheckHeldHides();
        Assert.Equal(new[] { "hide moonstone" }, h.SentText);
    }

    [Fact]
    public void HeldHides_GoOutThroughThePacer_WhenOneIsBound()
    {
        using Harness h = HidingMoonstones(2);
        List<IReadOnlyList<string>> batches = new();
        h.Discard.PacedSender = (commands, _, _) => batches.Add(commands);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 2);
        h.Feed("There is no room to hide moonstone here.");
        h.Feed("There is no room to hide moonstone here.");

        h.Discard.OnRoomEntered(RoomB);

        Assert.Empty(h.Sent);
        Assert.Equal(new[] { "hide moonstone", "hide moonstone" }, Assert.Single(batches));
    }

    [Fact]
    public void HideModeTurnedOff_GivesUpHeldHides()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");

        h.Discard.HideMode = false;
        h.Discard.OnRoomEntered(RoomB);

        Assert.Empty(h.Sent);
        Assert.Empty(h.Discard.HeldHides);
    }

    [Fact]
    public void EngineHide_Refused_WaitsForTheNextRoom()
    {
        using Harness h = new();
        h.Discard.HideMode = true;
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 1);
        h.Discard.OnRoomEntered(RoomA);

        h.Discard.OnInventoryChanged();
        h.Feed("There is no room to hide dagger here.");
        h.Discard.OnInventoryChanged();   // this room has refused it already
        Assert.Single(h.Sent);

        h.Discard.OnRoomEntered(RoomB);
        Assert.Equal(new[] { "hide dagger", "hide dagger" }, h.SentText);

        // Its switch turned off while a copy waits: the engine's hide stays held,
        // and goes out once the switch is back.
        h.Feed("There is no room to hide dagger here.");
        h.Enabled = false;
        h.Discard.OnRoomEntered(RoomC);
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal(1, h.Discard.HeldFor("dagger"));

        h.Enabled = true;
        h.Discard.RecheckHeldHides();
        Assert.Equal(3, h.Sent.Count);
    }

    [Fact]
    public void RetryLands_ThenNothingMoreIsSentInThatRoom()
    {
        using Harness h = new();
        h.Discard.HideMode = true;
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 1);
        h.Discard.OnRoomEntered(RoomA);
        h.Discard.OnInventoryChanged();
        h.Feed("There is no room to hide dagger here.");

        h.Discard.OnRoomEntered(RoomB);
        h.Hid("dagger");
        h.Carried.Clear();
        h.Discard.OnInventoryChanged();
        h.Discard.OnInventoryChanged();

        Assert.Equal(2, h.Sent.Count);   // the first try and the one retry
    }

    // ----- a held hide is for a hide still wanted ---------------------------

    // Two moonstones of the player's own and one from a chest: the chest's hide is
    // refused and held, then the chest's copy goes another way. A retry would hide
    // one of the player's own.
    private static Harness OwnTwoAndAHeldChestMoonstone()
    {
        Harness h = HidingMoonstones(3);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);   // the chest gave one
        h.Feed("There is no room to hide moonstone here.");
        Assert.Equal(1, h.Discard.HeldFor("moonstone"));
        return h;
    }

    [Fact]
    public void HeldHide_IsCalledOff_WhenTheCopyIsSold()
    {
        using Harness h = OwnTwoAndAHeldChestMoonstone();
        List<string> ended = new();
        h.Discard.HeldHideEnded += ended.Add;

        h.Discard.ReleaseHeld("moonstone", 1);   // "You sold moonstone …", by the wiring
        // Called off by the sale itself, before the pack or the room changes.
        Assert.Empty(h.Discard.HeldHides);
        Assert.Equal(new[] { "moonstone" }, ended);

        h.Carried.Lose("moonstone");
        h.Discard.OnRoomEntered(RoomB);

        Assert.Empty(h.Sent);
    }

    [Theory]
    [InlineData("You dropped moonstone.")]
    [InlineData("You hid moonstone.")]     // typed, or Hide All: not a hide sent here
    public void HeldHide_IsCalledOff_WhenTheCopyIsDroppedOrHiddenByHand(string line)
    {
        using Harness h = OwnTwoAndAHeldChestMoonstone();

        h.Feed(line);
        // Called off by the line itself, before the pack or the room changes.
        Assert.Empty(h.Discard.HeldHides);

        h.Carried.Lose("moonstone");
        h.Discard.OnRoomEntered(RoomB);

        Assert.Empty(h.Sent);
    }

    // The inventory announces the copy gone (Changed) before it names the sale, so
    // the hides are looked at again with the hold still in place and the count
    // already down. Kept back on arrival, that look must not send.
    [Fact]
    public void HeldHide_SoldWhileKeptBack_IsNotSentByTheInventoryChangeBeforeTheRelease()
    {
        using Harness h = OwnTwoAndAHeldChestMoonstone();
        bool sweeping = true;
        h.Discard.SuppressDuringSweep = () => sweeping;
        h.Discard.OnRoomEntered(RoomB);           // kept back
        sweeping = false;

        h.Carried.Lose("moonstone");
        h.Discard.OnInventoryChanged();           // Changed, from inside the sale
        h.Discard.ReleaseHeld("moonstone", 1);    // ItemSold, after it

        Assert.Empty(h.Sent);
        Assert.Empty(h.Discard.HeldHides);
    }

    [Fact]
    public void HeldHide_CopyGivenAway_IsNotRetriedIntoThePlayersOwn()
    {
        using Harness h = OwnTwoAndAHeldChestMoonstone();

        h.Carried.Lose("moonstone");                    // "You just gave moonstone to Bob."
        h.Discard.OnInventoryChanged();
        h.Discard.OnRoomEntered(RoomB);

        Assert.Empty(h.Sent);
        Assert.Empty(h.Discard.HeldHides);
    }

    [Fact]
    public void EngineHeldHide_LeavesTheKeepAmount_WhenACopyIsGivenAway()
    {
        using Harness h = new();
        h.Discard.HideMode = true;
        h.Map("dagger", 1, discard: true, keep: 1);
        h.Carried.Set("dagger", 3);
        h.Discard.OnRoomEntered(RoomA);
        h.Discard.OnInventoryChanged();           // two go out, one is kept
        h.Feed("There is no room to hide dagger here.");
        h.Feed("There is no room to hide dagger here.");

        h.Carried.Lose("dagger");                    // one given away: two left
        h.Discard.OnRoomEntered(RoomB);

        Assert.Equal(3, h.Sent.Count);            // one retry, not two
    }

    [Fact]
    public void CallOff_WhileARetryIsOut_ARefusalOfItHoldsNothing()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");
        h.Discard.OnRoomEntered(RoomB);           // the retry is out

        h.Discard.ReleaseHeld("moonstone", int.MaxValue, byHandOnly: true);   // ✕, or a Sell
        Assert.Empty(h.Discard.HeldHides);
        h.Feed("There is no room to hide moonstone here.");
        h.Discard.OnRoomEntered(RoomC);

        Assert.Single(h.Sent);
        Assert.Empty(h.Discard.HeldHides);
    }

    [Fact]
    public void CallOff_WhileARetryIsOut_ALandingIsStillADiscard()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");
        h.Discard.OnRoomEntered(RoomB);

        h.Discard.ReleaseHeld("moonstone", int.MaxValue, byHandOnly: true);

        Assert.True(h.Hid("moonstone"));          // kept out of the stash ledger
    }

    // With its switch off the engine's held copies would wait for good, and still
    // count as spoken for.
    [Fact]
    public void ByHandDiscard_TakesOverAnEngineHold_WhenTheSwitchIsOff()
    {
        using Harness h = new();
        h.Discard.HideMode = true;
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 1);
        h.Discard.OnRoomEntered(RoomA);
        h.Discard.OnInventoryChanged();
        h.Feed("There is no room to hide dagger here.");
        h.Enabled = false;
        List<string> byHand = new();

        (_, int sent, int held, _) = h.Discard.EmitDiscard(byHand.Add, "dagger", 1);

        Assert.Equal((0, 1), (sent, held));       // this room refused it: still waiting
        Assert.Empty(byHand);
        h.Discard.OnRoomEntered(RoomB);           // and now it goes, switch or no switch
        Assert.Equal(new[] { "hide dagger", "hide dagger" }, h.SentText);
    }

    [Fact]
    public void RefusalBeforeTheRoomIsKnown_TreatsTheFirstConfirmedRoomAsTheOneThatRefused()
    {
        using Harness h = new();
        h.Discard.HideMode = true;
        h.Map("moonstone", 7, discard: false);
        h.Carried.Set("moonstone", 1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");

        h.Discard.OnRoomEntered(RoomA);           // where it has been standing all along
        Assert.Empty(h.Sent);

        h.Discard.OnRoomEntered(RoomB);
        Assert.Equal(new[] { "hide moonstone" }, h.SentText);
    }

    [Fact]
    public void ByHandHide_ThatCouldNotBeAnswered_IsHeldInsteadOfSent()
    {
        using Harness h = HidingMoonstones(1);
        bool canHide = false;
        h.Discard.CanSeeToHide = () => canHide;
        List<string> byHand = new();

        (_, int sent, int held, _) = h.Discard.EmitDiscard(byHand.Add, "moonstone", 1);

        Assert.Equal((0, 1), (sent, held));
        Assert.Empty(byHand);
        Assert.Equal(0, h.Discard.UnansweredHides);

        // Nothing refused it, so it needn't wait for another room.
        canHide = true;
        h.Discard.RecheckHeldHides();
        Assert.Equal(new[] { "hide moonstone" }, h.SentText);
    }

    [Fact]
    public void KeptBack_IsLoggedOncePerReasonPerRoom()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");
        h.Discard.SuppressDuringSweep = () => true;

        h.Discard.OnRoomEntered(RoomB);
        h.Discard.OnInventoryChanged();
        h.Discard.OnInventoryChanged();
        h.Discard.OnRoomEntered(RoomC);

        Assert.Equal(2, h.Log.Snapshot().Count(e => e.Message.Contains("held hides kept back")));
    }

    // The by-hand and engine copies of one item through every way a count moves.
    [Fact]
    public void MixedByHandAndEngineCopies_ThroughLandingRefusalReleaseAndRead()
    {
        using Harness h = new();
        h.Discard.HideMode = true;
        h.Map("dagger", 1, discard: true, keep: 1);
        h.Discard.OnRoomEntered(RoomA);
        h.Enabled = false;
        h.Carried.Set("dagger", 4);

        h.Discard.EmitDiscard(_ => { }, "dagger", 1);   // one by hand, the engine switched off
        h.Enabled = true;
        h.Discard.OnInventoryChanged();                 // the engine: 4 - 1 out - keep 1 = 2
        Assert.Equal(3, h.Discard.UnansweredHides);

        h.Hid("dagger");                                // one lands
        h.Carried.Lose("dagger");
        h.Feed("There is no room to hide dagger here.");
        h.Feed("There is no room to hide dagger here.");
        AutoDiscardManager.HeldHideInfo held = Assert.Single(h.Discard.HeldHides);
        Assert.Equal(2, held.Count);
        Assert.Equal(0, h.Discard.UnansweredHides);

        h.Discard.ReleaseHeld("dagger", 1, byHandOnly: true);   // nothing by hand is left held
        Assert.Equal(2, Assert.Single(h.Discard.HeldHides).Engine);

        h.Carried.Lose("dagger");                          // a read finds two
        h.Discard.OnFullInventoryRead();
        Assert.Equal(1, Assert.Single(h.Discard.HeldHides).Count);

        h.Discard.OnRoomEntered(RoomB);                 // 2 carried, keep 1: one goes
        Assert.Equal(3, h.Sent.Count);
    }

    // A recheck and the engine's own evaluation run in the one inventory change.
    [Fact]
    public void RecheckAndEvaluation_InOneCall_SendOnce()
    {
        using Harness h = new();
        h.Discard.HideMode = true;
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 1);
        h.Discard.OnRoomEntered(RoomA);
        h.Discard.OnInventoryChanged();
        h.Feed("There is no room to hide dagger here.");
        bool sweeping = true;
        h.Discard.SuppressDuringSweep = () => sweeping;
        h.Discard.OnRoomEntered(RoomB);
        sweeping = false;

        h.Discard.OnInventoryChanged();

        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public void ReleaseHeld_ByHandOnly_LeavesTheEnginesOwnHold()
    {
        using Harness h = new();
        h.Discard.HideMode = true;
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 1);
        h.Discard.OnRoomEntered(RoomA);
        h.Discard.OnInventoryChanged();
        h.Feed("There is no room to hide dagger here.");

        h.Discard.ReleaseHeld("dagger", int.MaxValue, byHandOnly: true);   // ✕ on its row

        Assert.Equal(1, h.Discard.HeldFor("dagger"));
    }

    // A by-hand discard never answered to the auto-discard switch, whether or not
    // the item happens to be flagged.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ByHandHeldHide_IsRetriedWithTheEngineOff(bool flagged)
    {
        using Harness h = new();
        h.Enabled = false;
        h.Discard.HideMode = true;
        h.Map("moonstone", 7, discard: flagged);
        h.Carried.Set("moonstone", 1);
        h.Discard.OnRoomEntered(RoomA);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");

        h.Discard.OnRoomEntered(RoomB);

        Assert.Equal(new[] { "hide moonstone" }, h.SentText);
    }

    // ----- a hide that would get no answer ----------------------------------

    [Fact]
    public void HeldHide_WaitsWhileAHideCannotBeAnswered()
    {
        // The send gate is up, or the character is blind or in the dark: Stock
        // answers the hide with the can't-see line and hides nothing.
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");

        bool canHide = false;
        h.Discard.CanSeeToHide = () => canHide;
        h.Discard.OnRoomEntered(RoomB);
        Assert.Empty(h.Sent);
        Assert.Equal(1, h.Discard.HeldFor("moonstone"));

        // It clears while still in that room: the next inventory change looks again.
        canHide = true;
        h.Discard.OnInventoryChanged();
        Assert.Equal(new[] { "hide moonstone" }, h.SentText);
    }

    [Fact]
    public void EnginesOwnHide_IsNotSentWhileItCannotBeAnswered()
    {
        using Harness h = new();
        h.Discard.HideMode = true;
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 1);
        bool canHide = false;
        h.Discard.CanSeeToHide = () => canHide;

        h.Discard.OnInventoryChanged();
        Assert.Empty(h.Sent);
        Assert.Equal(0, h.Discard.UnansweredHides);

        canHide = true;
        h.Discard.OnInventoryChanged();
        Assert.Equal(new[] { "hide dagger" }, h.SentText);
    }

    [Fact]
    public void RetryNeverAnswered_IsHeldAgain_NotForgotten()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");
        h.Discard.OnRoomEntered(RoomB);          // the retry goes out and is lost
        Assert.Single(h.Sent);

        h.Settle();
        h.Discard.OnFullInventoryRead();
        h.Discard.OnFullInventoryRead();

        Assert.Equal(0, h.Discard.UnansweredHides);
        Assert.Equal(1, h.Discard.HeldFor("moonstone"));
        h.Discard.OnInventoryChanged();           // still room B, where it went out
        Assert.Single(h.Sent);
        h.Discard.OnRoomEntered(RoomC);
        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public void SweepEnding_RechecksHeldHides_ButNotInTheRoomThatRefused()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");

        h.Discard.RecheckHeldHides();             // still room A
        Assert.Empty(h.Sent);

        bool sweeping = true;
        h.Discard.SuppressDuringSweep = () => sweeping;
        h.Discard.OnRoomEntered(RoomB);
        sweeping = false;
        h.Discard.RecheckHeldHides();             // the sweep ended in room B

        Assert.Equal(new[] { "hide moonstone" }, h.SentText);
    }

    [Fact]
    public void Disconnect_KeepsHeldHides_TiedToTheRoomThatRefused()
    {
        using Harness h = new();
        h.Discard.HideMode = true;
        h.Map("dagger", 1, discard: true);
        h.Carried.Set("dagger", 1);
        h.Discard.OnRoomEntered(RoomA);
        h.Discard.OnInventoryChanged();
        h.Feed("There is no room to hide dagger here.");

        h.Discard.Reset("disconnected", keepHeld: true);
        h.Discard.OnRoomEntered(RoomA);           // back in, the same room
        h.Discard.OnInventoryChanged();
        h.Discard.OnFullInventoryRead();          // the login `i`
        Assert.Single(h.Sent);                    // not sent into the full room again

        h.Discard.OnRoomEntered(RoomB);
        Assert.Equal(2, h.Sent.Count);
    }

    // The carried list is the one from before the drop until the login `i` lands.
    // A hide for a copy lost while away would not be refused but said aloud.
    [Fact]
    public void Reconnect_IntoAnotherRoom_SendsNothingUntilThePackIsRead()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");

        h.Discard.Reset("disconnected", keepHeld: true);
        h.Discard.OnRoomEntered(RoomB);
        h.Discard.OnInventoryChanged();
        Assert.Empty(h.Sent);
        Assert.Contains(h.Log.Snapshot(), e => e.Message.Contains("hasn't been read again yet"));

        h.Discard.OnFullInventoryRead();          // still carried: now it goes
        Assert.Equal(new[] { "hide moonstone" }, h.SentText);
    }

    [Fact]
    public void DisconnectWhileARetryIsOut_ThatHadLanded_DoesNotHideAnOwnCopy()
    {
        using Harness h = OwnTwoAndAHeldChestMoonstone();
        h.Discard.OnRoomEntered(RoomB);           // the retry goes out, and lands unseen
        Assert.Single(h.Sent);

        h.Discard.Reset("disconnected", keepHeld: true);
        h.Carried.Lose("moonstone");                    // the login `i`: two left, the player's own
        h.Discard.OnFullInventoryRead();
        h.Discard.OnRoomEntered(RoomC);

        Assert.Single(h.Sent);
        Assert.Empty(h.Discard.HeldHides);
    }

    [Fact]
    public void HideModeOffThenOn_RestoresNothing_AndALaterHideIsNotALanding()
    {
        using Harness h = HidingMoonstones(1);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);
        h.Feed("There is no room to hide moonstone here.");
        h.Discard.OnRoomEntered(RoomB);           // retry in flight

        h.Discard.HideMode = false;
        h.Discard.HideMode = true;
        h.Hid("moonstone");

        Assert.Empty(h.Discard.HeldHides);
        Assert.DoesNotContain(h.Log.Snapshot(), e => e.Message.Contains("landed"));
    }

    [Fact]
    public void RefusalArrivingWithHideModeOff_HoldsNothing()
    {
        using Harness h = HidingMoonstones(1);
        List<string> refused = new();
        h.Discard.HideRefused += refused.Add;
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);

        h.Discard.HideMode = false;
        h.Feed("There is no room to hide moonstone here.");

        Assert.Empty(refused);
        Assert.Empty(h.Discard.HeldHides);
    }

    // ----- nothing unanswered is counted forever ----------------------------

    [Fact]
    public void FullInventoryRead_ForgetsAHideForACopyNoLongerCarried()
    {
        using Harness h = HidingMoonstones(2);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 2);

        h.Carried.Lose("moonstone");   // the read shows one left
        h.Discard.OnFullInventoryRead();

        Assert.Equal(1, h.Discard.UnansweredHides);
        Assert.True(h.Discard.TryConsumeSuppressedHide("moonstone"));
        Assert.False(h.Discard.TryConsumeSuppressedHide("moonstone"));
    }

    [Fact]
    public void FullInventoryRead_CapsHeldHidesAtWhatIsCarried()
    {
        using Harness h = HidingMoonstones(2);
        List<string> ended = new();
        h.Discard.HeldHideEnded += ended.Add;
        h.Discard.EmitDiscard(_ => { }, "moonstone", 2);
        h.Feed("There is no room to hide moonstone here.");
        h.Feed("There is no room to hide moonstone here.");

        h.Carried.Lose("moonstone");
        h.Discard.OnFullInventoryRead();
        Assert.Equal(1, h.Discard.HeldFor("moonstone"));

        h.Carried.Clear();
        h.Discard.OnFullInventoryRead();
        Assert.Empty(h.Discard.HeldHides);
        Assert.Equal(new[] { "moonstone" }, ended);
    }

    [Fact]
    public void ParadigmPartialEcho_LeavesTheRestCounted_UntilReadsGoQuiet()
    {
        using Harness h = new();
        h.Paradigm = true;
        h.Discard.HideMode = true;
        h.Map("moonstone", 7, discard: false);
        h.Carried.Set("moonstone", 3);
        List<string> sent = new();
        h.Discard.EmitDiscard(sent.Add, "moonstone", 3);

        h.Hid("2 moonstone");
        h.Carried.Set("moonstone", 1);
        Assert.Equal(1, h.Discard.UnansweredHides);

        h.Discard.OnFullInventoryRead();   // too soon after the answer to mean anything
        h.Discard.OnFullInventoryRead();
        Assert.Equal(1, h.Discard.UnansweredHides);

        h.Settle();
        h.Discard.OnFullInventoryRead();
        Assert.Equal(1, h.Discard.UnansweredHides);
        h.Discard.OnFullInventoryRead();
        Assert.Equal(0, h.Discard.UnansweredHides);
        Assert.Equal(1, h.Discard.EmitDiscard(sent.Add, "moonstone", 1).Sent);
    }

    // Commands handed to the pacer are counted at once and sent a few at a time.
    // While some are still queued, quiet reads say nothing about the game.
    [Fact]
    public void QuietInventoryReads_ForgetNothingWhileThePacerStillHoldsCommands()
    {
        using Harness h = HidingMoonstones(3);
        bool queued = true;
        h.Discard.SendsQueued = () => queued;
        h.Discard.EmitDiscard(_ => { }, "moonstone", 3);

        h.Settle();
        h.Discard.OnFullInventoryRead();
        h.Discard.OnFullInventoryRead();
        h.Discard.OnFullInventoryRead();
        Assert.Equal(3, h.Discard.UnansweredHides);
        Assert.Equal(0, h.Discard.EmitDiscard(_ => { }, "moonstone", 3).Sent);

        queued = false;
        h.Discard.OnFullInventoryRead();   // the queue emptied since the last read
        Assert.Equal(3, h.Discard.UnansweredHides);
        h.Discard.OnFullInventoryRead();
        Assert.Equal(0, h.Discard.UnansweredHides);
    }

    [Fact]
    public void QuietInventoryReads_ForgetAHideTheGameNeverAnswered()
    {
        // `You may not hide that item!` names no item, a dark room answers with its
        // own line, a command sent too fast is dropped: none settles the count.
        using Harness h = HidingMoonstones(1);
        List<string> sent = new();
        h.Discard.EmitDiscard(sent.Add, "moonstone", 1);

        h.Settle();
        h.Discard.OnFullInventoryRead();   // the first read after the answer was due
        Assert.Equal(0, h.Discard.EmitDiscard(sent.Add, "moonstone", 1).Sent);

        h.Discard.OnFullInventoryRead();   // nothing sent or answered since the last
        Assert.False(h.Discard.TryConsumeSuppressedHide("moonstone"));
        Assert.Equal(1, h.Discard.EmitDiscard(sent.Add, "moonstone", 1).Sent);
    }

    // Two listings asked for before a pile went out land before its first answer.
    // When a listing was asked for can't be known, so reads that soon after the
    // send say nothing: the pile stays counted and is not sent a second time.
    [Fact]
    public void ReadsLandingBeforeTheAnswers_ForgetNothing_AndThePileIsNotResent()
    {
        using Harness h = new();
        h.Map("dagger", 1, discard: true, keep: 2);
        h.Carried.Set("dagger", 5);
        h.Discard.OnInventoryChanged();
        Assert.Equal(3, h.Sent.Count);

        h.Discard.OnFullInventoryRead();
        h.Discard.OnFullInventoryRead();
        Assert.Equal(3, h.Discard.UnansweredDrops);

        for (int i = 0; i < 3; i++)
        {
            h.Feed("You dropped dagger.");
            h.Carried.Lose("dagger");
            h.Discard.OnInventoryChanged();
        }
        Assert.Equal(3, h.Sent.Count);
        Assert.Equal(2, h.Carried.Count("dagger"));
    }

    [Fact]
    public void InventoryReads_KeepCountingWhileAnswersStillArrive()
    {
        using Harness h = HidingMoonstones(3);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 3);

        h.Discard.OnFullInventoryRead();
        h.Hid("moonstone");                // a paced batch still draining
        h.Carried.Lose("moonstone");
        h.Discard.OnFullInventoryRead();

        Assert.Equal(2, h.Discard.UnansweredHides);
    }

    [Fact]
    public void Reset_ForgetsUnansweredAndHeldHides()
    {
        using Harness h = HidingMoonstones(2);
        h.Discard.EmitDiscard(_ => { }, "moonstone", 2);
        h.Feed("There is no room to hide moonstone here.");

        h.Discard.Reset("death");

        Assert.Equal(0, h.Discard.UnansweredHides);
        Assert.Empty(h.Discard.HeldHides);
        Assert.False(h.Discard.TryConsumeSuppressedHide("moonstone"));
        h.Discard.OnRoomEntered(RoomB);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void RefusedDrop_IsNotAHideRefusal()
    {
        using Harness h = HidingMoonstones(1);
        List<string> refused = new();
        h.Discard.HideRefused += refused.Add;
        h.Discard.EmitDiscard(_ => { }, "moonstone", 1);

        h.Feed("There is no room to drop moonstone here.");

        Assert.Empty(refused);
        Assert.Equal(1, h.Discard.UnansweredHides);
    }
}
