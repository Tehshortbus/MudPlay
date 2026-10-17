using System.IO;
using System.Linq;
using MudPlay.Services;
using MudPlay.ViewModels.GameData;
using Xunit;

namespace MudPlay.Tests;

public sealed class GameDataBrowserViewModelTests : IDisposable
{
    private readonly string _root;
    private readonly GameDataCache _cache;

    public GameDataBrowserViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-gdb-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _cache = new GameDataCache(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Fact]
    public void SeedSections_PopulatesEveryTableTab()
    {
        // The engine-backed tabs need their services, which this ctor isn't given;
        // the game-data tables are always there.
        GameDataBrowserViewModel vm = new(_cache, triggers: null);

        Assert.Contains(vm.Sections, s => s.Id == "monsters");
        Assert.Contains(vm.Sections, s => s.Id == "items");
        Assert.Contains(vm.Sections, s => s.Id == "spells");
        Assert.Contains(vm.Sections, s => s.Id == "rooms");
        Assert.Contains(vm.Sections, s => s.Id == "lairs");
        Assert.Contains(vm.Sections, s => s.Id == "shops");
        Assert.Contains(vm.Sections, s => s.Id == "races");
        Assert.Contains(vm.Sections, s => s.Id == "classes");
        Assert.Contains(vm.Sections, s => s.Id == "textblocks");
        Assert.Contains(vm.Sections, s => s.Id == "info");
        Assert.Contains(vm.Sections, s => s.Id == "unobtainable");
        Assert.Contains(vm.Sections, s => s.Id == "questflags");
    }

    [Fact]
    public void DerivedTables_LandInTheTableGroup()
    {
        GameDataBrowserViewModel vm = new(_cache, triggers: null);
        Assert.Contains(vm.TableSections, s => s.Id == "unobtainable");
        Assert.Contains(vm.TableSections, s => s.Id == "questflags");
        // Not among the engine-backed group.
        Assert.DoesNotContain(vm.EngineSections, s => s.Id == "questflags");
    }

    [Fact]
    public void Ctor_DefaultsToFirstSection()
    {
        GameDataBrowserViewModel vm = new(_cache, triggers: null);
        Assert.NotNull(vm.SelectedSection);
        Assert.Equal(vm.Sections[0], vm.SelectedSection);
    }

    [Fact]
    public void Ctor_WithInitialSectionId_SelectsThatSection()
    {
        GameDataBrowserViewModel vm = new(_cache, triggers: null, initialSectionId: "spells");
        Assert.Equal("spells", vm.SelectedSection?.Id);
    }

    [Fact]
    public void Ctor_WithUnknownInitialSectionId_FallsBackToFirst()
    {
        GameDataBrowserViewModel vm = new(_cache, triggers: null, initialSectionId: "not-a-real-section");
        Assert.Equal(vm.Sections[0], vm.SelectedSection);
    }

    [Fact]
    public void SearchText_FiltersVisibleSections()
    {
        GameDataBrowserViewModel vm = new(_cache, triggers: null);

        vm.SearchText = "spell";

        // Spells lives in the MDB-tables group.
        Assert.Contains(vm.TableSections, s => s.Id == "spells");
        Assert.DoesNotContain(vm.TableSections, s => s.Id == "monsters");
    }

    [Fact]
    public void SearchText_Empty_ShowsEverySection()
    {
        GameDataBrowserViewModel vm = new(_cache, triggers: null);

        vm.SearchText = "anything";
        vm.SearchText = string.Empty;

        // Engine + table groups together cover every seeded section.
        Assert.Equal(vm.Sections.Count, vm.EngineSections.Count + vm.TableSections.Count);
    }

    [Fact]
    public void StatusText_ReflectsActiveSetAndSelection()
    {
        Directory.CreateDirectory(Path.Combine(_root, "v1.11p"));
        _cache.SwitchSet("v1.11p");

        GameDataBrowserViewModel vm = new(_cache, triggers: null);
        Assert.Contains("v1.11p", vm.StatusText);
        Assert.Contains(vm.SelectedSection!.Title, vm.StatusText);
    }

    [Fact]
    public void StatusText_NoActiveSet_RendersPlaceholder()
    {
        GameDataBrowserViewModel vm = new(_cache, triggers: null);
        Assert.Contains("(no set)", vm.StatusText);
    }
}
