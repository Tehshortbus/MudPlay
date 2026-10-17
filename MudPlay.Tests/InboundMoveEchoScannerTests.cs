using System;
using System.IO;
using System.Text.RegularExpressions;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Verifies the bridge from LineExtractor's prompt/content split to
// RoomTracker.NoteInboundMoveEcho — the wire-format-dependent bit. The end-to-end
// echo-gate behaviour itself is covered by RoomTrackerTests /
// SoldiersQuartersGridLostRepro with echoes fed directly; here we prove the
// scanner forwards the right line (a prompt-split command echo) and ignores the
// wrong one (a room display following a bare prompt).
public sealed class InboundMoveEchoScannerTests : IDisposable
{
    // The game's output, fed the way the client receives it.
    private readonly ServerFeed _feed = new();

    private readonly string _root;

    public InboundMoveEchoScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-echo-scanner-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // Three identically-named "Hall" rooms in an N/S cycle, each {N,S}: a move
    // confirms by predicted-neighbour (identical name + exits), so its landing is
    // echo-gated — exactly the case the scanner must feed.
    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Hall",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "1/3", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Hall",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Hall",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/1", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private (RoomTracker Tracker, InboundMoveEchoScanner Scanner) NewScanner(Regex? promptPattern = null)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        InboundMoveEchoScanner scanner = promptPattern is null
            ? new(_feed.Lines, tracker)
            : new(_feed.Lines, tracker, () => promptPattern);
        return (tracker, scanner);
    }

    private static RoomObservation Hall() =>
        new("Hall", new HashSet<Direction> { Direction.N, Direction.S });

    // A prompt-split command echo (prompt line + trailing content sharing its
    // timestamp) is forwarded, so the move's same-named-neighbour landing confirms.
    [Fact]
    public void PromptSplitEcho_ConfirmsMove()
    {
        (RoomTracker tracker, InboundMoveEchoScanner scanner) = NewScanner();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        tracker.SetLocated(new RoomKey(1, 1), t0);
        tracker.NoteRoomObserved(Hall(), t0);
        tracker.NoteMoveSent(Direction.N, t0.AddSeconds(1));  // predicted 1/2 (identical {N,S})

        // The prompt with "n" echoed onto it: one row, which the extractor splits
        // into a prompt half and "n" sharing the row's timestamp.
        DateTimeOffset echoAt = t0.AddSeconds(2);
        _feed.Line("[HP=100/MA=50]:n", echoAt);

        tracker.NoteRoomObserved(Hall(), echoAt.AddMilliseconds(100));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 2), tracker.State.CurrentRoom!.Key);
    }

    // A room display following a BARE prompt carries a later timestamp (a separate
    // row), so the scanner does NOT forward it as an echo — the tracker records no
    // move-echo from it.
    [Fact]
    public void BarePromptThenRoomLine_NotTreatedAsEcho()
    {
        (RoomTracker tracker, InboundMoveEchoScanner scanner) = NewScanner();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        tracker.SetLocated(new RoomKey(1, 1), t0);
        tracker.NoteRoomObserved(Hall(), t0);
        tracker.NoteMoveSent(Direction.N, t0.AddSeconds(1));

        // Bare prompt, then the room name on a later row (different timestamp).
        _feed.Line("[HP=100/MA=50]:", t0.AddSeconds(2));
        _feed.Line("Hall", t0.AddSeconds(2).AddMilliseconds(40));

        // Nothing was forwarded as an echo — the room line is not a command echo.
        Assert.Null(tracker.LastInboundMoveEcho);
    }

    // The echo can be knocked off the prompt onto its own row — a BBS broadcast
    // rendering between the two (report stock-20260914-000155), or a command the
    // client sent in the same breath taking the prompt slot and pushing the move's
    // type-ahead echo down a line (auto-sneak's "sn", report -000112). Paths A and B
    // see nothing; the detached line is still the echo and must confirm the landing,
    // or the move sits Pending forever in an identically-named grid.
    [Fact]
    public void DetachedEcho_OnOwnLine_ConfirmsMove()
    {
        (RoomTracker tracker, InboundMoveEchoScanner scanner) = NewScanner();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        tracker.SetLocated(new RoomKey(1, 1), t0);
        tracker.NoteRoomObserved(Hall(), t0);
        tracker.NoteMoveSent(Direction.N, t0.AddSeconds(1));  // predicted 1/2 (identical {N,S})

        // Bare prompt, a broadcast line, then the echo stranded on its own row.
        DateTimeOffset at = t0.AddSeconds(2);
        _feed.Line("[HP=100/MA=50]:", at);
        _feed.Line("[Kitti] logs ON - Hi there!", at.AddMilliseconds(10));
        _feed.Line("n", at.AddMilliseconds(20));

        tracker.NoteRoomObserved(Hall(), at.AddMilliseconds(120));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 2), tracker.State.CurrentRoom!.Key);
    }

    // The detached path is scoped to the exact token of the move in flight, so
    // ordinary one-word content on its own row is never mistaken for an echo.
    [Fact]
    public void DetachedLine_NotTheInFlightToken_IsNotAnEcho()
    {
        (RoomTracker tracker, InboundMoveEchoScanner scanner) = NewScanner();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        tracker.SetLocated(new RoomKey(1, 1), t0);
        tracker.NoteRoomObserved(Hall(), t0);
        tracker.NoteMoveSent(Direction.N, t0.AddSeconds(1));

        // "Hall" and "s" are both single bare tokens; neither names the in-flight N.
        _feed.Line("Hall", t0.AddSeconds(2));
        _feed.Line("s", t0.AddSeconds(2).AddMilliseconds(10));

        Assert.Null(tracker.LastInboundMoveEcho);
    }

    // A detached echo must NOT latch the tracker's "this statline is echo-readable"
    // gate. It only appears when something displaced the echo, so it proves nothing
    // about the ordinary prompt-glued case. Latching it for a user whose statline the
    // scanner can't split would switch the arrival gate to demanding echoes it can
    // only occasionally see — freezing every un-echoed move as a re-look. Here the
    // second move gets no echo at all and must still confirm on the timing fallback.
    [Fact]
    public void DetachedEcho_DoesNotLatchStatlineReadable()
    {
        (RoomTracker tracker, InboundMoveEchoScanner scanner) = NewScanner();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        tracker.SetLocated(new RoomKey(1, 1), t0);
        tracker.NoteRoomObserved(Hall(), t0);

        // Move one lands via a detached echo.
        tracker.NoteMoveSent(Direction.N, t0.AddSeconds(1));
        _feed.Line("n", t0.AddSeconds(2));
        tracker.NoteRoomObserved(Hall(), t0.AddSeconds(2).AddMilliseconds(100));
        Assert.Equal(new RoomKey(1, 2), tracker.State.CurrentRoom!.Key);

        // Move two gets NO echo. Past the 400ms ambiguity floor the timing fallback
        // must still confirm it — proof the detached echo didn't flip the latch.
        tracker.NoteMoveSent(Direction.N, t0.AddSeconds(5));
        tracker.NoteRoomObserved(Hall(), t0.AddSeconds(6));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 3), tracker.State.CurrentRoom!.Key);
    }

    // A CUSTOM statline LineExtractor's built-in "[HP=..]:" split doesn't recognise:
    // the whole prompt + typed command arrive as ONE non-prompt line. Given the
    // user's configured statline matcher, the scanner still lifts the trailing
    // command (path B) — so the move's same-named-neighbour landing confirms.
    [Fact]
    public void CustomStatlinePrompt_UnsplitLine_ConfirmsMove()
    {
        // A user-authored prompt shape "<hp/mana>:" the default [HP=..]: split misses.
        Regex custom = new(@"<\d+/\d+>:", RegexOptions.CultureInvariant);
        (RoomTracker tracker, InboundMoveEchoScanner scanner) = NewScanner(custom);
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        tracker.SetLocated(new RoomKey(1, 1), t0);
        tracker.NoteRoomObserved(Hall(), t0);
        tracker.NoteMoveSent(Direction.N, t0.AddSeconds(1));  // predicted 1/2 (identical {N,S})

        // The whole row as one non-prompt line: prompt + the echoed "n".
        DateTimeOffset echoAt = t0.AddSeconds(2);
        _feed.Line("<100/50>:n", echoAt);

        tracker.NoteRoomObserved(Hall(), echoAt.AddMilliseconds(100));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 2), tracker.State.CurrentRoom!.Key);
    }
}
