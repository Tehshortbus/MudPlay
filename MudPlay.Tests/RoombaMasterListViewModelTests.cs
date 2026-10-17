using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.CharacterWorkshop;
using Xunit;

namespace MudPlay.Tests;

// RoombaMasterListViewModel cross-references GhItemLocationStore's sightings
// against Items.json's Obtained From (via the same ItemMdbViewBuilder the Item
// edit dialog uses) at a fixed 50 charm, excluding any shop whose host room is
// one of the user's labeled gang-house rooms.
public sealed class RoombaMasterListViewModelTests : IDisposable
{
    private readonly string _gameDataRoot = Path.Combine(
        Path.GetTempPath(), "mudplay-roomba-masterlist-gamedata-" + Path.GetRandomFileName());
    private readonly string _scratchBbs = "roomba-masterlist-test-" + Path.GetRandomFileName();

    public void Dispose()
    {
        try { Directory.Delete(_gameDataRoot, recursive: true); }
        catch { /* best-effort */ }
        try
        {
            string folder = AppPaths.BbsFolder(_scratchBbs);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // Two shops: #10 sits in room 1/1 (the labeled gang-house room), #20 sits in
    // room 1/50 (an ordinary town room). "torch" is stocked by both; "gh only
    // trinket" is stocked only by the gang-house shop.
    private (GhItemLocationStore locations, GhRoomLabelStore labels, ItemNameStore itemNames, GameDataCache cache, RoomGraphManager roomGraph)
        Setup()
    {
        Directory.CreateDirectory(Path.Combine(_gameDataRoot, "alpha"));
        File.WriteAllText(Path.Combine(_gameDataRoot, "alpha", "Rooms.json"), """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Gang House Storage" },
              { "Map Number": 1, "Room Number": 2, "Name": "Gang House Vault" },
              { "Map Number": 1, "Room Number": 50, "Name": "Town General Store" }
            ]
            """);
        File.WriteAllText(Path.Combine(_gameDataRoot, "alpha", "Shops.json"), """
            [
              { "Number": 10, "Name": "Gang Stash", "Assigned To": "Room 1/1", "Markup%": 0 },
              { "Number": 20, "Name": "General Store", "Assigned To": "Room 1/50", "Markup%": 10 }
            ]
            """);
        File.WriteAllText(Path.Combine(_gameDataRoot, "alpha", "Items.json"), """
            [
              { "Number": 1, "Name": "torch", "ItemType": 0, "Price": 10, "Currency": 0,
                "Obtained From": "Shop #10, Shop #20" },
              { "Number": 2, "Name": "gh only trinket", "ItemType": 0, "Price": 5, "Currency": 0,
                "Obtained From": "Shop #10" }
            ]
            """);

        GameDataCache cache = new(_gameDataRoot);
        cache.SwitchSet("alpha");
        ItemNameStore itemNames = new(cache);
        itemNames.OnActiveSetChanged("alpha");
        RoomGraphManager roomGraph = new(cache);
        roomGraph.OnActiveSetChanged("alpha");

        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhRoomLabelStore labels = new(profile);
        labels.OnRealmChanged(AppPaths.BbsFolder(_scratchBbs));
        labels.SetLabel(new RoomKey(1, 1), new System.Collections.Generic.List<GhCategoryRule>(), isCatchAll: false);

        GhItemLocationStore locations = new(itemNames);
        locations.OnRealmChanged(AppPaths.BbsFolder(_scratchBbs));

        return (locations, labels, itemNames, cache, roomGraph);
    }

    [Fact]
    public void Market_ExcludesGangHouseShop_ButKeepsTownShop()
    {
        var (locations, labels, itemNames, cache, roomGraph) = Setup();
        locations.RecordRoom(new RoomKey(1, 1), new[] { "torch" });

        using RoombaMasterListViewModel vm = new(locations, labels, itemNames, cache, roomGraph);

        RoombaMasterListRowViewModel row = Assert.Single(vm.Rows);
        Assert.Equal("torch", row.ItemName);
        Assert.DoesNotContain("Gang Stash", row.Market);
        Assert.Contains("General Store", row.Market);
        Assert.Contains("BUY:", row.Market);
        Assert.Contains("SELL:", row.Market);
    }

    [Fact]
    public void Market_ItemOnlySoldByGangHouseShop_ReportsNoOutsideMarket()
    {
        var (locations, labels, itemNames, cache, roomGraph) = Setup();
        locations.RecordRoom(new RoomKey(1, 1), new[] { "gh only trinket" });

        using RoombaMasterListViewModel vm = new(locations, labels, itemNames, cache, roomGraph);

        RoombaMasterListRowViewModel row = Assert.Single(vm.Rows);
        Assert.Equal("(no outside market)", row.Market);
    }

    [Fact]
    public void SameItemInMultipleRooms_ProducesOneRowPerRoom()
    {
        var (locations, labels, itemNames, cache, roomGraph) = Setup();
        locations.RecordRoom(new RoomKey(1, 1), new[] { "3 torch" });
        locations.RecordRoom(new RoomKey(1, 2), new[] { "2 torch" });

        using RoombaMasterListViewModel vm = new(locations, labels, itemNames, cache, roomGraph);

        Assert.Equal(2, vm.Rows.Count);
        Assert.Contains(vm.Rows, r => r.SeenIn.Contains("1/1") && r.Quantity == 3);
        Assert.Contains(vm.Rows, r => r.SeenIn.Contains("1/2") && r.Quantity == 2);
        // Both rows resolve to the same (cached) market cross-reference.
        Assert.All(vm.Rows, r => Assert.Contains("General Store", r.Market));
    }

    [Fact]
    public void SeenIn_IncludesResolvedRoomName()
    {
        var (locations, labels, itemNames, cache, roomGraph) = Setup();
        locations.RecordRoom(new RoomKey(1, 1), new[] { "torch" });

        using RoombaMasterListViewModel vm = new(locations, labels, itemNames, cache, roomGraph);

        RoombaMasterListRowViewModel row = Assert.Single(vm.Rows);
        Assert.Contains("Gang House Storage", row.SeenIn);
        Assert.Contains("1/1", row.SeenIn);
    }

    [Fact]
    public void UnresolvableItemName_ReportsUnresolved()
    {
        var (locations, labels, itemNames, cache, roomGraph) = Setup();
        locations.RecordRoom(new RoomKey(1, 1), new[] { "totally unknown widget" });

        using RoombaMasterListViewModel vm = new(locations, labels, itemNames, cache, roomGraph);

        RoombaMasterListRowViewModel row = Assert.Single(vm.Rows);
        Assert.Equal("(unresolved item)", row.Market);
    }

    [Fact]
    public void Rows_RefreshAutomatically_WhenLocationsChange()
    {
        var (locations, labels, itemNames, cache, roomGraph) = Setup();
        using RoombaMasterListViewModel vm = new(locations, labels, itemNames, cache, roomGraph);
        Assert.Empty(vm.Rows);

        locations.RecordRoom(new RoomKey(1, 50), new[] { "torch" });

        Assert.Single(vm.Rows);
    }

    [Fact]
    public void Dispose_StopsRefreshingOnFurtherChanges()
    {
        var (locations, labels, itemNames, cache, roomGraph) = Setup();
        RoombaMasterListViewModel vm = new(locations, labels, itemNames, cache, roomGraph);
        vm.Dispose();

        locations.RecordRoom(new RoomKey(1, 50), new[] { "torch" });

        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void BuildExportText_GroupsByRoom_ItemsAlphabeticalWithQuantity()
    {
        var (locations, labels, itemNames, cache, roomGraph) = Setup();
        // Room 1/1 holds two items out of alphabetical order; 1/2 holds one.
        locations.RecordRoom(new RoomKey(1, 1), new[] { "3 torch", "gh only trinket" });
        locations.RecordRoom(new RoomKey(1, 2), new[] { "2 torch" });

        using RoombaMasterListViewModel vm = new(locations, labels, itemNames, cache, roomGraph);
        string export = vm.BuildExportText();
        var lines = export.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        // Room headers carry the map/room and its resolved name.
        Assert.Contains("1/1 — Gang House Storage", lines);
        Assert.Contains("1/2 — Gang House Vault", lines);

        // Within 1/1, items are alphabetical ("gh only trinket" before "torch"),
        // each with its quantity, indented under the header.
        int header11 = lines.IndexOf("1/1 — Gang House Storage");
        Assert.Equal("  1x gh only trinket", lines[header11 + 1]);
        Assert.Equal("  3x torch", lines[header11 + 2]);

        // 1/1 (room 1) is listed before 1/2 (room 2).
        Assert.True(header11 < lines.IndexOf("1/2 — Gang House Vault"));
    }
}
