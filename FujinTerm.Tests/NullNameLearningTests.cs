using System.IO;
using System.Text.Json;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Coverage for the null-name learning flow added when the player
/// stumbles into a ganghouse room whose <c>Name</c> shipped as null
/// in the MDB export.
/// </summary>
public sealed class NullNameLearningTests : IDisposable
{
    private readonly string _root;

    public NullNameLearningTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-nullname-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 1/1 "Oak Street" — south → 15/861 (null-name, N back to 1/321).
    // We use 1/321 ↔ 15/861 layout that mirrors the real-world bug.
    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 321, "Name": "Oak Street",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "15/861", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 15, "Room Number": 861, "Name": null,
            "Light": 0, "Shop": 125, "Lair": "", "Delay": 5,
            "N": "1/321", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private (RoomGraphManager Graph, RoomTracker Tracker, GameDataCache Cache) NewTracker()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return (graph, new RoomTracker(graph), cache);
    }

    // ----- Room.DisplayName + HasUnknownName ------------------------

    [Fact]
    public void NullName_DisplayNameShowsPlaceholder()
    {
        (RoomGraphManager graph, _, _) = NewTracker();
        Room ganghouse = graph.GetRoom(new RoomKey(15, 861))!;
        Assert.True(ganghouse.HasUnknownName);
        Assert.Equal("???", ganghouse.DisplayName);
    }

    [Fact]
    public void NormalName_DisplayNameUsesRawName()
    {
        (RoomGraphManager graph, _, _) = NewTracker();
        Room oak = graph.GetRoom(new RoomKey(1, 321))!;
        Assert.False(oak.HasUnknownName);
        Assert.Equal("Oak Street", oak.DisplayName);
    }

    // ----- Tracker null-name neighbour matching ---------------------

    [Fact]
    public void Observation_OfNullNameNeighbour_LandsAndLearnsName()
    {
        (_, RoomTracker tracker, _) = NewTracker();
        tracker.SetLocated(new RoomKey(1, 321));

        NameLearnedEvent? captured = null;
        tracker.NameLearned += e => captured = e;

        // Server display arrives — observed name "Shop White House Room"
        // with exits {N}; matches the null-name neighbour at 15/861.
        tracker.NoteRoomObserved(new RoomObservation(
            "Shop White House Room",
            new HashSet<Direction> { Direction.N }));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(15, 861), tracker.State.CurrentRoom!.Key);
        Assert.Equal("Shop White House Room", tracker.State.CurrentRoom.Name);

        Assert.NotNull(captured);
        Assert.Equal(new RoomKey(15, 861), captured!.Value.Key);
        Assert.Equal("Shop White House Room", captured.Value.ObservedName);
    }

    [Fact]
    public void Observation_AfterLearn_FindsRoomByName()
    {
        (RoomGraphManager graph, RoomTracker tracker, _) = NewTracker();
        tracker.SetLocated(new RoomKey(1, 321));
        tracker.NoteRoomObserved(new RoomObservation(
            "Shop White House Room",
            new HashSet<Direction> { Direction.N }));

        // Subsequent searches by the learned name resolve.
        IReadOnlyList<RoomKey> hits = graph.FindCandidates(
            "Shop White House Room",
            new HashSet<Direction> { Direction.N });
        Assert.Single(hits);
        Assert.Equal(new RoomKey(15, 861), hits[0]);
    }

    // ----- Persistence ---------------------------------------------

    [Fact]
    public void Persist_WritesNameBackToRoomsJson()
    {
        (_, _, GameDataCache cache) = NewTracker();
        RoomNamePersistence persister = new(cache);

        bool ok = persister.Persist(new RoomKey(15, 861), "Shop White House Room");
        Assert.True(ok);

        // Re-read the file and confirm Name was updated.
        string json = File.ReadAllText(Path.Combine(_root, "alpha", "Rooms.json"));
        JsonDocument doc = JsonDocument.Parse(json);
        bool found = false;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            int mn = row.GetProperty("Map Number").GetInt32();
            int rn = row.GetProperty("Room Number").GetInt32();
            if (mn != 15 || rn != 861) continue;
            Assert.Equal("Shop White House Room", row.GetProperty("Name").GetString());
            found = true;
            break;
        }
        Assert.True(found, "15/861 must be present after persist");
    }

    [Fact]
    public void Persist_UnknownKey_ReturnsFalseAndLeavesFileIntact()
    {
        (_, _, GameDataCache cache) = NewTracker();
        string before = File.ReadAllText(Path.Combine(_root, "alpha", "Rooms.json"));

        RoomNamePersistence persister = new(cache);
        bool ok = persister.Persist(new RoomKey(99, 99), "Phantom Room");

        Assert.False(ok);
        Assert.Equal(before, File.ReadAllText(Path.Combine(_root, "alpha", "Rooms.json")));
    }

    [Fact]
    public void Persist_EmptyName_ReturnsFalse()
    {
        (_, _, GameDataCache cache) = NewTracker();
        RoomNamePersistence persister = new(cache);
        Assert.False(persister.Persist(new RoomKey(15, 861), ""));
        Assert.False(persister.Persist(new RoomKey(15, 861), "   "));
    }
}
