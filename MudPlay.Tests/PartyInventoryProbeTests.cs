using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

public sealed class PartyInventoryProbeTests
{
    // Harness wiring the probe to a real ChatRouter (so replies
    // flow through the same classification path production uses) and a
    // PartyBroadcaster whose wire is captured. The
    // armWindow seam is captured rather than timer-driven, so tests
    // fire the reply window deterministically via FireWindows.
    private sealed class Harness
    {
        public readonly MessageRouter Router;
        public readonly ChatRouter Chat;
        public readonly PartyState State = new();
        public readonly PartyBroadcaster Broadcaster;
        public readonly List<byte[]> Wire = new();
        public readonly List<Action> Windows = new();
        public readonly PartyInventoryProbe Probe;
        public readonly LogService Log = new();

        public Harness()
        {
            Router = new MessageRouter();
            DefaultPatterns.Seed(Router);
            Chat = new ChatRouter(Router);
            Broadcaster = new PartyBroadcaster(State);
            Broadcaster.SetWireSender(Wire.Add);
            Probe = new PartyInventoryProbe(Broadcaster, Chat, State, armWindow: Windows.Add, log: Log);
        }

        public void AddMember(string name, bool self = false, bool invited = false)
            => State.Members.Add(new PartyMember { Name = name, IsSelf = self, IsInvited = invited });

        public void GoInParty() => State.IsInParty = true;

        public void FireWindows()
        {
            Action[] snapshot = Windows.ToArray();
            Windows.Clear();
            foreach (Action w in snapshot) w();
        }

        public void Reply(string sender, string message)
        {
            string line = $"{sender} telepaths: {message}";
            Router.Dispatch(new Terminal.LineExtractor.EmittedLine(
                line, new Terminal.CellAttributes[line.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false));
        }

        public string Sent(int i) => Encoding.Latin1.GetString(Wire[i]);
    }

    [Fact]
    public async Task Query_NoOtherMembers_ReturnsEmptyImmediately()
    {
        var h = new Harness();
        // Only self in the roster — nobody to ask.
        h.AddMember("MudPlay", self: true);
        h.GoInParty();

        PartyInventoryProbe.PartyItemResult r = await h.Probe.QueryAsync(175, "rope");

        Assert.Equal(0, r.Expected);
        Assert.False(r.AnyHeld);
        Assert.Empty(h.Wire);   // no broadcast fired
    }

    // An invited member who hasn't joined can't answer, and waiting on them ran
    // every count to the end of its window.
    [Fact]
    public async Task Query_OnlyInvitedMember_ReturnsEmptyImmediately()
    {
        var h = new Harness();
        h.AddMember("Tristian", invited: true);
        h.GoInParty();

        PartyInventoryProbe.PartyItemResult r = await h.Probe.QueryAsync(175, "rope");

        Assert.Equal(0, r.Expected);
        Assert.Empty(h.Wire);
        Assert.Empty(h.Windows);
    }

    [Fact]
    public async Task Query_InvitedMember_IsNeitherAskedNorWaitedFor()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.AddMember("Tristian", invited: true);
        h.GoInParty();

        Task<PartyInventoryProbe.PartyItemResult> q = h.Probe.QueryAsync(175, "rope");
        Assert.Equal("/Bob @have rope\r", Assert.Single(h.Wire.Select(Encoding.Latin1.GetString)));

        h.Reply("Bob", "yes - 1x 'rope'");            // the one joined member answers
        PartyInventoryProbe.PartyItemResult r = await q.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, r.Expected);                  // done without the window closing
        Assert.Equal(1, r.TotalCount);
    }

    // Every remote-command reply comes back brace-wrapped on the wire; the probe
    // counted none of them, so a party holding ropes was taken for one holding none.
    [Theory]
    [InlineData("{yes - 1x 'rope and grapple'}", 1)]
    [InlineData("{no - nothing matching 'rope and grapple'}", 0)]
    [InlineData("{yes: 1}", 1)]                       // MegaMUD's reply, no item echo
    [InlineData("{Yes: 1.}", 1)]                      // and with its full stop (paradigm-20261019-041600)
    public async Task BraceWrappedReplies_AreCounted(string reply, int held)
    {
        var h = new Harness();
        h.AddMember("Placido");
        h.GoInParty();

        Task<PartyInventoryProbe.PartyItemResult> q = h.Probe.QueryAsync(191, "rope and grapple");
        h.Reply("Placido", reply);
        PartyInventoryProbe.PartyItemResult r = await q.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, r.Replied);
        Assert.Equal(held, r.TotalCount);
        Assert.Empty(r.Unanswered);
    }

    [Fact]
    public async Task CountOnlyReplies_AreCreditedToTheQueriesInTheOrderAsked()
    {
        var h = new Harness();
        h.AddMember("Gronx");
        h.GoInParty();

        Task<PartyInventoryProbe.PartyItemResult> rope = h.Probe.QueryAsync(191, "rope and grapple");
        Task<PartyInventoryProbe.PartyItemResult> torch = h.Probe.QueryAsync(52, "torch");
        h.Reply("Gronx", "{yes: 2}");
        h.Reply("Gronx", "{yes: 5}");

        Assert.Equal(2, (await rope.WaitAsync(TimeSpan.FromSeconds(5))).TotalCount);
        Assert.Equal(5, (await torch.WaitAsync(TimeSpan.FromSeconds(5))).TotalCount);
    }

    // The walk a count holds is let go when the probe's reply window closes on a
    // member who never answers: the one release that rests on the probe's own timer.
    [Fact]
    public async Task WindowClosing_OnASilentMember_ReleasesTheWalkItsCountHeld()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.GoInParty();
        bool held = false;
        List<(int Id, int Qty)> forwarded = new();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new Game.Map.PartyPathItemGate(
            isCarried: _ => false,
            selfCount: _ => 0,
            query: (id, name) => h.Probe.QueryAsync(id, name),
            itemName: _ => "rope",
            isEnabled: _ => true,
            perPersonQuantity: _ => 1,
            searchEnabled: () => false,
            inParty: () => true,
            selfIsLeader: () => true,
            selfGivenName: () => "MudPlay",
            forward: (ids, qty) => forwarded.Add((ids[0], qty)),
            post: a => a(),
            holdWalk: _ => held = true,
            releaseWalk: _ => { held = false; released.TrySetResult(); });

        gate.OnPathItemsRequired(new[] { 175 });
        Assert.True(held);
        Assert.Single(h.Windows);

        h.FireWindows();                              // nobody answered in time
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(held);
        // Bob never answered, so he is taken to hold none: a copy each for the two.
        Assert.Equal((175, 2), Assert.Single(forwarded));
    }

    // Report paradigm-20261019-041600: the leader holds a mine pass, both members
    // answer `{Yes: 1.}`, and the client still bought two at the Miner's Guild. The
    // full stop kept either answer from being read, so both were counted as holding
    // none. Fed as the game sent it, prompt and cursor moves included.
    [Fact]
    public async Task BothMembersAnsweringYesWithAFullStop_NothingIsBought()
    {
        var h = new Harness();
        var feed = new ServerFeed(h.Router);
        h.AddMember("Raijin", self: true);
        h.AddMember("Boost Pressure");
        h.AddMember("Fujin");
        h.GoInParty();
        List<(int Id, int Qty)> forwarded = new();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new Game.Map.PartyPathItemGate(
            isCarried: _ => true,
            selfCount: _ => 1,
            query: (id, name) => h.Probe.QueryAsync(id, name),
            itemName: _ => "mine pass",
            isEnabled: _ => true,
            perPersonQuantity: _ => 1,
            searchEnabled: () => false,
            inParty: () => true,
            selfIsLeader: () => true,
            selfGivenName: () => "Raijin",
            forward: (ids, qty) => forwarded.Add((ids[0], qty)),
            post: a => a(),
            holdWalk: _ => { },
            releaseWalk: _ => released.TrySetResult());
        List<string> gateWire = new();
        gate.SetWireSender(b => gateWire.Add(Encoding.Latin1.GetString(b)));

        gate.OnPathItemsRequired(new[] { 942 });
        Assert.Equal(new[] { "/Boost @have mine pass\r", "/Fujin @have mine pass\r" },
                     h.Wire.Select(Encoding.Latin1.GetString));
        const string Prompt = "\u001b[79D\u001b[K\u001b[0;37m[HP=\u001b[0;37m456\u001b[0;37m/MA=\u001b[0;37m164\u001b[0;37m]:\u001b[0m\u001b[79D\u001b[K";
        feed.Raw(Prompt + "\u001b[0;32mBoost telepaths: \u001b[0;37m{Yes: 1.}\r\n");
        feed.Raw(Prompt + "\u001b[0;32mFujin telepaths: \u001b[0;37m{Yes: 1.}\r\n");
        h.FireWindows();                              // the 4 s window closes behind the answers
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(forwarded);                      // three passes for three: none to buy
        Assert.Empty(gateWire);                       // and nobody short to hand one to
    }

    // A member's answer the probe can't read is quoted in the program log, and a
    // bug report must mask chat there like anywhere else: the line is written in
    // the `X telepaths: …` shape the censor knows.
    [Fact]
    public void AnUnreadAnswer_IsLogged_AndCensoredInTheBugReport()
    {
        var h = new Harness();
        h.AddMember("Boost Pressure");
        h.GoInParty();

        _ = h.Probe.QueryAsync(942, "mine pass");
        h.Reply("Boost", "{you flibber}");

        BugReportBuilder.Section section =
            BugReportLogSections.ProgramLog(h.Log, StandInCensor.Of("flibber"));
        string logged = Assert.Single(section.Entries!, e => e.Contains("telepaths:"));
        Assert.Contains("[censored]", logged);
        Assert.DoesNotContain("flibber", logged);
    }

    // A count too big for an int used to throw out of the chat handler and cancel
    // the rest of the line's handlers. It is unread instead: logged, not counted.
    [Theory]
    [InlineData("{yes: 99999999999}")]
    [InlineData("{yes - 99999999999x 'rope'}")]
    public async Task ACountTooBigToRead_IsLoggedAndNotCounted(string reply)
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.GoInParty();

        Task<PartyInventoryProbe.PartyItemResult> q = h.Probe.QueryAsync(175, "rope");
        h.Reply("Bob", reply);
        h.FireWindows();
        PartyInventoryProbe.PartyItemResult r = await q.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, r.Replied);
        Assert.Equal(new[] { "Bob" }, r.Unanswered);
        Assert.Contains(h.Log.Snapshot(), e => e.Message.Contains("not read as a count"));
    }

    // The members who were asked and didn't answer are named, so the route card
    // and the walk read the same party from one count. An invitee was never asked.
    [Fact]
    public async Task WindowClosing_NamesTheMembersWhoDidNotAnswer()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.AddMember("Sil");
        h.AddMember("Tristian", invited: true);
        h.GoInParty();

        Task<PartyInventoryProbe.PartyItemResult> q = h.Probe.QueryAsync(175, "rope");
        h.Reply("Bob", "yes - 1x 'rope'");
        h.FireWindows();
        PartyInventoryProbe.PartyItemResult r = await q.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, r.Expected);
        Assert.Equal(1, r.Replied);
        Assert.Equal(new[] { "Sil" }, r.Unanswered);
        Assert.Equal(new[] { "Bob" }, r.CountsByMember.Keys);
    }

    // A member whose client turns the count down answers nothing the count can read,
    // so they're unanswered. They alone are short when everyone who answered holds
    // one: the copies a leader can only fetch, and asks about walking on without
    // when it can't.
    [Theory]
    [InlineData("yes - 1x 'rope'", true)]
    [InlineData("no - nothing matching 'rope'", false)]
    public async Task AMemberWhoRefusesTheCount_IsAloneShort_OnlyWhenEveryoneWhoAnsweredHoldsOne(
        string bobsAnswer, bool riaAloneShort)
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.AddMember("Ria");
        h.GoInParty();

        Task<PartyInventoryProbe.PartyItemResult> q = h.Probe.QueryAsync(175, "rope");
        h.Reply("Bob", bobsAnswer);
        h.Reply("Ria", "{command invalid or not allowed}");
        h.FireWindows();
        PartyInventoryProbe.PartyItemResult r = await q.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "Ria" }, r.Unanswered);
        Assert.Equal(riaAloneShort ? new[] { "Ria" } : Array.Empty<string>(), r.SilentAloneShort());
    }

    [Fact]
    public async Task EveryoneAnswering_LeavesNobodyUnanswered()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.GoInParty();

        Task<PartyInventoryProbe.PartyItemResult> q = h.Probe.QueryAsync(175, "rope");
        h.Reply("Bob", "no - nothing matching 'rope'");
        PartyInventoryProbe.PartyItemResult r = await q.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(r.Unanswered);
        Assert.Empty(PartyInventoryProbe.PartyItemResult.Empty(175).Unanswered);
    }

    [Fact]
    public void Query_BroadcastsHaveToEachMember()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.AddMember("Al");
        h.GoInParty();

        _ = h.Probe.QueryAsync(175, "rope");

        Assert.Equal(2, h.Wire.Count);
        Assert.Equal("/Bob @have rope\r", h.Sent(0));
        Assert.Equal("/Al @have rope\r", h.Sent(1));
    }

    [Fact]
    public async Task AllReply_CompletesEarly_WithAggregatedCounts()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.AddMember("Al");
        h.GoInParty();

        Task<PartyInventoryProbe.PartyItemResult> task = h.Probe.QueryAsync(175, "rope");
        h.Reply("Bob", "yes - 3x 'rope'");
        h.Reply("Al", "no - nothing matching 'rope'");

        // Completed by the second reply — no window needed.
        Assert.True(task.IsCompleted);
        PartyInventoryProbe.PartyItemResult r = await task;
        Assert.Equal(3, r.TotalCount);
        Assert.Equal(2, r.Expected);
        Assert.Equal(2, r.Replied);
        Assert.Equal(3, r.CountsByMember["Bob"]);
        Assert.Equal(0, r.CountsByMember["Al"]);
        Assert.True(r.AnyHeld);
    }

    [Fact]
    public async Task PartialReplies_ThenWindow_CompletesWithWhatArrived()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.AddMember("Al");
        h.GoInParty();

        Task<PartyInventoryProbe.PartyItemResult> task = h.Probe.QueryAsync(175, "rope");
        h.Reply("Bob", "yes - 2x 'rope'");
        Assert.False(task.IsCompleted);   // Al hasn't answered

        h.FireWindows();

        Assert.True(task.IsCompleted);
        PartyInventoryProbe.PartyItemResult r = await task;
        Assert.Equal(2, r.TotalCount);
        Assert.Equal(1, r.Replied);
        Assert.Equal(2, r.Expected);
        Assert.Equal(2, r.CountsByMember["Bob"]);
        Assert.False(r.CountsByMember.ContainsKey("Al"));   // non-responder absent, not zero
    }

    [Fact]
    public async Task ConcurrentQueries_CorrelateByEchoedItemName()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.AddMember("Al");
        h.GoInParty();

        Task<PartyInventoryProbe.PartyItemResult> rope = h.Probe.QueryAsync(1, "rope");
        Task<PartyInventoryProbe.PartyItemResult> grapple = h.Probe.QueryAsync(2, "grapple");

        h.Reply("Bob", "yes - 2x 'rope'");
        h.Reply("Bob", "yes - 1x 'grapple'");
        h.Reply("Al", "no - nothing matching 'rope'");
        h.Reply("Al", "no - nothing matching 'grapple'");

        PartyInventoryProbe.PartyItemResult ropeR = await rope;
        PartyInventoryProbe.PartyItemResult grappleR = await grapple;
        Assert.Equal(2, ropeR.TotalCount);
        Assert.Equal(1, grappleR.TotalCount);
        Assert.Equal(1, ropeR.ItemId);
        Assert.Equal(2, grappleR.ItemId);
    }

    [Fact]
    public async Task NonMatchingTelepath_Ignored()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.GoInParty();

        Task<PartyInventoryProbe.PartyItemResult> task = h.Probe.QueryAsync(1, "rope");
        h.Reply("Bob", "hey what's up");   // not a @have reply
        Assert.False(task.IsCompleted);

        h.FireWindows();
        PartyInventoryProbe.PartyItemResult r = await task;
        Assert.Equal(0, r.Replied);
        Assert.False(r.AnyHeld);
    }

    [Fact]
    public async Task WrongItemReply_DoesNotCompleteOtherQuery()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.GoInParty();

        Task<PartyInventoryProbe.PartyItemResult> task = h.Probe.QueryAsync(1, "rope");
        // Bob answers about a different item — must not satisfy the rope query.
        h.Reply("Bob", "yes - 5x 'boat'");
        Assert.False(task.IsCompleted);

        h.FireWindows();
        PartyInventoryProbe.PartyItemResult r = await task;
        Assert.Equal(0, r.Replied);
    }

    [Fact]
    public async Task ReplyFromNonMember_Ignored()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.GoInParty();

        Task<PartyInventoryProbe.PartyItemResult> task = h.Probe.QueryAsync(1, "rope");
        h.Reply("Stranger", "yes - 9x 'rope'");   // not in the party
        Assert.False(task.IsCompleted);

        h.Reply("Bob", "yes - 1x 'rope'");
        Assert.True(task.IsCompleted);
        PartyInventoryProbe.PartyItemResult r = await task;
        Assert.Equal(1, r.TotalCount);
        Assert.False(r.CountsByMember.ContainsKey("Stranger"));
    }

    [Fact]
    public async Task DuplicateReplyFromMember_CountedOnce()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.AddMember("Al");
        h.GoInParty();

        Task<PartyInventoryProbe.PartyItemResult> task = h.Probe.QueryAsync(1, "rope");
        h.Reply("Bob", "yes - 3x 'rope'");
        h.Reply("Bob", "yes - 3x 'rope'");   // duplicate — Bob already removed
        Assert.False(task.IsCompleted);   // Al still outstanding

        h.Reply("Al", "no - nothing matching 'rope'");
        PartyInventoryProbe.PartyItemResult r = await task;
        Assert.Equal(3, r.TotalCount);   // not 6
    }

    [Fact]
    public async Task FullNameSpeaker_ReducedToGivenName()
    {
        var h = new Harness();
        h.AddMember("Bob Ironhelm");
        h.GoInParty();

        Task<PartyInventoryProbe.PartyItemResult> task = h.Probe.QueryAsync(1, "rope");
        h.Reply("Bob", "yes - 2x 'rope'");   // reply uses given name only

        Assert.True(task.IsCompleted);
        PartyInventoryProbe.PartyItemResult r = await task;
        Assert.Equal(2, r.CountsByMember["Bob"]);
    }

    [Fact]
    public async Task Dispose_CompletesPendingWithEmpty()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.GoInParty();

        Task<PartyInventoryProbe.PartyItemResult> task = h.Probe.QueryAsync(1, "rope");
        Assert.False(task.IsCompleted);

        h.Probe.Dispose();

        PartyInventoryProbe.PartyItemResult r = await task;
        Assert.Equal(0, r.Expected);
        Assert.False(r.AnyHeld);
    }

    [Fact]
    public async Task Query_ItemNameWithSpaces_MatchesEchoedQuery()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.GoInParty();

        Task<PartyInventoryProbe.PartyItemResult> task = h.Probe.QueryAsync(1, "small key");
        Assert.Equal("/Bob @have small key\r", h.Sent(0));
        h.Reply("Bob", "yes - 1x 'small key'");

        Assert.True(task.IsCompleted);
        PartyInventoryProbe.PartyItemResult r = await task;
        Assert.Equal(1, r.TotalCount);
    }
}
