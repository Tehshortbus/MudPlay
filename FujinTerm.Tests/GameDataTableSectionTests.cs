using System.IO;
using System.Linq;
using FujinTerm.Services;
using FujinTerm.ViewModels.GameData.Tables;
using Xunit;

namespace FujinTerm.Tests;

public sealed class GameDataTableSectionTests : IDisposable
{
    private readonly string _root;
    private readonly GameDataCache _cache;

    public GameDataTableSectionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-table-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _cache = new GameDataCache(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private void SeedMonsters(string setName, string json)
    {
        string dir = Path.Combine(_root, setName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Monsters.json"), json);
    }

    [Fact]
    public void NoActiveSet_RendersEmpty()
    {
        MonstersSectionViewModel vm = new(_cache);
        Assert.Empty(vm.AllRows);
        Assert.Empty(vm.FilteredRows);
    }

    [Fact]
    public void Reload_PopulatesRowsFromActiveSet()
    {
        SeedMonsters("v1.11p",
            "[{\"Id\":1,\"Name\":\"Goblin\",\"Level\":1,\"Hp\":10}," +
             "{\"Id\":2,\"Name\":\"Orc\",\"Level\":3,\"Hp\":25}]");

        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);

        Assert.Equal(2, vm.AllRows.Count);
        Assert.Equal("Goblin", vm.AllRows[0].Get("Name"));
        Assert.Equal("1",      vm.AllRows[0].Get("Level"));
        Assert.Equal("Orc",    vm.AllRows[1].Get("Name"));
    }

    [Fact]
    public void SearchText_FiltersByNameColumn()
    {
        SeedMonsters("v1.11p",
            "[{\"Id\":1,\"Name\":\"Goblin Warrior\"}," +
             "{\"Id\":2,\"Name\":\"Goblin Mage\"}," +
             "{\"Id\":3,\"Name\":\"Orc Chieftain\"}]");
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);

        vm.SearchText = "goblin";

        Assert.Equal(2, vm.FilteredRows.Count);
        Assert.All(vm.FilteredRows, r => Assert.Contains("Goblin", r.Get("Name")!));
    }

    [Fact]
    public void MissingColumn_RendersAsNull()
    {
        SeedMonsters("v1.11p", "[{\"Name\":\"Goblin\"}]"); // no Level / Hp
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);

        Assert.Null(vm.AllRows[0].Get("Level"));
        Assert.Null(vm.AllRows[0].Get("Hp"));
        Assert.Equal("Goblin", vm.AllRows[0].Get("Name"));
    }

    [Fact]
    public void ActiveSetChanged_ReloadsRows()
    {
        SeedMonsters("v1.11p", "[{\"Name\":\"Goblin\"}]");
        SeedMonsters("paradigm-1.8.5", "[{\"Name\":\"Skeleton\"},{\"Name\":\"Zombie\"}]");

        MonstersSectionViewModel vm = new(_cache);

        _cache.SwitchSet("v1.11p");
        Assert.Single(vm.AllRows);

        _cache.SwitchSet("paradigm-1.8.5");
        Assert.Equal(2, vm.AllRows.Count);
    }

    [Fact]
    public void GameDataRow_CollapsesAllJsonValueKindsToStrings()
    {
        SeedMonsters("v1.11p",
            "[{\"Name\":\"Goblin\",\"Level\":5,\"IsBoss\":true,\"Notes\":null}]");
        _cache.SwitchSet("v1.11p");

        MonstersSectionViewModel vm = new(_cache);

        Assert.Equal("Goblin", vm.AllRows[0].Get("Name"));
        Assert.Equal("5",      vm.AllRows[0].Get("Level"));
        // IsBoss + Notes aren't in the Monsters column list so they don't appear.
        Assert.DoesNotContain(vm.AllRows[0].Cells, c => c.Column == "IsBoss");
    }

    [Fact]
    public void StatusText_ShowsCountAndFilteredCount()
    {
        SeedMonsters("v1.11p",
            "[{\"Name\":\"Goblin\"},{\"Name\":\"Orc\"},{\"Name\":\"Skeleton\"}]");
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);

        Assert.Contains("3 rows", vm.StatusText);

        vm.SearchText = "gob";
        Assert.Contains("1 / 3 rows", vm.StatusText);
    }
}
