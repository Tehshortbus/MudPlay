using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A too-dark room reveals its occupant a beat AFTER the move confirms, but the
// dark advance dead-reckons and fires StateChanged synchronously — so without a
// hold the loop sends the next move before the monster surfaces and marches past
// the fight. DarkRoomMovementSettle asserts a short-lived settle gate on each
// dark advance to buy that beat; these tests pin that it (a) asserts only on a
// real dark-room move, (b) self-clears when the window elapses, and (c) stays
// silent on a normal lit arrival.
public sealed class DarkRoomMovementSettleTests : IDisposable
{
    private readonly string _root;

    public DarkRoomMovementSettleTests()
    {
        _root = Path.Combine(Path.GetTempPath(),
            "mudplay-darksettle-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // Town Gates (1/1) N → North Square (1/3), so a pending N move dead-reckons
    // onto a mapped neighbour when the dark line lands.
    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Town Gates",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/3", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "North Square",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private RoomTracker NewTracker()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return new RoomTracker(graph);
    }

    private static RoomObservation Obs(string name, params Direction[] exits)
        => new(name, new HashSet<Direction>(exits));

    [Fact]
    public void DarkRoomAdvance_AssertsSettleGate()
    {
        RoomTracker tracker = NewTracker();
        MovementCoordinator coord = new();
        Action? scheduled = null;
        using DarkRoomMovementSettle settle = new(
            tracker, coord, scheduleOverride: (_, act) => scheduled = act);

        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteMoveSent(Direction.N);
        tracker.NoteDarkRoomEntered();

        Assert.True(tracker.IsInDarkRoom);
        Assert.Contains(MovementCoordinator.DarkRoomSettleGate, coord.AssertedGates);
        Assert.NotNull(scheduled);
    }

    [Fact]
    public void SettleWindowElapsed_ClearsGate()
    {
        RoomTracker tracker = NewTracker();
        MovementCoordinator coord = new();
        Action? scheduled = null;
        using DarkRoomMovementSettle settle = new(
            tracker, coord, scheduleOverride: (_, act) => scheduled = act);

        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteMoveSent(Direction.N);
        tracker.NoteDarkRoomEntered();
        Assert.Contains(MovementCoordinator.DarkRoomSettleGate, coord.AssertedGates);

        // Fire the captured settle callback as the timer would.
        scheduled!.Invoke();

        Assert.DoesNotContain(MovementCoordinator.DarkRoomSettleGate, coord.AssertedGates);
    }

    [Fact]
    public void LitRoomArrival_DoesNotAssert()
    {
        RoomTracker tracker = NewTracker();
        MovementCoordinator coord = new();
        using DarkRoomMovementSettle settle = new(
            tracker, coord, scheduleOverride: (_, _) => { });

        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteMoveSent(Direction.N);
        // A normal lit room prints its name + exits — never trips the dark hold.
        tracker.NoteRoomObserved(Obs("North Square", Direction.S));

        Assert.False(tracker.IsInDarkRoom);
        Assert.DoesNotContain(MovementCoordinator.DarkRoomSettleGate, coord.AssertedGates);
    }

    [Fact]
    public void ConsecutiveDarkAdvances_HoldOneGate_TheSettleReleasesIt()
    {
        RoomTracker tracker = NewTracker();
        MovementCoordinator coord = new();
        Action? scheduled = null;
        using DarkRoomMovementSettle settle = new(
            tracker, coord, scheduleOverride: (_, act) => scheduled = act);

        tracker.SetLocated(new RoomKey(1, 1));
        tracker.NoteMoveSent(Direction.N);
        tracker.NoteDarkRoomEntered();               // 1/1 → 1/3, dark
        tracker.NoteMoveSent(Direction.S);
        tracker.NoteDarkRoomEntered();               // 1/3 → 1/1, still dark

        // Idempotent assert — one gate held across both advances.
        Assert.Contains(MovementCoordinator.DarkRoomSettleGate, coord.AssertedGates);

        scheduled!.Invoke();
        Assert.DoesNotContain(MovementCoordinator.DarkRoomSettleGate, coord.AssertedGates);
    }
}
