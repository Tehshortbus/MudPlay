using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

public sealed class PartyLevelProbeTests
{
    // Mirrors PartyInventoryProbeTests: the probe is wired to a
    // real ChatRouter so replies flow through the same
    // classification path production uses, a PartyBroadcaster
    // whose wire is captured, and a captured armWindow seam so tests
    // fire the reply window deterministically.
    private sealed class Harness
    {
        public readonly MessageRouter Router;
        public readonly ChatRouter Chat;
        public readonly PartyState State = new();
        public readonly PartyBroadcaster Broadcaster;
        public readonly List<byte[]> Wire = new();
        public readonly List<Action> Windows = new();
        public readonly List<(string Name, int Level)> Recorded = new();
        public readonly PartyLevelProbe Probe;

        public Harness()
        {
            Router = new MessageRouter();
            DefaultPatterns.Seed(Router);
            Chat = new ChatRouter(Router);
            Broadcaster = new PartyBroadcaster(State);
            Broadcaster.SetWireSender(Wire.Add);
            Probe = new PartyLevelProbe(
                Broadcaster, Chat, State,
                armWindow: Windows.Add,
                recordLevel: (n, l) => Recorded.Add((n, l)),
                log: null);
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

        // A well-formed @level reply as ExperienceQueryHandler emits it.
        public static string Level(int lvl) => $"Level {lvl}, 1,234 exp, 500 to next level";
    }

    [Fact]
    public async Task Query_NoOtherMembers_ReturnsEmptyImmediately()
    {
        var h = new Harness();
        h.AddMember("MudPlay", self: true);
        h.GoInParty();

        PartyLevelProbe.PartyLevelResult r = await h.Probe.QueryAsync();

        Assert.Equal(0, r.Expected);
        Assert.Equal(0, r.Replied);
        Assert.Empty(h.Wire);   // no broadcast fired
    }

    [Fact]
    public void Reply_NoPendingQuery_StillRecordsLevel()
    {
        var h = new Harness();
        h.AddMember("MudPlay", self: true);
        h.AddMember("Raijin");
        h.GoInParty();

        // No probe in flight — an unsolicited (or reconnect-time) @level reply
        // must still persist the exact level so a narrow level gate reads it
        // instead of the coarse title band.
        h.Reply("Raijin", Harness.Level(10));

        Assert.Contains(("Raijin", 10), h.Recorded);
    }

    [Fact]
    public async Task Reply_AfterQueryWindowClosed_StillRecordsLevel()
    {
        var h = new Harness();
        h.AddMember("MudPlay", self: true);
        h.AddMember("Raijin");
        h.GoInParty();

        Task<PartyLevelProbe.PartyLevelResult> q = h.Probe.QueryAsync();
        h.FireWindows();                                  // window elapses with no reply
        PartyLevelProbe.PartyLevelResult r = await q;
        Assert.Equal(0, r.Replied);                       // probe saw nothing in time

        // The reply lands after the window — the exact level is still recorded
        // (the previous code dropped it, leaving level gates stuck on the band).
        h.Reply("Raijin", Harness.Level(10));

        Assert.Contains(("Raijin", 10), h.Recorded);
    }

    [Fact]
    public async Task Query_OnlyInvitedMember_ReturnsEmptyImmediately()
    {
        var h = new Harness();
        h.AddMember("Tristian", invited: true);   // invited, not joined
        h.GoInParty();

        PartyLevelProbe.PartyLevelResult r = await h.Probe.QueryAsync();

        Assert.Equal(0, r.Expected);   // invited member isn't an expected replier
        Assert.Empty(h.Wire);          // and no @level goes out
    }

    [Fact]
    public void Query_SkipsInvitedMember_BroadcastsOnlyToJoined()
    {
        var h = new Harness();
        h.AddMember("Bob");                        // joined
        h.AddMember("Tristian", invited: true);    // pending invite
        h.GoInParty();

        _ = h.Probe.QueryAsync();

        Assert.Single(h.Wire);
        Assert.Equal("/Bob @level\r", h.Sent(0));   // invited row not telepathed
    }

    [Fact]
    public void Query_BroadcastsLevelToEachMember()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.AddMember("Al");
        h.GoInParty();

        _ = h.Probe.QueryAsync();

        Assert.Equal(2, h.Wire.Count);
        Assert.Equal("/Bob @level\r", h.Sent(0));
        Assert.Equal("/Al @level\r", h.Sent(1));
    }

    [Fact]
    public async Task AllReply_CompletesEarly_WithLevels()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.AddMember("Al");
        h.GoInParty();

        Task<PartyLevelProbe.PartyLevelResult> task = h.Probe.QueryAsync();
        h.Reply("Bob", Harness.Level(42));
        h.Reply("Al", Harness.Level(12));

        Assert.True(task.IsCompleted);   // completed by the second reply — no window needed
        PartyLevelProbe.PartyLevelResult r = await task;
        Assert.Equal(2, r.Expected);
        Assert.Equal(2, r.Replied);
        Assert.Equal(42, r.LevelsByMember["Bob"]);
        Assert.Equal(12, r.LevelsByMember["Al"]);
        Assert.True(r.Replied > 0);
    }

    [Fact]
    public async Task PartialReplies_ThenWindow_CompletesWithWhatArrived()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.AddMember("Al");
        h.GoInParty();

        Task<PartyLevelProbe.PartyLevelResult> task = h.Probe.QueryAsync();
        h.Reply("Bob", Harness.Level(30));
        Assert.False(task.IsCompleted);   // Al hasn't answered

        h.FireWindows();

        Assert.True(task.IsCompleted);
        PartyLevelProbe.PartyLevelResult r = await task;
        Assert.Equal(1, r.Replied);
        Assert.Equal(2, r.Expected);
        Assert.Equal(30, r.LevelsByMember["Bob"]);
        Assert.False(r.LevelsByMember.ContainsKey("Al"));   // non-responder absent
    }

    [Fact]
    public async Task LevelUnknownReply_CountsAsRepliedButNoLevel()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.AddMember("Al");
        h.GoInParty();

        Task<PartyLevelProbe.PartyLevelResult> task = h.Probe.QueryAsync();
        h.Reply("Bob", Harness.Level(25));
        h.Reply("Al", "level unknown - parse a stat screen first (type stat)");

        Assert.True(task.IsCompleted);   // both answered, so no window wait
        PartyLevelProbe.PartyLevelResult r = await task;
        Assert.Equal(2, r.Replied);
        Assert.Equal(25, r.LevelsByMember["Bob"]);
        Assert.False(r.LevelsByMember.ContainsKey("Al"));   // replied, but level not usable
    }

    [Fact]
    public async Task ChatterStartingLevelN_NotMisparsed()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.GoInParty();

        Task<PartyLevelProbe.PartyLevelResult> task = h.Probe.QueryAsync();
        // Human chatter that happens to start "Level 5," — must not be read as a reply.
        h.Reply("Bob", "Level 5, that dungeon is deadly");
        Assert.False(task.IsCompleted);

        h.FireWindows();
        PartyLevelProbe.PartyLevelResult r = await task;
        Assert.Equal(0, r.Replied);
        Assert.Empty(r.LevelsByMember);
    }

    [Fact]
    public async Task ReplyFromNonMember_Ignored()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.GoInParty();

        Task<PartyLevelProbe.PartyLevelResult> task = h.Probe.QueryAsync();
        h.Reply("Stranger", Harness.Level(99));   // not in the party
        Assert.False(task.IsCompleted);

        h.Reply("Bob", Harness.Level(40));
        Assert.True(task.IsCompleted);
        PartyLevelProbe.PartyLevelResult r = await task;
        Assert.Equal(40, r.LevelsByMember["Bob"]);
        Assert.False(r.LevelsByMember.ContainsKey("Stranger"));
    }

    [Fact]
    public async Task DuplicateReplyFromMember_TakenOnce()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.AddMember("Al");
        h.GoInParty();

        Task<PartyLevelProbe.PartyLevelResult> task = h.Probe.QueryAsync();
        h.Reply("Bob", Harness.Level(20));
        h.Reply("Bob", Harness.Level(21));   // duplicate — Bob already removed
        Assert.False(task.IsCompleted);       // Al still outstanding

        h.Reply("Al", Harness.Level(15));
        PartyLevelProbe.PartyLevelResult r = await task;
        Assert.Equal(20, r.LevelsByMember["Bob"]);   // first answer wins, not overwritten
        Assert.Equal(2, r.Replied);
    }

    [Fact]
    public async Task FullNameSpeaker_ReducedToGivenName()
    {
        var h = new Harness();
        h.AddMember("Bob Ironhelm");
        h.GoInParty();

        Task<PartyLevelProbe.PartyLevelResult> task = h.Probe.QueryAsync();
        h.Reply("Bob", Harness.Level(33));   // reply uses given name only

        Assert.True(task.IsCompleted);
        PartyLevelProbe.PartyLevelResult r = await task;
        Assert.Equal(33, r.LevelsByMember["Bob"]);
    }

    [Fact]
    public async Task ConcurrentQueries_ShareReplies()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.GoInParty();

        Task<PartyLevelProbe.PartyLevelResult> a = h.Probe.QueryAsync();
        Task<PartyLevelProbe.PartyLevelResult> b = h.Probe.QueryAsync();

        // One reply (no echoed parameter) satisfies both in-flight queries.
        h.Reply("Bob", Harness.Level(50));

        PartyLevelProbe.PartyLevelResult ra = await a;
        PartyLevelProbe.PartyLevelResult rb = await b;
        Assert.Equal(50, ra.LevelsByMember["Bob"]);
        Assert.Equal(50, rb.LevelsByMember["Bob"]);
    }

    [Fact]
    public void KnownReply_RecordsExactLevelToTable()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.AddMember("Al");
        h.GoInParty();

        _ = h.Probe.QueryAsync();
        h.Reply("Bob", Harness.Level(42));
        h.Reply("Al", Harness.Level(12));

        Assert.Equal(2, h.Recorded.Count);
        Assert.Contains(("Bob", 42), h.Recorded);
        Assert.Contains(("Al", 12), h.Recorded);
    }

    [Fact]
    public void UnknownReply_RecordsNoLevel()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.GoInParty();

        _ = h.Probe.QueryAsync();
        h.Reply("Bob", "level unknown - parse a stat screen first (type stat)");

        Assert.Empty(h.Recorded);   // "unknown" carries no level to persist
    }

    [Fact]
    public void ChatterStartingLevelN_RecordsNoLevel()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.GoInParty();

        _ = h.Probe.QueryAsync();
        h.Reply("Bob", "Level 5, that dungeon is deadly");   // human chatter, not a reply

        Assert.Empty(h.Recorded);   // the misparse guard protects persistence too
    }

    [Fact]
    public void FullNameSpeaker_RecordsUnderGivenName()
    {
        var h = new Harness();
        h.AddMember("Bob Ironhelm");
        h.GoInParty();

        _ = h.Probe.QueryAsync();
        h.Reply("Bob", Harness.Level(33));

        Assert.Contains(("Bob", 33), h.Recorded);
    }

    [Fact]
    public async Task Dispose_CompletesPendingWithEmpty()
    {
        var h = new Harness();
        h.AddMember("Bob");
        h.GoInParty();

        Task<PartyLevelProbe.PartyLevelResult> task = h.Probe.QueryAsync();
        Assert.False(task.IsCompleted);

        h.Probe.Dispose();

        PartyLevelProbe.PartyLevelResult r = await task;
        Assert.Equal(0, r.Expected);
        Assert.Equal(0, r.Replied);
    }
}
