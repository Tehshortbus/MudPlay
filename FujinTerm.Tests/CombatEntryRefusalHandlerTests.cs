using System.IO;
using System.Text;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

public sealed class CombatEntryRefusalHandlerTests : IDisposable
{
    private const string CombatGatedLine = "You may not enter that room while in combat.";

    private readonly string _root;

    public CombatEntryRefusalHandlerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-combatentry-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Origin",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Beyond",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed class Harness : IDisposable
    {
        public RoomTracker Tracker { get; }
        public CombatEntryRefusalHandler Handler { get; }
        public bool EngineActive { get; set; } = true;
        public string? LastSent { get; private set; }
        public int SendCount { get; private set; }

        public Harness(string root)
        {
            Directory.CreateDirectory(Path.Combine(root, "alpha"));
            File.WriteAllText(Path.Combine(root, "alpha", "Rooms.json"), GraphJson);
            GameDataCache cache = new(root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");
            Tracker = new RoomTracker(graph);
            LineExtractor lines = new(new TerminalEmulator(80, 25));
            Handler = new CombatEntryRefusalHandler(lines, Tracker, () => EngineActive);
            Handler.SetWireSender(bytes =>
            {
                LastSent = Encoding.Latin1.GetString(bytes);
                SendCount++;
            });
        }

        public void SetupPending()
        {
            Tracker.SetLocated(new RoomKey(1, 1));
            Tracker.NoteMoveSent(Direction.N);
            Assert.Equal(RoomConfidence.Pending, Tracker.State.Confidence);
        }

        public void Dispose() => Handler.Dispose();
    }

    [Fact]
    public void CombatGatedRefusal_WhileEngineDrivingPending_SendsBreakAndSettles()
    {
        using Harness h = new(_root);
        h.SetupPending();

        h.Handler.FeedTestLine(CombatGatedLine);

        Assert.Equal("break\r", h.LastSent);
        Assert.Equal(1, h.SendCount);
        Assert.True(h.Handler.IsSettling);
        // The move is still in flight until the settle beat elapses.
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);
    }

    [Fact]
    public void CompleteRetry_AfterBreak_RevertsPendingToSource()
    {
        using Harness h = new(_root);
        h.SetupPending();

        h.Handler.FeedTestLine(CombatGatedLine);
        h.Handler.CompleteRetryForTest();

        Assert.False(h.Handler.IsSettling);
        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), h.Tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void SecondRefusal_WhileSettling_DoesNotStackBreaks()
    {
        using Harness h = new(_root);
        h.SetupPending();

        h.Handler.FeedTestLine(CombatGatedLine);
        h.Handler.FeedTestLine(CombatGatedLine);

        Assert.Equal(1, h.SendCount);
    }

    [Fact]
    public void ManualPlayer_EngineInactive_LeavesCombatAlone()
    {
        using Harness h = new(_root);
        h.SetupPending();
        h.EngineActive = false;

        h.Handler.FeedTestLine(CombatGatedLine);

        Assert.Equal(0, h.SendCount);
        Assert.False(h.Handler.IsSettling);
    }

    [Fact]
    public void NoMoveInFlight_NonPending_DoesNothing()
    {
        using Harness h = new(_root);
        h.Tracker.SetLocated(new RoomKey(1, 1));   // Confirmed, no pending move

        h.Handler.FeedTestLine(CombatGatedLine);

        Assert.Equal(0, h.SendCount);
        Assert.False(h.Handler.IsSettling);
    }

    [Fact]
    public void UnrelatedLine_DoesNotTrigger()
    {
        using Harness h = new(_root);
        h.SetupPending();

        h.Handler.FeedTestLine("The goblin growls at you.");

        Assert.Equal(0, h.SendCount);
        Assert.False(h.Handler.IsSettling);
    }

    [Fact]
    public void ChatLineQuotingPhrase_DoesNotTrigger()
    {
        using Harness h = new(_root);
        h.SetupPending();

        // Anchored pattern — a quoted copy inside a chat line can't false-fire.
        h.Handler.FeedTestLine("[Gossip] Bob: heh 'You may not enter that room while in combat.' noob");

        Assert.Equal(0, h.SendCount);
        Assert.False(h.Handler.IsSettling);
    }
}
