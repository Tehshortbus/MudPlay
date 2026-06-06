using System.IO;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.ViewModels.Navigation;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 7.20 — LairEditorDialogViewModel save / cancel / validation flow.
/// Drives the VM headlessly; the ConfirmService overwrite path uses a
/// real DialogService but no main window is set (the path returns
/// silently when the overwrite confirm fires, which is fine because the
/// tests below don't trip a rename collision).
/// </summary>
public sealed class LairEditorDialogViewModelTests : IDisposable
{
    private readonly string _bbs;

    public LairEditorDialogViewModelTests()
    {
        string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
        _bbs = "test-laireditor-" + suffix;
    }

    public void Dispose()
    {
        try
        {
            string bbsFolder = AppPaths.BbsFolder(_bbs);
            if (Directory.Exists(bbsFolder)) Directory.Delete(bbsFolder, recursive: true);
        }
        catch { /* best-effort */ }
    }

    private const string RoomsJson = """
        [
          { "Map Number": 1, "Room Number": 100, "Name": "Sewer A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private sealed class Harness : IDisposable
    {
        public required string SetName { get; init; }
        public required LairManager Setups { get; init; }
        public required RoomGraphManager Graph { get; init; }
        public required LairTimerStore Timers { get; init; }
        public void Dispose() => Timers.Dispose();
    }

    private Harness NewHarness()
    {
        string setName = "test-set-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string setRoot = Path.Combine(AppPaths.GameDataRoot, setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"), RoomsJson);

        GameDataCache cache = new();
        cache.SwitchSet(setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(setName);
        RoomTracker tracker = new(graph);

        LairManager setups = new();
        setups.LoadAll(_bbs);
        LairTimerStore timers = new(cache, graph, tracker);

        return new Harness
        {
            SetName = setName,
            Setups = setups,
            Graph = graph,
            Timers = timers,
        };
    }

    // ----- validation ----------------------------------------------

    [Fact]
    public void Fresh_NameBlank_CannotSave()
    {
        using Harness h = NewHarness();
        LairSetup draft = new("", new[] { new LairMarker(1, 100) });
        LairEditorDialogViewModel vm = new(
            draft, h.Setups, h.Graph, h.Timers, confirm: null, isNew: true);

        Assert.False(vm.CanSave);
        Assert.True(vm.HasNameError);
    }

    [Fact]
    public void Fresh_NoMarkers_CannotSave()
    {
        using Harness h = NewHarness();
        LairSetup draft = new("Empty", Array.Empty<LairMarker>());
        LairEditorDialogViewModel vm = new(
            draft, h.Setups, h.Graph, h.Timers, confirm: null, isNew: true);

        Assert.False(vm.CanSave);
        Assert.False(vm.HasNameError);
    }

    [Fact]
    public void NameAndMarkers_CanSave()
    {
        using Harness h = NewHarness();
        LairSetup draft = new("Sewer Run", new[] { new LairMarker(1, 100) });
        LairEditorDialogViewModel vm = new(
            draft, h.Setups, h.Graph, h.Timers, confirm: null, isNew: true);

        Assert.True(vm.CanSave);
    }

    // ----- markers --------------------------------------------------

    [Fact]
    public void Constructor_PopulatesMarkerRows_WithGraphLabels()
    {
        using Harness h = NewHarness();
        LairSetup draft = new("Run", new[]
        {
            new LairMarker(1, 100, overrideRespawnSeconds: 90),
        });
        LairEditorDialogViewModel vm = new(
            draft, h.Setups, h.Graph, h.Timers, confirm: null, isNew: true);

        LairMarkerRowViewModel row = Assert.Single(vm.Markers);
        Assert.Equal(new RoomKey(1, 100), row.Key);
        Assert.Equal("Sewer A", row.RoomName);
        Assert.Equal(90, row.OverrideRespawnSeconds);
        Assert.Equal("1/100 — Sewer A", row.DisplayHeader);
        Assert.Equal("no game-data timer", row.DefaultHint);
    }

    [Fact]
    public void RemoveMarker_DropsRow_AndUpdatesCanSave()
    {
        using Harness h = NewHarness();
        LairSetup draft = new("Run", new[] { new LairMarker(1, 100) });
        LairEditorDialogViewModel vm = new(
            draft, h.Setups, h.Graph, h.Timers, confirm: null, isNew: true);

        Assert.True(vm.CanSave);
        LairMarkerRowViewModel row = vm.Markers[0];
        vm.RemoveMarkerCommand.Execute(row);

        Assert.Empty(vm.Markers);
        Assert.False(vm.CanSave);
    }

    // ----- Save / Cancel -------------------------------------------

    [Fact]
    public async Task Save_NewSetup_PersistsViaManager_AndFiresClose()
    {
        using Harness h = NewHarness();
        LairSetup draft = new("Sewer", new[]
        {
            new LairMarker(1, 100, overrideRespawnSeconds: 120),
        });
        LairEditorDialogViewModel vm = new(
            draft, h.Setups, h.Graph, h.Timers, confirm: null, isNew: true);

        LairSetup? closed = null;
        vm.CloseRequested += s => closed = s;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(closed);
        Assert.Equal("Sewer", closed!.Name);

        LairSetup? roundTrip = h.Setups.Get("Sewer");
        Assert.NotNull(roundTrip);
        LairMarker only = Assert.Single(roundTrip!.Markers);
        Assert.Equal(1,  only.Map);
        Assert.Equal(100, only.Room);
        Assert.Equal(120, only.OverrideRespawnSeconds);
    }

    [Fact]
    public void Cancel_FiresCloseWithNull_AndDoesNotPersist()
    {
        using Harness h = NewHarness();
        LairSetup draft = new("Untouched", new[] { new LairMarker(1, 100) });
        LairEditorDialogViewModel vm = new(
            draft, h.Setups, h.Graph, h.Timers, confirm: null, isNew: true);

        LairSetup? closed = new("placeholder", Array.Empty<LairMarker>());
        bool fired = false;
        vm.CloseRequested += s => { closed = s; fired = true; };

        vm.CancelCommand.Execute(null);

        Assert.True(fired);
        Assert.Null(closed);
        Assert.Null(h.Setups.Get("Untouched"));
    }

    [Fact]
    public async Task Save_RenameExisting_DeletesOldFile()
    {
        using Harness h = NewHarness();
        h.Setups.Save(new LairSetup("OldName", new[] { new LairMarker(1, 100) }));

        LairSetup existing = h.Setups.Get("OldName")!;
        LairEditorDialogViewModel vm = new(
            existing, h.Setups, h.Graph, h.Timers, confirm: null, isNew: false);
        vm.Name = "NewName";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(h.Setups.Get("OldName"));
        Assert.NotNull(h.Setups.Get("NewName"));
    }
}
