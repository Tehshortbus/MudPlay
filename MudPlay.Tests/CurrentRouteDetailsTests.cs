using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// CurrentRouteDetails.Build — the "Details…" step plan for the route the nav engine
// is currently executing. Turns the active route's room-key polyline into the
// route-picker's numbered rows and attaches each room's lair monsters.
public sealed class CurrentRouteDetailsTests : IDisposable
{
    private readonly string _root;

    public CurrentRouteDetailsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-routedetails-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // A short line: 1/1(Home) ─N─ 1/2(Cavern) ─N─ 1/3(Deep).
    private const string Rooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Home", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "NPC": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Cavern", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "NPC": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Deep", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "NPC": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // 1/1(Ledge) ─N─ 1/2(Cliff) ─N(Item: 474)─ 1/3(Below): the 1/2→1/3 hop is
    // gated on carrying item 474 (a rope & grapple).
    private const string ItemGateRooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Ledge", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "NPC": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Cliff", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "NPC": 0,
            "N": "1/3 (Item: 474)", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Below", "CMD": 0,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "NPC": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private RoomGraphManager NewGraph() => NewGraph(Rooms);
    private RoomGraphManager NewItemGateGraph() => NewGraph(ItemGateRooms);

    private RoomGraphManager NewGraph(string roomsJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), roomsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return graph;
    }

    [Fact]
    public void Build_RendersStepRows_AndAttachesMonstersToTheirRoom()
    {
        RoomGraphManager graph = NewGraph();
        var route = new[] { new RoomKey(1, 1), new RoomKey(1, 2), new RoomKey(1, 3) };

        // Only 1/2 has monsters — the injected lookup mirrors the VM's real one
        // (placed + lair, deduped).
        RoomKey monsterRoom = new(1, 2);
        var link = new RoomDetailLink("cave worm(#8)", null, new RelayCommand(() => { }));
        IReadOnlyList<RoomDetailLink> MonsterLinks(RoomKey k) =>
            k.Equals(monsterRoom) ? new[] { link } : Array.Empty<RoomDetailLink>();

        IReadOnlyList<RouteDetailRow> rows =
            CurrentRouteDetails.Build(graph, null, null, route, _ => null, MonsterLinks, _ => { }, _ => null, ItemLink);

        // Two hops → two move rows in the route-picker "N> map/room < command" format,
        // PLUS a final arrival row for the destination (1/3) the route ends in.
        Assert.Equal(3, rows.Count);
        Assert.StartsWith("1>", rows[0].Step.Line);
        Assert.Contains("1/1 Home", rows[0].Step.Line);
        Assert.Contains("< n", rows[0].Step.Line);

        // The line is split so the room is its own link: "1>" / "1/1 Home" / "< n".
        Assert.Equal("1>", rows[0].NumberLabel);
        Assert.Equal("1/1 Home", rows[0].Location);
        Assert.Equal("< n", rows[0].CommandSuffix);
        Assert.NotNull(rows[0].OpenRoom);

        // Row 0 departs 1/1 (no monsters); row 1 departs 1/2 (→ the monster link).
        Assert.Equal(new RoomKey(1, 1), rows[0].Step.Room);
        Assert.False(rows[0].HasMonsters);
        Assert.Empty(rows[0].Monsters);

        Assert.Equal(new RoomKey(1, 2), rows[1].Step.Room);
        Assert.True(rows[1].HasMonsters);
        Assert.Equal("cave worm(#8)", rows[1].Monsters.Single().Text);

        // The final arrival row: the destination 1/3, no command ("(arrive)").
        Assert.True(rows[2].IsArrival);
        Assert.Equal(new RoomKey(1, 3), rows[2].Step.Room);
        Assert.Equal("(arrive)", rows[2].CommandSuffix);
        Assert.Contains("1/3", rows[2].Location);
        Assert.Equal("3>", rows[2].NumberLabel);
    }

    // Item id → a stub link (the tests never open a real record).
    private static RoomDetailLink ItemLink(int id)
        => new($"item#{id}", null, new RelayCommand(() => { }));

    [Fact]
    public void Build_AttachesHazardToItsRoom()
    {
        RoomGraphManager graph = NewGraph();
        var route = new[] { new RoomKey(1, 1), new RoomKey(1, 2), new RoomKey(1, 3) };

        // Only 1/2 is a hazard — the injected lookup mirrors the VM's RoomHazardIndex
        // resolution (harmful spell + the item that makes it safe to cross).
        RoomKey hazardRoom = new(1, 2);
        var hazard = new RouteStepWarning(
            new RoomDetailLink("drowning", null, new RelayCommand(() => { })),
            new[] { new RoomDetailLink("log raft(#5)", null, new RelayCommand(() => { })) });
        RouteStepWarning? Hazards(RoomKey k) => k.Equals(hazardRoom) ? hazard : null;

        IReadOnlyList<RouteDetailRow> rows = CurrentRouteDetails.Build(
            graph, null, null, route, _ => null,
            _ => Array.Empty<RoomDetailLink>(), _ => { }, Hazards, ItemLink);

        Assert.False(rows[0].HasWarning);
        Assert.True(rows[1].HasWarning);
        RouteStepWarning w = rows[1].Warning!;
        Assert.True(w.HasSpell);
        Assert.Equal("drowning", w.Spell!.Text);
        Assert.True(w.HasItems);
        Assert.Equal("log raft(#5)", w.Items.Single().Text);
    }

    [Fact]
    public void Build_FlagsAnItemGatedExit()
    {
        RoomGraphManager graph = NewItemGateGraph();
        var route = new[] { new RoomKey(1, 1), new RoomKey(1, 2), new RoomKey(1, 3) };

        IReadOnlyList<RouteDetailRow> rows = CurrentRouteDetails.Build(
            graph, null, null, route, _ => null,
            _ => Array.Empty<RoomDetailLink>(), _ => { }, _ => null, ItemLink);

        // The 1/2 → 1/3 hop is (Item: 474): the row for 1/2 warns, naming the gate
        // item — no hazard spell, just the requirement.
        Assert.False(rows[0].HasWarning);
        Assert.True(rows[1].HasWarning);
        RouteStepWarning w = rows[1].Warning!;
        Assert.False(w.HasSpell);
        Assert.Equal("needs", w.Label);
        Assert.Contains(w.Items, l => l.Text == "item#474");
    }

    [Fact]
    public void Build_TrivialOrEmptyRoute_ReturnsNoRows()
    {
        RoomGraphManager graph = NewGraph();
        IReadOnlyList<RoomDetailLink> NoMonsters(RoomKey _) => Array.Empty<RoomDetailLink>();

        Assert.Empty(CurrentRouteDetails.Build(graph, null, null, Array.Empty<RoomKey>(), _ => null, NoMonsters, _ => { }, _ => null, ItemLink));
        Assert.Empty(CurrentRouteDetails.Build(graph, null, null, new[] { new RoomKey(1, 1) }, _ => null, NoMonsters, _ => { }, _ => null, ItemLink));
    }
}
