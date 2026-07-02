using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using FujinTerm.Game;
using FujinTerm.Game.Remote;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using Xunit;

namespace FujinTerm.Tests;

public sealed class PartyInventoryProbeTests
{
    /// <summary>
    /// Harness wiring the probe to a real <see cref="ChatRouter"/> (so replies
    /// flow through the same classification path production uses) and a
    /// <see cref="PartyBroadcaster"/> whose wire is captured. The
    /// <c>armWindow</c> seam is captured rather than timer-driven, so tests
    /// fire the reply window deterministically via <see cref="FireWindows"/>.
    /// </summary>
    private sealed class Harness
    {
        public readonly MessageRouter Router;
        public readonly ChatRouter Chat;
        public readonly PartyState State = new();
        public readonly PartyBroadcaster Broadcaster;
        public readonly List<byte[]> Wire = new();
        public readonly List<Action> Windows = new();
        public readonly PartyInventoryProbe Probe;

        public Harness()
        {
            Router = new MessageRouter();
            DefaultPatterns.Seed(Router);
            Chat = new ChatRouter(Router);
            Broadcaster = new PartyBroadcaster(State);
            Broadcaster.SetWireSender(Wire.Add);
            Probe = new PartyInventoryProbe(Broadcaster, Chat, State, armWindow: Windows.Add, log: null);
        }

        public void AddMember(string name, bool self = false)
            => State.Members.Add(new PartyMember { Name = name, IsSelf = self });

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
        h.AddMember("Fujin", self: true);
        h.GoInParty();

        PartyInventoryProbe.PartyItemResult r = await h.Probe.QueryAsync(175, "rope");

        Assert.Equal(0, r.Expected);
        Assert.False(r.AnyHeld);
        Assert.Empty(h.Wire);   // no broadcast fired
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
        h.Reply("Bob", "yes - 3x matching 'rope'");
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
        h.Reply("Bob", "yes - 2x matching 'rope'");
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

        h.Reply("Bob", "yes - 2x matching 'rope'");
        h.Reply("Bob", "yes - 1x matching 'grapple'");
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
        h.Reply("Bob", "yes - 5x matching 'boat'");
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
        h.Reply("Stranger", "yes - 9x matching 'rope'");   // not in the party
        Assert.False(task.IsCompleted);

        h.Reply("Bob", "yes - 1x matching 'rope'");
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
        h.Reply("Bob", "yes - 3x matching 'rope'");
        h.Reply("Bob", "yes - 3x matching 'rope'");   // duplicate — Bob already removed
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
        h.Reply("Bob", "yes - 2x matching 'rope'");   // reply uses given name only

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
        h.Reply("Bob", "yes - 1x matching 'small key'");

        Assert.True(task.IsCompleted);
        PartyInventoryProbe.PartyItemResult r = await task;
        Assert.Equal(1, r.TotalCount);
    }
}
