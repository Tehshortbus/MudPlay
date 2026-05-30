using System.IO;
using System.Linq;
using FujinTerm.Services;
using FujinTerm.ViewModels.GameData;
using Xunit;

namespace FujinTerm.Tests;

public sealed class GameDataBrowserViewModelTests : IDisposable
{
    private readonly string _root;
    private readonly GameDataCache _cache;

    public GameDataBrowserViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-gdb-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _cache = new GameDataCache(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Fact]
    public void SeedSections_PopulatesEveryEventualTab()
    {
        GameDataBrowserViewModel vm = new(_cache);

        Assert.Contains(vm.Sections, s => s.Id == "players");
        Assert.Contains(vm.Sections, s => s.Id == "macros");
        Assert.Contains(vm.Sections, s => s.Id == "triggers");
        Assert.Contains(vm.Sections, s => s.Id == "aliases");
        Assert.Contains(vm.Sections, s => s.Id == "messages");
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
    }

    [Fact]
    public void Ctor_DefaultsToFirstSection()
    {
        GameDataBrowserViewModel vm = new(_cache);
        Assert.NotNull(vm.SelectedSection);
        Assert.Equal(vm.Sections[0], vm.SelectedSection);
    }

    [Fact]
    public void Ctor_WithInitialSectionId_SelectsThatSection()
    {
        GameDataBrowserViewModel vm = new(_cache, initialSectionId: "spells");
        Assert.Equal("spells", vm.SelectedSection?.Id);
    }

    [Fact]
    public void Ctor_WithUnknownInitialSectionId_FallsBackToFirst()
    {
        GameDataBrowserViewModel vm = new(_cache, initialSectionId: "not-a-real-section");
        Assert.Equal(vm.Sections[0], vm.SelectedSection);
    }

    [Fact]
    public void SearchText_FiltersVisibleSections()
    {
        GameDataBrowserViewModel vm = new(_cache);

        vm.SearchText = "spell";

        // Spells lives in the MDB-tables group.
        Assert.Contains(vm.TableSections, s => s.Id == "spells");
        Assert.DoesNotContain(vm.TableSections, s => s.Id == "monsters");
    }

    [Fact]
    public void SearchText_Empty_ShowsEverySection()
    {
        GameDataBrowserViewModel vm = new(_cache);

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

        GameDataBrowserViewModel vm = new(_cache);
        Assert.Contains("v1.11p", vm.StatusText);
        Assert.Contains(vm.SelectedSection!.Title, vm.StatusText);
    }

    [Fact]
    public void StatusText_NoActiveSet_RendersPlaceholder()
    {
        GameDataBrowserViewModel vm = new(_cache);
        Assert.Contains("(no set)", vm.StatusText);
    }
}
