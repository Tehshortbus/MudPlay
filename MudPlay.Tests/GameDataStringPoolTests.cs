using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MudPlay.Game.Combat;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The structures built from a game-data load keep each distinct text once, through
// the load's StringPool: the room graph's names, lair tags and exit hints, the room-
// name index's names, the monster catalogue's attack names. The pooling must change
// nothing but the copies: every text reads exactly as the file wrote it (case,
// whitespace, characters past the BMP), texts that differ stay apart, and a set
// switch starts a new pool so the old set's strings can go.
public sealed class GameDataStringPoolTests : IDisposable
{
    private const string RoomsJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Windscar Plateau", "Lair": "(Max 3): 7,[20-15-15-10]",
            "N": "1/2 (Class: 6 OK, 0 NO)", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Windscar Plateau", "Lair": "(Max 3): 7,[20-15-15-10]",
            "N": "1/3 (Class: 6 OK, 0 NO)", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "windscar plateau", "Lair": "\u0000",
            "N": "0", "S": "1/2", "E": "1/4", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Windscar  Plateau ", "Lair": "\u0000",
            "N": "0", "S": "0", "E": "0", "W": "1/3", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 2, "Room Number": 1, "Name": "Dragon's Teeth Hills é 🐉", "Lair": "\u0000",
            "N": "0", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 2, "Room Number": 2, "Name": "Dragon's Teeth Hills é 🐉", "Lair": "\u0000",
            "N": "0", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 2, "Room Number": 3, "Name": "", "Lair": "\u0000",
            "N": "0", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private const string MonstersJson = """
        [
          { "Number": 1, "Name": "orc", "AttType-1": 1, "AttName-1": "hits", "AttType-2": 1, "AttName-2": "Hits" },
          { "Number": 2, "Name": "goblin", "AttType-1": 1, "AttName-1": "hits", "AttType-2": 1, "AttName-2": "bites " },
          { "Number": 3, "Name": "kobold", "AttType-1": 1, "AttName-1": "bites " }
        ]
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mudplay-strpool-" + Path.GetRandomFileName());
    private readonly GameDataCache _cache;
    private readonly RoomGraphManager _graph;

    public GameDataStringPoolTests()
    {
        foreach (string set in new[] { "alpha", "beta" })
        {
            string dir = Path.Combine(_root, set);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Rooms.json"), RoomsJson);
            File.WriteAllText(Path.Combine(dir, "Monsters.json"), MonstersJson);
        }
        // As AppServices wires them.
        _cache = new GameDataCache(_root);
        _graph = new RoomGraphManager(_cache);
        _cache.ActiveSetChanged += _graph.OnActiveSetChanged;
        _cache.SwitchSet("alpha");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    // The file's own texts, read by System.Text.Json: what every value must equal.
    private static Dictionary<(int, int), JsonElement> FileRooms() =>
        JsonDocument.Parse(RoomsJson).RootElement.EnumerateArray()
            .ToDictionary(r => (r.GetProperty("Map Number").GetInt32(), r.GetProperty("Room Number").GetInt32()));

    private Room Room(int map, int room) => _graph.GetRoom(new RoomKey(map, room))!;

    [Fact]
    public void EveryTextReadsExactlyAsTheFileWroteIt()
    {
        RoomTable<string> names = RoomNameIndex.For(_cache);
        foreach (((int map, int room), JsonElement row) in FileRooms())
        {
            string expected = row.GetProperty("Name").GetString()!;
            Assert.Equal(expected, Room(map, room).Name, StringComparer.Ordinal);
            Assert.Equal(expected, names[new RoomKey(map, room)], StringComparer.Ordinal);
            string lair = row.GetProperty("Lair").GetString()!;
            Assert.Equal(lair == "\0" ? null : lair, Room(map, room).RawLairTag);
        }
        Assert.Equal("Class: 6 OK, 0 NO", Room(1, 1).Exits[Direction.N].RawHint);

        MonsterCatalog monsters = new(_cache);
        Dictionary<int, MonsterCatalogEntry> byNumber = monsters.All.ToDictionary(m => m.Number);
        foreach (JsonElement row in JsonDocument.Parse(MonstersJson).RootElement.EnumerateArray())
        {
            MonsterCatalogEntry entry = byNumber[row.GetProperty("Number").GetInt32()];
            string[] expected = Enumerable.Range(1, 5)
                .Where(i => row.TryGetProperty($"AttName-{i}", out _))
                .Select(i => row.GetProperty($"AttName-{i}").GetString()!).ToArray();
            Assert.Equal(expected, entry.Attacks.Select(a => a.Name));
        }
    }

    [Fact]
    public void ARepeatedTextIsOneString_AcrossRoomsAndIndexes_AndTextsThatDifferStayApart()
    {
        RoomTable<string> names = RoomNameIndex.For(_cache);

        Assert.Same(Room(1, 1).Name, Room(1, 2).Name);
        Assert.Same(Room(1, 1).Name, names[new RoomKey(1, 2)]);
        Assert.Same(Room(2, 1).Name, names[new RoomKey(2, 2)]);
        Assert.Same(Room(1, 1).RawLairTag, Room(1, 2).RawLairTag);
        Assert.Same(Room(1, 1).Exits[Direction.N].RawHint, Room(1, 2).Exits[Direction.N].RawHint);
        // Case and whitespace are part of the text.
        Assert.Equal(new[] { "Windscar Plateau", "windscar plateau", "Windscar  Plateau " },
            new[] { Room(1, 1).Name, Room(1, 3).Name, Room(1, 4).Name });

        MonsterCatalog monsters = new(_cache);
        MonsterAttackSlot[] slots = monsters.All.SelectMany(m => m.Attacks).ToArray();
        Assert.Same(slots.First(s => s.Name == "hits").Name, slots.Last(s => s.Name == "hits").Name);
        Assert.Same(slots.First(s => s.Name == "bites ").Name, slots.Last(s => s.Name == "bites ").Name);
        Assert.Equal(new[] { "hits", "Hits", "hits", "bites ", "bites " }, slots.Select(s => s.Name));
    }

    [Fact]
    public void ASetSwitchStartsANewPool_SoTheOldSetsStringsAreNotKept()
    {
        StringPool alpha = _cache.Strings;
        string alphaName = Room(1, 1).Name;
        Assert.True(alpha.Count > 0);

        _cache.SwitchSet("beta");

        Assert.NotSame(alpha, _cache.Strings);
        Assert.Equal(alphaName, Room(1, 1).Name);
        Assert.NotSame(alphaName, Room(1, 1).Name);
        Assert.Same(Room(1, 1).Name, RoomNameIndex.For(_cache)[new RoomKey(1, 2)]);
    }
}
