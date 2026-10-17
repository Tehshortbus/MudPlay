using System.Text;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// A follower asks its leader's client where the party is heading, and the answer
// decides whether the Bossing set stays on.
public sealed class LeaderBossTravelProbeTests
{
    private static readonly RoomKey Boss = new(6, 1249);

    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public PathReplyTracker Replies { get; }
        public LeaderBossTravelProbe Probe { get; }
        // What Probe put on the wire, through the sender the client binds.
        public List<byte[]> ProbeWire { get; } = new();
        public List<bool> Answers { get; } = new();
        public string? Leader { get; set; } = "Raijin";
        public bool StillNeeded { get; set; } = true;
        // The wait for the leader's answer.
        public ManualTimers Timers { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Replies = new PathReplyTracker(Router, isPartyLeader: s => s == Leader);
            Probe = new LeaderBossTravelProbe(Replies,
                leaderGivenName: () => Leader,
                headsToBoss: r => r.Destination == Boss,
                isBossRoom: k => k == Boss,
                stillNeeded: () => StillNeeded,
                schedule: Timers.Arm);
            Probe.SetWireSender(ProbeWire.Add);
            Probe.Resolved += Answers.Add;
        }

        public string LastSent => Encoding.Latin1.GetString(ProbeWire[^1]);

        public void Line(string text) =>
            Router.Dispatch(new LineExtractor.EmittedLine(text, [], DateTimeOffset.UtcNow, false));

        public void Dispose() { Probe.Dispose(); Replies.Dispose(); }
    }

    [Fact]
    public void Ask_TelepathsTheLeader_AndABossDestinationAnswersYes()
    {
        using Harness h = new();
        Assert.True(h.Probe.Ask());
        Assert.Equal("/Raijin @path\r", h.LastSent);

        h.Line("Raijin telepaths: {walking to 6/1249; Dark Road (map 6, room 1200); step 3/40}");

        Assert.Equal(new[] { true }, h.Answers);
        // Nothing more is asked until the next boss room.
        h.Probe.NoteMoved();
        Assert.Single(h.ProbeWire);
    }

    [Fact]
    public void AnswerThatIsNotABossRoom_IsANo()
    {
        using Harness h = new();
        h.Probe.Ask();

        h.Line("Raijin telepaths: {walking to 1/5; Dark Road (map 6, room 1200); step 3/40}");

        Assert.Equal(new[] { false }, h.Answers);
    }

    // No reply to the first ask decides nothing: ask again when the party moves. No
    // reply to that one within the wait takes the gear off.
    [Fact]
    public void NoAnswer_AsksAgainOnTheNextMove_ThenGivesUp()
    {
        using Harness h = new();
        h.Probe.Ask();
        Assert.Equal(LeaderBossTravelProbe.ReplyTimeout, h.Timers.PendingDelays[^1]);

        h.Timers.RunDue(LeaderBossTravelProbe.ReplyTimeout);
        Assert.Empty(h.Answers);

        h.Probe.NoteMoved();
        Assert.Equal(2, h.ProbeWire.Count);
        h.Timers.RunDue(LeaderBossTravelProbe.ReplyTimeout);

        Assert.Equal(new[] { false }, h.Answers);
    }

    [Fact]
    public void NoAnswer_ThenABossDestinationOnTheSecondAsk_IsAYes()
    {
        using Harness h = new();
        h.Probe.Ask();
        h.Timers.RunDue(LeaderBossTravelProbe.ReplyTimeout);
        h.Probe.NoteMoved();

        h.Line("Raijin telepaths: {walking to 6/1249; Dark Road (map 6, room 1200); step 1/40}");

        Assert.Equal(new[] { true }, h.Answers);
    }

    [Fact]
    public void NoLeader_NothingIsAsked()
    {
        using Harness h = new() { Leader = null };
        Assert.False(h.Probe.Ask());
        Assert.Empty(h.ProbeWire);
    }

    [Fact]
    public void SomeoneElsesReply_IsIgnored()
    {
        using Harness h = new();
        h.Probe.Ask();

        h.Line("Suijin telepaths: {walking to 6/1249; Dark Road (map 6, room 1200); step 3/40}");

        Assert.Empty(h.Answers);
    }

    // An idle leader hasn't picked where to go: nothing is decided until the party
    // moves, and the answer to that second question stands.
    [Fact]
    public void IdleLeader_IsAskedAgainWhenThePartyMoves()
    {
        using Harness h = new();
        h.Probe.Ask();
        h.Line("Raijin telepaths: {not moving}");
        Assert.Empty(h.Answers);

        h.Probe.NoteMoved();
        Assert.Equal(2, h.ProbeWire.Count);
        h.Line("Raijin telepaths: {walking to 6/1249; Dark Road (map 6, room 1200); step 1/40}");

        Assert.Equal(new[] { true }, h.Answers);
    }

    [Fact]
    public void IdleLeader_StillIdleOnceMoving_IsANo()
    {
        using Harness h = new();
        h.Probe.Ask();
        h.Line("Raijin telepaths: {not moving; last ran loop 'Bosses'}");
        h.Probe.NoteMoved();
        h.Line("Raijin telepaths: {not moving}");

        Assert.Equal(new[] { false }, h.Answers);
    }

    [Fact]
    public void LeaderOnALoop_IsANo()
    {
        using Harness h = new();
        h.Probe.Ask();

        h.Line("Raijin telepaths: {running loop 'Bosses'; Dark Road (map 6, room 1200); step 3/40}");

        Assert.Equal(new[] { false }, h.Answers);
    }

    [Fact]
    public void Moving_WithNoIdleAnswerOutstanding_AsksNothing()
    {
        using Harness h = new();
        h.Probe.NoteMoved();
        Assert.Empty(h.ProbeWire);
    }
}
