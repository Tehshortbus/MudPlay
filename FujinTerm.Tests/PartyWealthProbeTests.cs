using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using FujinTerm.Game;
using FujinTerm.Game.Remote;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Mirrors <see cref="PartyLevelProbeTests"/>: the probe is wired to a real
/// <see cref="ChatRouter"/> so replies flow through the same classification
/// path production uses, a <see cref="PartyBroadcaster"/> whose wire is
/// captured, and a captured <c>armWindow</c> seam so tests fire the reply
/// window deterministically. The three @wealth reply shapes
/// (<see cref="InventoryQueryHandler"/>.OnWealth) are exercised: a loaded
/// wallet with coins, a real empty purse, and "wealth unknown".
/// </summary>
public sealed class PartyWealthProbeTests
{
    private sealed class Harness
    {
        public readonly MessageRouter Router;
        public readonly ChatRouter Chat;
        public readonly PartyState State = new();
        public readonly PartyBroadcaster Broadcaster;
        public readonly List<byte[]> Wire = new();
        public readonly List<Action> Windows = new();
        public readonly List<(string Name, long Copper)> Recorded = new();
        public readonly PartyWealthProbe Probe;

        public Harness()
        {
            Router = new MessageRouter();
            DefaultPatterns.Seed(Router);
            Chat = new ChatRouter(Router);
            Broadcaster = new PartyBroadcaster(State);
            Broadcaster.SetWireSender(Wire.Add);
            Probe = new PartyWealthProbe(
                Broadcaster, Chat, State,
                armWindow: Windows.Add,
                recordWealth: (n, c) => Recorded.Add((n, c)),
                log: null);
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

        // A loaded @wealth reply carrying coins, as InventoryQueryHandler emits
        // it: a denomination breakdown plus the consolidated "(= N copper)" tail
        // (":N0" thousands-commas).
        public static string Coins(long copper) => $"1 gold (= {copper:N0} copper)";

        // The empty-purse reply — no coins, no "(= …)" tail.
        public const string EmptyPurse = "no coins on hand";

        // The not-parsed-yet reply.
        public const string Unknown = "wealth unknown - parse inventory first (type i)";
    }

    [Fact]
    public async Task Query_NoOtherMembers_ReturnsEmptyImmediately()
    {
        var h = new Harness();
        h.AddMember("Fujin", self: true);
        h.GoInParty();

        PartyWealthProbe.PartyWealthResult r = await h.Probe.QueryAsync();

        Assert.Equal(0, r.Expected);
        Assert.False(r.AnyReplied);
        Assert.Empty(h.Wire);   // no broadcast fired
    }

    [Fact]
    public void Query_BroadcastsWealthToEachMember()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.AddMember("Al");
        h.GoInParty();

        _ = h.Probe.QueryAsync();

        Assert.Equal(2, h.Wire.Count);
        Assert.Equal("/Bob @wealth\r", h.Sent(0));
        Assert.Equal("/Al @wealth\r", h.Sent(1));
    }

    [Fact]
    public async Task AllReply_CompletesEarly_WithWealth()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.AddMember("Al");
        h.GoInParty();

        Task<PartyWealthProbe.PartyWealthResult> task = h.Probe.QueryAsync();
        h.Reply("Bob", Harness.Coins(4200));
        h.Reply("Al", Harness.Coins(150));

        Assert.True(task.IsCompleted);   // completed by the second reply
        PartyWealthProbe.PartyWealthResult r = await task;
        Assert.Equal(2, r.Expected);
        Assert.Equal(2, r.Replied);
        Assert.Equal(4200, r.WealthByMember["Bob"]);
        Assert.Equal(150, r.WealthByMember["Al"]);
        Assert.True(r.AnyReplied);
    }

    [Fact]
    public async Task CommaFormattedValue_ParsedAsFullAmount()
    {
        var h = new Harness();
        h.AddMember("Rich");
        h.GoInParty();

        Task<PartyWealthProbe.PartyWealthResult> task = h.Probe.QueryAsync();
        h.Reply("Rich", Harness.Coins(1_234_567));   // "1,234,567 copper"

        PartyWealthProbe.PartyWealthResult r = await task;
        Assert.Equal(1_234_567, r.WealthByMember["Rich"]);   // commas stripped
    }

    [Fact]
    public async Task EmptyPurseReply_CountsAsZero()
    {
        var h = new Harness();
        h.AddMember("Broke");
        h.GoInParty();

        Task<PartyWealthProbe.PartyWealthResult> task = h.Probe.QueryAsync();
        h.Reply("Broke", Harness.EmptyPurse);   // a real empty wallet

        PartyWealthProbe.PartyWealthResult r = await task;
        Assert.Equal(1, r.Replied);
        Assert.Equal(0, r.WealthByMember["Broke"]);   // 0 is a usable figure — gates any toll
    }

    [Fact]
    public async Task WealthUnknownReply_CountsAsRepliedButNoValue()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.AddMember("Al");
        h.GoInParty();

        Task<PartyWealthProbe.PartyWealthResult> task = h.Probe.QueryAsync();
        h.Reply("Bob", Harness.Coins(900));
        h.Reply("Al", Harness.Unknown);

        Assert.True(task.IsCompleted);   // both answered, no window wait
        PartyWealthProbe.PartyWealthResult r = await task;
        Assert.Equal(2, r.Replied);
        Assert.Equal(900, r.WealthByMember["Bob"]);
        Assert.False(r.WealthByMember.ContainsKey("Al"));   // replied, but no usable figure
    }

    [Fact]
    public async Task PartialReplies_ThenWindow_CompletesWithWhatArrived()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.AddMember("Al");
        h.GoInParty();

        Task<PartyWealthProbe.PartyWealthResult> task = h.Probe.QueryAsync();
        h.Reply("Bob", Harness.Coins(300));
        Assert.False(task.IsCompleted);   // Al hasn't answered

        h.FireWindows();

        PartyWealthProbe.PartyWealthResult r = await task;
        Assert.Equal(1, r.Replied);
        Assert.Equal(2, r.Expected);
        Assert.Equal(300, r.WealthByMember["Bob"]);
        Assert.False(r.WealthByMember.ContainsKey("Al"));   // non-responder absent
    }

    [Fact]
    public async Task ChatterMentioningCopper_NotMisparsed()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.GoInParty();

        Task<PartyWealthProbe.PartyWealthResult> task = h.Probe.QueryAsync();
        // Human chatter that mentions copper but lacks the "(= N copper)" tail.
        h.Reply("Bob", "I found tons of copper down here");
        Assert.False(task.IsCompleted);

        h.FireWindows();
        PartyWealthProbe.PartyWealthResult r = await task;
        Assert.Equal(0, r.Replied);
        Assert.Empty(r.WealthByMember);
    }

    [Fact]
    public async Task ReplyFromNonMember_Ignored()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.GoInParty();

        Task<PartyWealthProbe.PartyWealthResult> task = h.Probe.QueryAsync();
        h.Reply("Stranger", Harness.Coins(99999));   // not in the party
        Assert.False(task.IsCompleted);

        h.Reply("Bob", Harness.Coins(400));
        PartyWealthProbe.PartyWealthResult r = await task;
        Assert.Equal(400, r.WealthByMember["Bob"]);
        Assert.False(r.WealthByMember.ContainsKey("Stranger"));
    }

    [Fact]
    public async Task FullNameSpeaker_ReducedToGivenName()
    {
        var h = new Harness();
        h.AddMember("Bob Ironhelm");
        h.GoInParty();

        Task<PartyWealthProbe.PartyWealthResult> task = h.Probe.QueryAsync();
        h.Reply("Bob", Harness.Coins(777));   // reply uses given name only

        PartyWealthProbe.PartyWealthResult r = await task;
        Assert.Equal(777, r.WealthByMember["Bob"]);
    }

    [Fact]
    public void KnownReply_ForwardsToRecordSeam()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.AddMember("Broke");
        h.GoInParty();

        _ = h.Probe.QueryAsync();
        h.Reply("Bob", Harness.Coins(4200));
        h.Reply("Broke", Harness.EmptyPurse);

        Assert.Equal(2, h.Recorded.Count);
        Assert.Contains(("Bob", 4200L), h.Recorded);
        Assert.Contains(("Broke", 0L), h.Recorded);   // empty purse recorded as 0
    }

    [Fact]
    public void UnknownReply_RecordsNothing()
    {
        var h = new Harness();
        h.AddMember("Al");
        h.GoInParty();

        _ = h.Probe.QueryAsync();
        h.Reply("Al", Harness.Unknown);

        Assert.Empty(h.Recorded);   // "unknown" carries no figure to forward
    }

    [Fact]
    public async Task Dispose_CompletesPendingWithEmpty()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.GoInParty();

        Task<PartyWealthProbe.PartyWealthResult> task = h.Probe.QueryAsync();
        Assert.False(task.IsCompleted);

        h.Probe.Dispose();

        PartyWealthProbe.PartyWealthResult r = await task;
        Assert.Equal(0, r.Expected);
        Assert.False(r.AnyReplied);
    }
}
