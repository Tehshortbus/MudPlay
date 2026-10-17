using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;
using Xunit.Abstractions;

namespace MudPlay.Tests;

// Reproduction for the "LostWays Lockers" fragility (issue #478): looping the
// 4x5 grid of identically-named "Soldier's Quarters" (map 8, around 1703) in
// lostwaysrealm2 reliably drives RoomTracker to Lost.
//
// The grid (all 20 rooms share the name; only the exit-set distinguishes them,
// and even that collapses — the 6 interior rooms are all 4-way N/S/E/W):
//
//         col0   col1   col2   col3
//   row0: 1703 — 1702 — 1701 — 1699
//          |      |      |      |
//   row1: 1704 — 1708 — 1712 — 1716
//          |      |      |      |
//   row2: 1705 — 1709 — 1713 — 1717
//          |      |      |      |
//   row3: 1706 — 1710 — 1714 — 1718
//          |      |      |      |
//   row4: 1707 — 1711 — 1715 — 1719
//
// Root cause: a sent move only flips the tracker to Pending; position advances
// when a room display matches the predicted target (name + subset exits). There
// was no move<->display correlation token, so a stray re-display of the room the
// player is STILL in (a passing mob re-rendering the room, a look, a combat
// redraw) also matches the predicted target in a homogeneous grid, and the only
// guard was a 400ms timing floor that a normal-latency re-display slipped past —
// phantom-advancing the tracker one room ahead, invisibly (every room reads the
// same name) until the predicted exits stopped matching and it bailed to Lost.
//
// Fixed in two layers: NoteCommandDropped un-counts a move the rate limiter
// silently drops (the net miscount that made the drift permanent), and the echo
// gate advances position only once the server has echoed the move command back
// ("[HP=..]:s"), so a stray re-display without a preceding echo can no longer be
// mistaken for the landing — regardless of its timing. These tests drive the REAL
// grid through the REAL RoomTracker to prove both.
public sealed class SoldiersQuartersGridLostRepro : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root;

    public SoldiersQuartersGridLostRepro(ITestOutputHelper output)
    {
        _out = output;
        _root = Path.Combine(Path.GetTempPath(), "mudplay-soldiers-grid-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // The real lostwaysrealm2 map-8 grid around 1703, extracted verbatim.
    private const string GridJson = """
        [
        {"Map Number":8,"Room Number":1703,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"0","S":"8/1704","E":"8/1702","W":"0","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1702,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"0","S":"8/1708","E":"8/1701","W":"8/1703","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1701,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"0","S":"8/1712","E":"8/1699","W":"8/1702","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1699,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"0","S":"8/1716","E":"0","W":"8/1701","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1704,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1703","S":"8/1705","E":"8/1708","W":"0","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1708,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1702","S":"8/1709","E":"8/1712","W":"8/1704","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1712,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1701","S":"8/1713","E":"8/1716","W":"8/1708","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1716,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1699","S":"8/1717","E":"0","W":"8/1712","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1705,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1704","S":"8/1706","E":"8/1709","W":"0","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1709,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1708","S":"8/1710","E":"8/1713","W":"8/1705","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1713,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1712","S":"8/1714","E":"8/1717","W":"8/1709","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1717,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1716","S":"8/1718","E":"0","W":"8/1713","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1706,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1705","S":"8/1707","E":"8/1710","W":"0","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1710,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1709","S":"8/1711","E":"8/1714","W":"8/1706","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1714,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1713","S":"8/1715","E":"8/1718","W":"8/1710","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1718,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1717","S":"8/1719","E":"0","W":"8/1714","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1707,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1706","S":"0","E":"8/1711","W":"0","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1711,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1710","S":"0","E":"8/1715","W":"8/1707","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1715,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1714","S":"0","E":"8/1719","W":"8/1711","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1719,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1718","S":"0","E":"0","W":"8/1715","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"}
        ]
        """;

    private const string Name = "Soldier's Quarters";

    private RoomTracker NewTracker()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GridJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return new RoomTracker(graph);
    }

    // Real grid adjacency (room -> direction -> neighbour), so the simulator can
    // move the "true" position exactly as the game would.
    private static readonly Dictionary<int, Dictionary<Direction, int>> Adj = BuildAdjacency();

    private static Dictionary<int, Dictionary<Direction, int>> BuildAdjacency()
    {
        // Parsed straight from the grid above.
        int[,] rows =
        {
            {1703,1702,1701,1699},
            {1704,1708,1712,1716},
            {1705,1709,1713,1717},
            {1706,1710,1714,1718},
            {1707,1711,1715,1719},
        };
        var adj = new Dictionary<int, Dictionary<Direction, int>>();
        for (int r = 0; r < 5; r++)
            for (int c = 0; c < 4; c++)
            {
                var m = new Dictionary<Direction, int>();
                if (r > 0) m[Direction.N] = rows[r - 1, c];
                if (r < 4) m[Direction.S] = rows[r + 1, c];
                if (c < 3) m[Direction.E] = rows[r, c + 1];
                if (c > 0) m[Direction.W] = rows[r, c - 1];
                adj[rows[r, c]] = m;
            }
        return adj;
    }

    private static HashSet<Direction> ExitsOf(int room) => new(Adj[room].Keys);

    private static RoomObservation Obs(int room) => new(Name, ExitsOf(room));

    // ---------------------------------------------------------------------
    // Test A — the phantom-advance fix via the echo gate (echo-readable statline).
    // A stray re-display of the room the player is STILL in matches the predicted
    // target in this grid, but without the server having echoed the move it is NOT
    // the landing — so the tracker stays put regardless of how late the re-display
    // arrives (the old 400ms floor let a late one phantom-advance). Once the move's
    // echo arrives, the next matching display IS the landing and confirms.
    // ---------------------------------------------------------------------
    [Fact]
    public void SlowSourceRedisplay_Echoed_DoesNotPhantomAdvance()
    {
        RoomTracker tracker = NewTracker();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Echo-capable session (a default "[HP=..]:"-style statline the scanner
        // reads): a move-echo has already flowed, so the tracker trusts the echo
        // signal and drops the timing fallback.
        tracker.NoteInboundMoveEcho("n", t0);

        // Standing, Confirmed, in interior room 1708 (4-way N/S/E/W).
        tracker.SetLocated(new RoomKey(8, 1708), t0);
        tracker.NoteRoomObserved(Obs(1708), t0);

        // Send "s" (predicted target 1709, also interior 4-way). Pending.
        tracker.NoteMoveSent(Direction.S, t0.AddSeconds(1));

        // A passing mob re-renders room 1708 — the room we're STILL in — 700ms
        // later, well past the old 400ms floor. Same name + exits as predicted
        // target 1709, but the server has NOT echoed our "s", so it is not the
        // landing: the tracker stays put (the old timing guard phantom-advanced here).
        tracker.NoteRoomObserved(Obs(1708), t0.AddSeconds(1).AddMilliseconds(700));
        _out.WriteLine($"after slow un-echoed re-display: believes {tracker.State.CurrentRoom?.Key}, {tracker.State.Confidence}");
        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);
        Assert.Equal(new RoomKey(8, 1708), tracker.State.CurrentRoom?.Key);

        // The server now echoes "s"; the next matching display is the genuine
        // landing at 1709 and confirms.
        tracker.NoteInboundMoveEcho("s", t0.AddSeconds(2));
        tracker.NoteRoomObserved(Obs(1709), t0.AddSeconds(2).AddMilliseconds(100));
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(8, 1709), tracker.State.CurrentRoom?.Key);
    }

    // A statline the scanner can't read (a custom prompt) never produces an echo,
    // so the guard falls back to the old timing floor rather than freezing: a FAST
    // stray re-display is still held. (This session feeds no echo, so the latch
    // stays off and the fallback is in force.)
    [Fact]
    public void FastSourceRedisplay_NoEchoStatline_HeldByTimingFallback()
    {
        RoomTracker tracker = NewTracker();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        tracker.SetLocated(new RoomKey(8, 1708), t0);
        tracker.NoteRoomObserved(Obs(1708), t0);
        tracker.NoteMoveSent(Direction.S, t0.AddSeconds(1));

        tracker.NoteRoomObserved(Obs(1708), t0.AddSeconds(1).AddMilliseconds(100));

        Assert.Equal(new RoomKey(8, 1708), tracker.State.CurrentRoom?.Key);
    }

    // ---------------------------------------------------------------------
    // Test B — the permanent-drift fix. A lone stray re-display self-heals
    // (commands still equal real moves), so permanent drift needs a NET miscount:
    // a move the client counts but the character never makes. Roomba's fast
    // command bursts routinely trip the game's typing-rate limiter ("You are
    // typing too quickly - command ignored"), which drops the command silently —
    // the client counted it, the character didn't move. In a homogeneous grid
    // that one-room lie is invisible and compounds into Lost.
    //
    // The fix (RoomTracker.NoteCommandDropped, driven by MovementRefusalDetector's
    // rate-limiter pattern) un-counts the dropped move so the count never runs
    // past reality. This walks a deterministic grid leg with a dropped move
    // mid-way and asserts the tracker ends exactly where the character is.
    [Fact]
    public void SnakeLoop_DroppedMove_UnCounted_StaysInSync()
    {
        RoomTracker tracker = NewTracker();
        var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        int realPos = 1703;
        tracker.SetLocated(new RoomKey(8, realPos), clock);
        tracker.NoteRoomObserved(Obs(realPos), clock);

        // Each (direction, dropped) is a VALID exit from the real position at that
        // step. The dropped E at 1707 is the rate-limiter drop; the real E right
        // after makes the actual 1707->1711 move. Path: down col0, E to col1, up
        // col1, E to col2, down col2 — a realistic loop leg through the 4-way core.
        (Direction dir, bool dropped)[] steps =
        {
            (Direction.S, false), (Direction.S, false), (Direction.S, false), (Direction.S, false), // 1703->1707
            (Direction.E, true),                                                                     // DROPPED at 1707
            (Direction.E, false),                                                                    // 1707->1711
            (Direction.N, false), (Direction.N, false), (Direction.N, false), (Direction.N, false),  // 1711->1702
            (Direction.E, false),                                                                    // 1702->1701
            (Direction.S, false), (Direction.S, false), (Direction.S, false), (Direction.S, false),  // 1701->1715
        };

        foreach ((Direction dir, bool dropped) in steps)
        {
            clock = clock.AddSeconds(2);
            tracker.NoteMoveSent(dir, clock);   // client counts the send either way

            if (dropped)
            {
                // Typing-rate limiter drops it 200ms later: no real move, no arrival.
                tracker.NoteCommandDropped(clock.AddMilliseconds(200));
                continue;
            }

            realPos = Adj[realPos][dir];   // valid exit by construction
            // The server echoes the executed move just before rendering the
            // landing — the causal signal the echo gate confirms on.
            tracker.NoteInboundMoveEcho(dir.ToToken(), clock.AddMilliseconds(1400));
            tracker.NoteRoomObserved(Obs(realPos), clock.AddMilliseconds(1500));
        }

        RoomKey? believed = tracker.State.CurrentRoom?.Key;
        _out.WriteLine($"real position 8/{realPos}, tracker believes {believed}, confidence {tracker.State.Confidence}");

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(8, realPos), believed);
    }

    // ---------------------------------------------------------------------
    // Test C — the passive grid re-localiser. When we're Lost / ambiguous-Suspect
    // in this grid with NO engine driving, EngineRecoveryGate's tier-2 forward
    // localiser never runs (it early-returns with no engine). The tracker instead
    // narrows the same FootprintMatcher passively off the moves the player makes by
    // hand: each (move, observation) pair drops candidates until one remains, then it
    // re-anchors — with no command sent. These walk the REAL grid to prove it.
    //
    // The right-hand column 1716/1717/1718 all display {N,S,W} — never a 1-of-1
    // exact match, so ONLY the passive footprint can resolve it (the tracker's own
    // 1-of-1 / name-covering deductions can't). Walking N then S,S narrows
    // {1716,1717,1718} → {1718} the instant a hop would fall off the bottom corner
    // (1719 is {N,W}), and we re-anchor at 1718 while the display is still ambiguous.
    // ---------------------------------------------------------------------
    [Fact]
    public void PassiveGrid_NoEngine_ConvergesAndReanchors()
    {
        RoomTracker tracker = NewTracker();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        // No engine-attached probe set → treated as "no engine attached", so the
        // passive path runs.

        // From Unknown, the first ambiguous display seeds the footprint (three exact
        // candidates 1716/1717/1718) and drops us to Suspect with no anchor.
        tracker.NoteRoomObserved(Obs(1717), t0);
        Assert.NotEqual(RoomConfidence.Confirmed, tracker.State.Confidence);

        // Real path 1717 →N→ 1716 →S→ 1717 →S→ 1718, every display {N,S,W}.
        PassiveStep(tracker, Direction.N, 1716, t0.AddSeconds(1));
        PassiveStep(tracker, Direction.S, 1717, t0.AddSeconds(2));
        PassiveStep(tracker, Direction.S, 1718, t0.AddSeconds(3));

        _out.WriteLine($"passive result: {tracker.State.Confidence} at {tracker.State.CurrentRoom?.Key}");
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(8, 1718), tracker.State.CurrentRoom?.Key);
    }

    // Same walk, but an engine IS attached: the passive path must stand fully down
    // (the gate's own forward localiser owns recovery then), so the tracker never
    // re-anchors on its own and stays unresolved.
    [Fact]
    public void PassiveGrid_EngineAttached_StandsDown()
    {
        RoomTracker tracker = NewTracker();
        tracker.SetEngineAttachedProbe(() => true);
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        tracker.NoteRoomObserved(Obs(1717), t0);
        PassiveStep(tracker, Direction.N, 1716, t0.AddSeconds(1));
        PassiveStep(tracker, Direction.S, 1717, t0.AddSeconds(2));
        PassiveStep(tracker, Direction.S, 1718, t0.AddSeconds(3));

        _out.WriteLine($"engine-attached result: {tracker.State.Confidence} at {tracker.State.CurrentRoom?.Key}");
        Assert.NotEqual(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Null(tracker.State.CurrentRoom);
    }

    // Refuses to guess: an oscillating walk that stays genuinely ambiguous (the
    // footprint never narrows below two of 1716/1717/1718) must NOT re-anchor.
    [Fact]
    public void PassiveGrid_NeverUnique_StaysUnresolved()
    {
        RoomTracker tracker = NewTracker();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        tracker.NoteRoomObserved(Obs(1717), t0);
        PassiveStep(tracker, Direction.S, 1718, t0.AddSeconds(1));
        PassiveStep(tracker, Direction.N, 1717, t0.AddSeconds(2));
        PassiveStep(tracker, Direction.S, 1718, t0.AddSeconds(3));
        PassiveStep(tracker, Direction.N, 1717, t0.AddSeconds(4));

        _out.WriteLine($"ambiguous result: {tracker.State.Confidence} at {tracker.State.CurrentRoom?.Key}");
        Assert.NotEqual(RoomConfidence.Confirmed, tracker.State.Confidence);
    }

    // One manual move plus its landing display — no engine echo, because we're
    // recovering (Suspect/Lost), not tracking a Pending move: the passive footprint
    // reads the move + observation directly.
    private static void PassiveStep(RoomTracker tracker, Direction dir, int landedRoom, DateTimeOffset when)
    {
        tracker.NoteMoveSent(dir, when);
        tracker.NoteRoomObserved(Obs(landedRoom), when.AddMilliseconds(100));
    }
}
