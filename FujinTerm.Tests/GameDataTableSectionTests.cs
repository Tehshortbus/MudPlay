using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
    public async Task Reload_PopulatesRowsFromActiveSet()
    {
        SeedMonsters("v1.11p",
            "[{\"Number\":1,\"Name\":\"Goblin\",\"HP\":10,\"EXP\":3}," +
             "{\"Number\":2,\"Name\":\"Orc\",\"HP\":25,\"EXP\":10}]");

        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        // Lazy-load: rows materialise on first activation. Bypass the
        // dispatcher-Post deferral OnActivated uses in app context — call
        // LoadAsync directly so the awaiter surfaces completion before
        // the asserts run.
        await vm.LoadAsync();

        Assert.Equal(2, vm.AllRows.Count);
        Assert.Equal("Goblin", vm.AllRows[0].Get("Name"));
        Assert.Equal("10",     vm.AllRows[0].Get("HP"));
        Assert.Equal("Orc",    vm.AllRows[1].Get("Name"));
    }

    [Fact]
    public async Task SearchText_FiltersByNameColumn()
    {
        SeedMonsters("v1.11p",
            "[{\"Id\":1,\"Name\":\"Goblin Warrior\"}," +
             "{\"Id\":2,\"Name\":\"Goblin Mage\"}," +
             "{\"Id\":3,\"Name\":\"Orc Chieftain\"}]");
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        vm.SearchText = "goblin";

        Assert.Equal(2, vm.FilteredRows.Count);
        Assert.All(vm.FilteredRows, r => Assert.Contains("Goblin", r.Get("Name")!));
    }

    [Fact]
    public async Task MissingColumn_RendersAsNull()
    {
        SeedMonsters("v1.11p", "[{\"Name\":\"Goblin\"}]"); // no HP / EXP
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        Assert.Null(vm.AllRows[0].Get("HP"));
        Assert.Null(vm.AllRows[0].Get("EXP"));
        Assert.Equal("Goblin", vm.AllRows[0].Get("Name"));
    }

    [Fact]
    public async Task ActiveSetChanged_ReloadsRows()
    {
        SeedMonsters("v1.11p", "[{\"Name\":\"Goblin\"}]");
        SeedMonsters("paradigm-1.8.5", "[{\"Name\":\"Skeleton\"},{\"Name\":\"Zombie\"}]");

        MonstersSectionViewModel vm = new(_cache);
        // Activation marks the tab as "loaded" — subsequent ActiveSetChanged
        // events trigger a reload. Without activation the section stays cold
        // (the lazy-load contract) and set switches would no-op.
        await vm.LoadAsync();

        _cache.SwitchSet("v1.11p");
        Assert.Single(vm.AllRows);

        _cache.SwitchSet("paradigm-1.8.5");
        Assert.Equal(2, vm.AllRows.Count);
    }

    [Fact]
    public async Task GameDataRow_CollapsesAllJsonValueKindsToStrings()
    {
        SeedMonsters("v1.11p",
            "[{\"Name\":\"Goblin\",\"HP\":5,\"Undead\":true,\"GreetTXT\":null}]");
        _cache.SwitchSet("v1.11p");

        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        Assert.Equal("Goblin", vm.AllRows[0].Get("Name"));
        Assert.Equal("5",      vm.AllRows[0].Get("HP"));
        // GreetTXT isn't in the Monsters column list so it doesn't appear.
        Assert.DoesNotContain(vm.AllRows[0].Cells, c => c.Column == "GreetTXT");
    }

    [Fact]
    public async Task StatusText_ShowsCountAndFilteredCount()
    {
        SeedMonsters("v1.11p",
            "[{\"Name\":\"Goblin\"},{\"Name\":\"Orc\"},{\"Name\":\"Skeleton\"}]");
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        Assert.Contains("3 rows", vm.StatusText);

        vm.SearchText = "gob";
        Assert.Contains("1 / 3 rows", vm.StatusText);
    }
}
