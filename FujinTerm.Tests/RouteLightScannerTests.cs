using System.Collections.Generic;
using FujinTerm.Game.Light;
using FujinTerm.Game.Map;
using Xunit;

namespace FujinTerm.Tests;

public sealed class RouteLightScannerTests
{
    private static Room RoomAt(RoomKey key, int light) => new()
    {
        Key = key,
        Name = "r" + key,
        Light = light,
        Exits = new Dictionary<Direction, RoomExit>(),
    };

    // A small graph the scanner resolves against; unlisted keys resolve to null
    // (route rooms outside the active set).
    private static Func<RoomKey, Room?> GraphOf(params Room[] rooms)
    {
        var map = new Dictionary<RoomKey, Room>();
        foreach (Room r in rooms) map[r.Key] = r;
        return k => map.TryGetValue(k, out Room? room) ? room : null;
    }

    private static readonly RoomKey A = new(1, 100);
    private static readonly RoomKey B = new(1, 101);
    private static readonly RoomKey C = new(1, 102);

    [Fact]
    public void Scan_EmptyRoute_IsEmpty()
    {
        RouteLightScan scan = RouteLightScanner.Scan(
            System.Array.Empty<RoomKey>(), GraphOf(), charIllu: 0);

        Assert.Equal(RouteLightScan.Empty, scan);
        Assert.False(scan.NeedsLight);
    }

    [Fact]
    public void Scan_AllLitRooms_NeedsNoLight()
    {
        Func<RoomKey, Room?> graph = GraphOf(RoomAt(A, 0), RoomAt(B, 0), RoomAt(C, 0));

        RouteLightScan scan = RouteLightScanner.Scan(new[] { A, B, C }, graph, charIllu: 0);

        Assert.Equal(3, scan.RoomCount);
        Assert.Equal(0, scan.DarkestRoomLight);
        Assert.Equal(0, scan.NeededLightStrength);
        Assert.False(scan.NeedsLight);
    }

    [Fact]
    public void Scan_DarkestRoom_DrivesNeededStrength()
    {
        // B is the darkest at -300; unlit worn illu → gap to -150 is 150.
        Func<RoomKey, Room?> graph = GraphOf(RoomAt(A, -50), RoomAt(B, -300), RoomAt(C, -120));

        RouteLightScan scan = RouteLightScanner.Scan(new[] { A, B, C }, graph, charIllu: 0);

        Assert.Equal(3, scan.RoomCount);
        Assert.Equal(-300, scan.DarkestRoomLight);
        Assert.Equal(B, scan.DarkestRoom);
        Assert.Equal(150, scan.NeededLightStrength);
        Assert.True(scan.NeedsLight);
    }

    [Fact]
    public void Scan_WornIlluShrinksNeededStrength()
    {
        Func<RoomKey, Room?> graph = GraphOf(RoomAt(A, -300));

        // +50 worn illu means only a 100-strength light is needed for the -300 room.
        RouteLightScan scan = RouteLightScanner.Scan(new[] { A }, graph, charIllu: 50);

        Assert.Equal(100, scan.NeededLightStrength);
    }

    [Fact]
    public void Scan_SkipsUnresolvedRooms()
    {
        // Only A and C exist; B is off-set and resolves to null — it must not
        // count nor influence the darkest reading.
        Func<RoomKey, Room?> graph = GraphOf(RoomAt(A, -100), RoomAt(C, -160));

        RouteLightScan scan = RouteLightScanner.Scan(new[] { A, B, C }, graph, charIllu: 0);

        Assert.Equal(2, scan.RoomCount);
        Assert.Equal(-160, scan.DarkestRoomLight);
        Assert.Equal(C, scan.DarkestRoom);
        Assert.Equal(10, scan.NeededLightStrength); // -150 - (-160)
    }

    [Fact]
    public void Scan_DarkRouteAlreadyCoveredByIllu_NeedsNoLight()
    {
        // A -140 room with 0 worn illu is already seeable (V = -140 ≥ -150).
        Func<RoomKey, Room?> graph = GraphOf(RoomAt(A, -140));

        RouteLightScan scan = RouteLightScanner.Scan(new[] { A }, graph, charIllu: 0);

        Assert.Equal(-140, scan.DarkestRoomLight);
        Assert.Equal(0, scan.NeededLightStrength);
        Assert.False(scan.NeedsLight);
    }
}
