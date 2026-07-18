using System.Collections.Generic;
using System.IO;
using FujinTerm.Game.Map;
using FujinTerm.Game.Spells;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

// Covers TeleportMazeIndex: structural detection of a random-teleport pocket
// (the Warped Asylum shape) and the 1x2-signature relocalization lookup. The
// synthetic pocket below mirrors the real thing — a one-way cast-pocket mouth
// into a cluster of same-named corridors, each with a random-teleport
// ("reshuffle") exit, plus dead-end cells reached only by walking.
public sealed class TeleportMazeIndexTests : IDisposable
{
    private readonly string _root;

    public TeleportMazeIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-maze-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private GameDataCache NewCache() => new(_root);

    private void SeedTable(string setName, string table, string json)
    {
        string dir = Path.Combine(_root, setName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, table + ".json"), json);
    }

    private TeleportMazeIndex BuildIndex(string roomsJson, string spellsJson, string tbinfoJson = "[]")
    {
        SeedTable("alpha", "Rooms", roomsJson);
        SeedTable("alpha", "TBInfo", tbinfoJson);
        SeedTable("alpha", "Spells", spellsJson);
        GameDataCache cache = NewCache();
        cache.SwitchSet("alpha");
        TBInfoStore tbinfo = new(cache);
        tbinfo.OnActiveSetChanged("alpha");
        KnownSpellCatalog spells = new(cache);
        RoomGraphManager graph = new(cache, log: null, tbinfo, spells);
        graph.OnActiveSetChanged("alpha");
        return new TeleportMazeIndex(graph);
    }

    // Spell 596: random teleport (Abil 140 value 0, range 10..21). Spell 620:
    // fixed teleport (Abil 140 value 5) — a deterministic cast that mints a
    // pocket entrance but no reshuffle exit.
    private const string MazeSpells = """
        [
          { "Number": 596, "Name": "warp", "Short": "wrp",
            "MinBase": 10, "MaxBase": 21,
            "Abil-0": 140, "AbilVal-0": 0, "Abil-1": 141, "AbilVal-1": 1 },
          { "Number": 620, "Name": "recall", "Short": "rcl",
            "MinBase": 0, "MaxBase": 0,
            "Abil-0": 140, "AbilVal-0": 5, "Abil-1": 141, "AbilVal-1": 1 }
        ]
        """;

    // A one-way cast mouth (1/1 S → 1/10, casting random-teleport 596) into a
    // four-corridor cluster. Each corridor's D exit re-casts 596 (the reshuffle).
    // 1/10 and 1/11 share an own-exit mask (E|W|D) — only the looked-at neighbour
    // masks tell them apart, which is exactly what the 1x2 signature exists for.
    // 1/20 / 1/21 are dead-end Padded Cells reached by a plain cardinal walk.
    private const string MazeRooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Courtyard",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "1/10 (Cast: pre-0, post-596)", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Gate",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 10, "Name": "Warped Asylum",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/11", "W": "1/12",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "1/11 (Cast: pre-0, post-596)" },
          { "Map Number": 1, "Room Number": 11, "Name": "Warped Asylum",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/13", "W": "1/10",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "1/10 (Cast: pre-0, post-596)" },
          { "Map Number": 1, "Room Number": 12, "Name": "Warped Asylum",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/20", "E": "1/10", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "1/10 (Cast: pre-0, post-596)" },
          { "Map Number": 1, "Room Number": 13, "Name": "Warped Asylum",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/21", "E": "0", "W": "1/11",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "1/10 (Cast: pre-0, post-596)" },
          { "Map Number": 1, "Room Number": 20, "Name": "Padded Cell",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "1/12", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 21, "Name": "Padded Cell",
            "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
            "N": "1/13", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Own-exit masks for the corridors (cardinal bit = 1u << (int)dir):
    // E=1<<2, W=1<<3, S=1<<1, N=1<<0, D=1<<9.
    private const uint MaskEWD = (1u << 2) | (1u << 3) | (1u << 9); // 1/10, 1/11
    private const uint MaskSED = (1u << 1) | (1u << 2) | (1u << 9); // 1/12
    private const uint MaskSWD = (1u << 1) | (1u << 3) | (1u << 9); // 1/13
    private const uint MaskN   = (1u << 0);                          // cells

    [Fact]
    public void DetectsPocketMembers_ButNotRoomsOutsideTheMouth()
    {
        TeleportMazeIndex index = BuildIndex(MazeRooms, MazeSpells);

        Assert.True(index.HasMazes);
        Assert.True(index.IsMazeRoom(new RoomKey(1, 10)));
        Assert.True(index.IsMazeRoom(new RoomKey(1, 13)));
        Assert.True(index.IsMazeRoom(new RoomKey(1, 20)));   // dead-end cell still a member
        Assert.False(index.IsMazeRoom(new RoomKey(1, 1)));   // the mouth room is outside
        Assert.False(index.IsMazeRoom(new RoomKey(1, 2)));   // overworld
    }

    [Fact]
    public void TryGetEntrance_ResolvesTheOneWayMouth_ForEveryPocketMember()
    {
        TeleportMazeIndex index = BuildIndex(MazeRooms, MazeSpells);

        // Every pocket member resolves to the same mouth: 1/1 walking S.
        foreach (int room in new[] { 10, 11, 12, 13, 20, 21 })
        {
            Assert.True(index.TryGetEntrance(new RoomKey(1, room), out RoomKey src, out Direction dir));
            Assert.Equal(new RoomKey(1, 1), src);
            Assert.Equal(Direction.S, dir);
        }

        // Outside rooms have no entrance record.
        Assert.False(index.TryGetEntrance(new RoomKey(1, 1), out _, out _));
        Assert.False(index.TryGetEntrance(new RoomKey(1, 2), out _, out _));
    }

    [Fact]
    public void ReshuffleDirections_AreTheCastTeleportExits()
    {
        TeleportMazeIndex index = BuildIndex(MazeRooms, MazeSpells);

        Assert.Equal(new[] { Direction.D }, index.ReshuffleDirections(new RoomKey(1, 10)));
        Assert.Empty(index.ReshuffleDirections(new RoomKey(1, 20)));  // cell — no cast exit
        Assert.Empty(index.ReshuffleDirections(new RoomKey(1, 1)));   // non-maze room
    }

    [Fact]
    public void TryIdentify_DisambiguatesTwins_ByNeighbourMasks()
    {
        TeleportMazeIndex index = BuildIndex(MazeRooms, MazeSpells);

        // 1/10 and 1/11 share MaskEWD; only the neighbour masks separate them.
        var landedOn10 = new Dictionary<Direction, uint>
        {
            { Direction.E, MaskEWD },   // → 1/11
            { Direction.W, MaskSED },   // → 1/12
            { Direction.D, MaskEWD },   // reshuffle target 1/11
        };
        Assert.True(index.TryIdentify(MaskEWD, landedOn10, out RoomKey k10));
        Assert.Equal(new RoomKey(1, 10), k10);

        var landedOn11 = new Dictionary<Direction, uint>
        {
            { Direction.E, MaskSWD },   // → 1/13
            { Direction.W, MaskEWD },   // → 1/10
            { Direction.D, MaskEWD },   // reshuffle target 1/10
        };
        Assert.True(index.TryIdentify(MaskEWD, landedOn11, out RoomKey k11));
        Assert.Equal(new RoomKey(1, 11), k11);
    }

    [Fact]
    public void TryIdentify_Fails_WhenALookDidNotResolve()
    {
        TeleportMazeIndex index = BuildIndex(MazeRooms, MazeSpells);

        // Missing the D neighbour mask — the signature can't be completed.
        var incomplete = new Dictionary<Direction, uint>
        {
            { Direction.E, MaskEWD },
            { Direction.W, MaskSED },
        };
        Assert.False(index.TryIdentify(MaskEWD, incomplete, out _));
    }

    [Fact]
    public void TryIdentify_Fails_WhenSignatureMatchesNothing()
    {
        TeleportMazeIndex index = BuildIndex(MazeRooms, MazeSpells);

        var bogus = new Dictionary<Direction, uint>
        {
            { Direction.E, 999 },
            { Direction.W, 999 },
            { Direction.D, 999 },
        };
        Assert.False(index.TryIdentify(MaskEWD, bogus, out _));
    }

    [Fact]
    public void DeterministicPocket_WithNoRandomTeleport_IsNotIndexed()
    {
        // Same one-way mouth shape, but the entrance casts the FIXED teleport 620
        // and the interior has no reshuffle exit — a deterministic pocket that
        // plain BFS already handles, so the maze index leaves it alone.
        const string rooms = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Courtyard",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/10 (Cast: pre-0, post-620)", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 10, "Name": "Sealed Chamber",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "1/11", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 11, "Name": "Sealed Chamber",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "0", "W": "1/10",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        TeleportMazeIndex index = BuildIndex(rooms, MazeSpells);

        Assert.False(index.HasMazes);
        Assert.False(index.IsMazeRoom(new RoomKey(1, 10)));
    }

    // Regression guard for the Paradigm-1.9.1 Warped Asylum lever ignore
    // (RoomGraphManager.ParadigmAsylumLeverRoom). Room 9/1259 sits inside a
    // one-way cast pocket but carries a `pull lever` CMD teleport (CMD 10243)
    // back to the outside entry 9/1180. If that escape were synthesised as a
    // routable Teleport edge, pocket detection would walk it back out and the
    // asylum would never be flagged/indexed as a maze — the exact Paradigm bug.
    // Skipping just this room's synthesis keeps the pocket one-way, so it detects
    // the same as stock (whose 9/1259 has no lever).
    [Fact]
    public void ParadigmAsylumLever_DoesNotDefeatPocketDetection()
    {
        const string rooms = """
            [
              { "Map Number": 9, "Room Number": 1, "Name": "Courtyard",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "9/1183 (Cast: pre-0, post-596)", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 9, "Room Number": 1183, "Name": "Warped Asylum",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "9/1259", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "9/1259 (Cast: pre-0, post-596)" },
              { "Map Number": 9, "Room Number": 1259, "Name": "Warped Asylum",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 10243, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "0", "W": "9/1183",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 9, "Room Number": 1180, "Name": "Asylum Entrance",
                "Light": 0, "Shop": 0, "Spell": 0, "CMD": 0, "Lair": "", "Delay": 0,
                "N": "9/1", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        // CMD 10243 → `pull lever` teleporting to 9/1180 (the escape we ignore).
        const string tbinfo = """
            [ { "Number": 10243, "LinkTo": 0,
                "Action": "pull lever:teleport 1180 9\n",
                "Called From": "Room 9/1259" } ]
            """;
        TeleportMazeIndex index = BuildIndex(rooms, MazeSpells, tbinfo);

        Assert.True(index.HasMazes);
        Assert.True(index.IsMazeRoom(new RoomKey(9, 1183)));
        Assert.True(index.IsMazeRoom(new RoomKey(9, 1259)));   // the lever room is a member
        Assert.False(index.IsMazeRoom(new RoomKey(9, 1180)));  // escape target stays outside
        Assert.False(index.IsMazeRoom(new RoomKey(9, 1)));     // mouth source is outside
    }
}
