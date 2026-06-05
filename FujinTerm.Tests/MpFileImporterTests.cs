using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FujinTerm.Game.Map;
using FujinTerm.Game.Map.MpFile;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// End-to-end importer tests over a hand-built tiny graph. The
/// <see cref="MpFileParser"/> + <see cref="MegaMudHash"/> tests cover
/// the structural / hash-encoding edges; these tests cover the
/// candidate filter + closure walk + multi-candidate ranking that
/// only fire against a real <see cref="RoomGraphManager"/>.
/// </summary>
public sealed class MpFileImporterTests : IDisposable
{
    private readonly string _root;

    public MpFileImporterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-mpimporter-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ----- StripMapRoomSuffix ---------------------------------------

    [Theory]
    [InlineData("Webbed Lair (complete)-17 2674", "Webbed Lair (complete)")]
    [InlineData("Ancient Crypt-1 1943",           "Ancient Crypt")]
    [InlineData("Crypt Level 1 Loop-1 1028",      "Crypt Level 1 Loop")]
    [InlineData("Bone Warriors-17 2647",          "Bone Warriors")]
    [InlineData("Undermountain Level 20-16 1743", "Undermountain Level 20")]
    [InlineData("VU20 Loop",                      "VU20 Loop")]   // no suffix → unchanged
    [InlineData("Crypt Level 1",                  "Crypt Level 1")]
    [InlineData("",                               "")]
    public void StripMapRoomSuffix_Cases(string input, string expected)
        => Assert.Equal(expected, MpFileImporter.StripMapRoomSuffix(input));

    // ----- happy-path end-to-end -----------------------------------

    // Smallest viable closed loop: rooms 1/1 and 1/2 wired so
    // 1/1 -N→ 1/2 and 1/2 -S→ 1/1. A two-step N,S walk closes back
    // on 1/1.
    private const string MicroGraph = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Start",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "End",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private RoomGraphManager BuildGraph()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), MicroGraph);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return graph;
    }

    [Fact]
    public void Resolve_UniqueClosedLoop_ReturnsThatRoom()
    {
        RoomGraphManager graph = BuildGraph();

        // "Start" hashes to whatever ComputeNameHash produces; we
        // compute the recorded hashExits for room 1/1 (exits: N only)
        // so the file's start-hash matches exactly.
        Room start = graph.GetRoom(new RoomKey(1, 1))!;
        string startHash = MegaMudHash.ComputeHashExits(start.Name,
            new HashSet<Direction>(start.Exits.Keys));

        Room mid = graph.GetRoom(new RoomKey(1, 2))!;
        string midHash = MegaMudHash.ComputeHashExits(mid.Name,
            new HashSet<Direction>(mid.Exits.Keys));

        string text =
            "[Test loop][]\n" +
            $"[TEST:Group:Start]\n" +
            $"{startHash}:{startHash}:2:-1:0:::\n" +
            $"{startHash}:0000:n\n" +
            $"{midHash}:0000:s\n";
        MpLoopFile file = MpFileParser.Parse(text);

        MpFileImporter importer = new(graph);
        MpImportResolution res = importer.Resolve(file);

        Assert.False(res.Failed);
        Assert.True(res.HasUniqueBest);
        Assert.Equal(new RoomKey(1, 1), res.BestCandidates[0].AnchorKey);
        Assert.Equal(0, res.BestCandidates[0].HashMismatches);
    }

    [Fact]
    public void Resolve_NoMatch_ReturnsFailWithReason()
    {
        RoomGraphManager graph = BuildGraph();
        // Hash for a name not in the graph.
        string madeUp = "FFF" + "00000";
        string text =
            "[Test][]\n" +
            "[TEST:Group:Nothing]\n" +
            $"{madeUp}:{madeUp}:2:-1:0:::\n" +
            $"{madeUp}:0000:n\n" +
            $"{madeUp}:0000:s\n";
        MpLoopFile file = MpFileParser.Parse(text);

        MpFileImporter importer = new(graph);
        MpImportResolution res = importer.Resolve(file);

        Assert.True(res.Failed);
        Assert.NotNull(res.Error);
    }

    [Fact]
    public void BuildLoop_FromResolution_FillsWaypointsAndStripsSuffix()
    {
        RoomGraphManager graph = BuildGraph();
        Room start = graph.GetRoom(new RoomKey(1, 1))!;
        Room mid = graph.GetRoom(new RoomKey(1, 2))!;
        string startHash = MegaMudHash.ComputeHashExits(start.Name,
            new HashSet<Direction>(start.Exits.Keys));
        string midHash = MegaMudHash.ComputeHashExits(mid.Name,
            new HashSet<Direction>(mid.Exits.Keys));

        string text =
            "[Test Loop-1 1][Tester]\n" +
            "[TEST:Group:Start]\n" +
            $"{startHash}:{startHash}:2:-1:0:::\n" +
            $"{startHash}:0000:n\n" +
            $"{midHash}:0000:s\n";
        MpLoopFile file = MpFileParser.Parse(text);

        MpFileImporter importer = new(graph);
        Loop? loop = importer.BuildLoop(file, new RoomKey(1, 1));

        Assert.NotNull(loop);
        Assert.Equal("Test Loop", loop!.Name);   // -1 1 suffix stripped
        Assert.Equal(2, loop.Waypoints.Count);
        Assert.Equal(new RoomKey(1, 1), loop.Waypoints[0].Key);
        Assert.Equal(new RoomKey(1, 2), loop.Waypoints[1].Key);
        Assert.Contains("Tester", loop.Notes);
    }
}
