using System.Collections.Generic;
using System.IO;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A recovery-driven resume (RoomTracker.SetLocated) re-raises StateChanged for a
// reactor registered early, as Roomba's recon dispatch registers: a room placed by
// a resync is still reconned.
public sealed class GhSweepRecoveryResyncTests : IDisposable
{
    private readonly string _root;

    public GhSweepRecoveryResyncTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-ghsweep-resync-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private RoomGraphManager NewGraph()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return graph;
    }

    [Fact]
    public void SetLocated_FiresGenuineStateChanged_ForEarlyRegisteredReactor()
    {
        // ParadigmPositionResolver.OnLocationObserved calls _tracker.SetLocated(key)
        // when an `rm` reply resolves — this is the one path that must produce a
        // real RoomTransition (NewRoom != PreviousRoom) for GhSweepManager's early
        // wrapper subscriber to react to, or a resync-resolved room's recon
        // dispatch never fires.
        RoomTracker tracker = new(NewGraph());
        List<RoomTransition> seen = new();
        tracker.StateChanged += t => seen.Add(t);

        tracker.SetLocated(new RoomKey(1, 1));
        seen.Clear();
        tracker.SetLocated(new RoomKey(1, 2));

        RoomTransition t = Assert.Single(seen);
        Assert.NotNull(t.NewRoom);
        Assert.Equal(new RoomKey(1, 2), t.NewRoom!.Key);
        Assert.NotNull(t.PreviousRoom);
        Assert.Equal(new RoomKey(1, 1), t.PreviousRoom!.Key);
    }
}
