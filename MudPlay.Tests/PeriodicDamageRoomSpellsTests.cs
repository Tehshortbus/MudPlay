using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.Settings;
using Xunit;

namespace MudPlay.Tests;

// Settings → Periodic Damage Room Spells: the index behind it (which room spells
// damage, which rooms carry them, what bars a rest by default and after the user's
// choice) and the tab's own bookkeeping (only the spells set away from the default
// are stored, another data set's choices are kept, the empty states, the log line
// on a save, and the room list by area).
public sealed class PeriodicDamageRoomSpellsTests : IDisposable
{
    private readonly string _root;

    public PeriodicDamageRoomSpellsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-roomspelldamage-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ----- the index -------------------------------------------------------

    private static string RoomRow(int map, int number, string name, int spell) =>
        $$"""
        { "Map Number": {{map}}, "Room Number": {{number}}, "Name": "{{name}}", "Light": 0, "Shop": 0,
          "Spell": {{spell}}, "Lair": "", "Delay": 5, "N": "0", "S": "0", "E": "0", "W": "0",
          "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        """;

    // Magma heat (every tick), the fungus spell (one band of a table) and inn rest
    // (no damage), as in the game data.
    private const string Spells = """
        [
          { "Number": 526, "Name": "magma heat", "MinBase": 30, "MaxBase": 60, "Abil-0": 1, "AbilVal-0": 0 },
          { "Number": 1205, "Name": "fungus spell", "MinBase": 4072, "MaxBase": 4072, "Abil-0": 148, "AbilVal-0": 0 },
          { "Number": 1204, "Name": "spray of spores", "MinBase": 5, "MaxBase": 25, "Abil-0": 1, "AbilVal-0": 0 },
          { "Number": 484, "Name": "inn rest", "Abil-0": 123, "AbilVal-0": 200 }
        ]
        """;

    private const string TBInfo = """
        [
          { "Number": 4072, "LinkTo": 0, "Action": "random 4073\n" },
          { "Number": 4073, "LinkTo": 0, "Action": "80:addevil 0\n90:message 3160:cast 1204\n100:message 3160\n" }
        ]
        """;

    private RoomSpellDamageIndex NewIndex(out System.Action<string?> switchTo, LogService? log = null)
    {
        string dir = Path.Combine(_root, "one");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), "[" + string.Join(",",
            RoomRow(16, 1, "Lava Tube", 526), RoomRow(16, 2, "Lava Tube", 526), RoomRow(6, 3, "Lava Tube", 526),
            RoomRow(7, 4, "Fungus Cave", 1205), RoomRow(1, 5, "Inn", 484), RoomRow(1, 6, "Street", 0)) + "]");
        File.WriteAllText(Path.Combine(dir, "Spells.json"), Spells);
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), TBInfo);

        GameDataCache cache = new(_root);
        TBInfoStore tbinfo = new(cache);
        RoomGraphManager graph = new(cache);
        RoomSpellDamageIndex index = new(cache, graph, new KnownSpellCatalog(cache), tbinfo, log);
        switchTo = set =>
        {
            cache.SwitchSet(set);
            tbinfo.OnActiveSetChanged(set);
            graph.OnActiveSetChanged(set);
            index.OnActiveSetChanged(set);
        };
        return index;
    }

    [Fact]
    public void Index_ListsTheDamagingSpells_WithTheirRooms_AndClearsWithTheSet()
    {
        RoomSpellDamageIndex index = NewIndex(out System.Action<string?> switchTo);
        switchTo("one");

        Assert.Equal(new[] { 526, 1205 }, index.Readings.Keys.Order());
        Assert.Equal(RoomSpellDamage.EveryTick, index.ClassOf(526));
        Assert.Equal(RoomSpellDamage.OnARoll, index.ClassOf(1205));
        Assert.Equal(RoomSpellDamage.None, index.ClassOf(484));
        Assert.Equal(new[] { new RoomKey(6, 3), new RoomKey(16, 1), new RoomKey(16, 2) },
            index.RoomsOf(526).Select(r => r.Key).OrderBy(k => k.Map).ThenBy(k => k.Room));
        Assert.Empty(index.RoomsOf(484));

        switchTo(null);
        Assert.Empty(index.Readings);
        Assert.Empty(index.RoomsOf(526));
    }

    [Fact]
    public void Index_BarsResting_FollowsTheClass_UntilTheUserSaysOtherwise()
    {
        RoomSpellDamageIndex index = NewIndex(out System.Action<string?> switchTo);
        switchTo("one");
        Dictionary<int, bool> chosen = new();
        int asked = 0;
        IReadOnlyDictionary<int, bool> Chosen() { asked++; return chosen; }

        Assert.True(index.BarsResting(526, Chosen));     // every tick: barred
        Assert.False(index.BarsResting(1205, Chosen));   // on a roll: rested in
        Assert.False(index.BarsResting(484, Chosen));    // no damage
        Assert.False(index.BarsResting(0, Chosen));
        Assert.Equal(2, asked);                          // the stored choices are read for a damaging spell only

        chosen[526] = false;
        chosen[1205] = true;
        chosen[484] = true;                              // a stray entry for a spell that does no damage changes nothing
        Assert.False(index.BarsResting(526, Chosen));
        Assert.True(index.BarsResting(1205, Chosen));
        Assert.False(index.BarsResting(484, Chosen));
    }

    // ----- the tab -----------------------------------------------------------

    private static Room NewRoom(int map, int room, string name, int spell) =>
        new() { Key = new RoomKey(map, room), Name = name, Spell = spell, Exits = new Dictionary<Direction, RoomExit>() };

    private static PeriodicDamageRoomSpell MagmaHeat(int rooms = 3) => new(
        526, "magma heat",
        new RoomSpellDamageReading(RoomSpellDamage.EveryTick, 30, 60, false, 0, false, [], null),
        "magma amulet or phoenix feather (worn)",
        Enumerable.Range(1, rooms).Select(i => NewRoom(i % 2 == 0 ? 6 : 16, i, i % 3 == 0 ? "Infernal Cavern" : "Lava Tube", 526)).ToList());

    private static PeriodicDamageRoomSpell Fungus() => new(
        1205, "fungus spell",
        new RoomSpellDamageReading(RoomSpellDamage.OnARoll, 5, 25, false, 10, false, [], null),
        string.Empty,
        new[] { NewRoom(7, 4, "Fungus Cave", 1205) });

    private sealed class Tab
    {
        public ProfileService Profile { get; } = TestProfiles.Service();
        public List<PeriodicDamageRoomSpell> Listed { get; } = new() { MagmaHeat(), Fungus() };
        public string? ActiveSet { get; set; } = "one";
        public LogService Log { get; } = new();
        public List<string> Info { get; } = new();

        public Tab() => Log.EntryAdded += e => { if (e.Severity == LogSeverity.Info) Info.Add(e.Message); };

        public PeriodicDamageRoomSpellsSectionViewModel Open() =>
            new(Profile, () => Listed, () => ActiveSet, _ => null, gameData: null, Log);

        public Dictionary<int, bool> Stored() =>
            PeriodicDamageRoomSpellsSectionViewModel.ReadOrDefault(Profile.Current).BarsResting;
    }

    [Fact]
    public void Tab_StartsWithEveryTickSpellsTicked_AndTheRestClear()
    {
        Tab tab = new();
        tab.Profile.LoadDefaultProfile();
        using PeriodicDamageRoomSpellsSectionViewModel vm = tab.Open();

        Assert.True(vm.HasSpells);
        Assert.Equal(new[] { (526, true), (1205, false) }, vm.Spells.Select(r => (r.Number, r.BarsResting)));
        PeriodicDamageRoomSpellRowViewModel magma = vm.Spells[0];
        Assert.Equal(("#526", "magma heat", "30–60", "every tick", "magma amulet or phoenix feather (worn)", "3"),
            (magma.NumberText, magma.Name, magma.Damage, magma.How, magma.Counters, magma.RoomCount));
        Assert.Equal(("on a roll: 10% of ticks", "nothing in the game data"), (vm.Spells[1].How, vm.Spells[1].Counters));
        Assert.False(vm.IsDirty);
        Assert.Equal("1 of 2 bar resting; all as the default", vm.Summary);
    }

    [Fact]
    public void Tab_StoresOnlyTheSpellsSetAwayFromTheDefault_AndSaysWhatChanged()
    {
        Tab tab = new();
        tab.Profile.LoadDefaultProfile();
        using PeriodicDamageRoomSpellsSectionViewModel vm = tab.Open();

        vm.Spells[0].BarsResting = false;   // magma heat: rest there anyway
        vm.Spells[1].BarsResting = true;    // fungus: bar it
        Assert.True(vm.IsDirty);
        vm.Apply();

        Assert.Equal(new Dictionary<int, bool> { [526] = false, [1205] = true }, tab.Stored());
        Assert.False(vm.IsDirty);
        Assert.Equal("1 of 2 bar resting; 2 changed from the default", vm.Summary);
        Assert.Single(tab.Info, l => l == "Periodic Damage Room Spells: magma heat (#526) no longer bars resting; "
            + "fungus spell (#1205) now bars resting. In effect from the next rest decision.");

        // Back to the default: the entry goes, so a later change of the default reaches this character.
        tab.Info.Clear();
        vm.Spells[0].BarsResting = true;
        vm.Apply();
        Assert.Equal(new Dictionary<int, bool> { [1205] = true }, tab.Stored());
        Assert.Single(tab.Info, l => l.Contains("magma heat (#526) now bars resting") && !l.Contains("fungus"));

        // A save that changes nothing says nothing.
        tab.Info.Clear();
        vm.Apply();
        Assert.Empty(tab.Info);
    }

    [Fact]
    public void Tab_ReadsTheStoredChoices_AndKeepsAnotherDataSetsEntries()
    {
        Tab tab = new();
        CharacterProfile profile = tab.Profile.LoadDefaultProfile();
        profile.Settings = new()
        {
            [PeriodicDamageRoomSpellSettings.TabKey] = JsonSerializer.SerializeToElement(
                new PeriodicDamageRoomSpellSettings { BarsResting = { [526] = false, [5242] = false } }),
        };
        using PeriodicDamageRoomSpellsSectionViewModel vm = tab.Open();

        Assert.False(vm.Spells[0].BarsResting);
        Assert.True(vm.Spells[0].IsChanged);

        vm.ResetToDefaultsCommand.Execute(null);
        Assert.True(vm.Spells[0].BarsResting);
        vm.Apply();

        // Freezing cold isn't in this data set's list: its choice is not this tab's to drop.
        Assert.Equal(new Dictionary<int, bool> { [5242] = false }, tab.Stored());
    }

    [Fact]
    public void Tab_Discard_PutsTheBoxesBack()
    {
        Tab tab = new();
        tab.Profile.LoadDefaultProfile();
        using PeriodicDamageRoomSpellsSectionViewModel vm = tab.Open();

        vm.Spells[0].BarsResting = false;
        vm.Discard();

        Assert.True(vm.Spells[0].BarsResting);
        Assert.False(vm.IsDirty);
        Assert.Empty(tab.Stored());
    }

    [Fact]
    public void Tab_WithNothingToList_SaysWhy()
    {
        Tab tab = new();
        using PeriodicDamageRoomSpellsSectionViewModel vm = tab.Open();

        // No profile.
        Assert.False(vm.HasSpells);
        Assert.Empty(vm.Spells);
        Assert.StartsWith("Load or create a character profile", vm.EmptyReason);
        vm.Apply();   // nothing to save to, and no throw

        // A profile, no game data.
        tab.Listed.Clear();
        tab.ActiveSet = null;
        tab.Profile.LoadDefaultProfile();
        Assert.False(vm.HasSpells);
        Assert.StartsWith("No game data is loaded", vm.EmptyReason);

        // Game data with no such spell.
        tab.ActiveSet = "one";
        tab.Profile.LoadDefaultProfile();
        Assert.Equal("The loaded game data has no room spell that does damage.", vm.EmptyReason);
    }

    [Fact]
    public void Tab_ListsAgain_WhenTheGameDataChanges()
    {
        string dir = Path.Combine(_root, "empty");
        Directory.CreateDirectory(dir);
        GameDataCache cache = new(_root);
        Tab tab = new();
        tab.Profile.LoadDefaultProfile();
        using PeriodicDamageRoomSpellsSectionViewModel vm = new(tab.Profile, () => tab.Listed, () => tab.ActiveSet, _ => null, cache, tab.Log);
        vm.SelectedSpell = vm.Spells[0];

        tab.Listed.RemoveAt(1);
        cache.SwitchSet("empty");

        Assert.Equal(new[] { 526 }, vm.Spells.Select(r => r.Number));
        Assert.Equal(526, vm.SelectedSpell?.Number);   // the picked spell stays picked when it is still listed
    }

    [Fact]
    public void Tab_GameDataChangeUnderUnsavedTicks_KeepsThoseStillListed_AndNamesTheOnesItDropped()
    {
        string dir = Path.Combine(_root, "empty");
        Directory.CreateDirectory(dir);
        GameDataCache cache = new(_root);
        Tab tab = new();
        tab.Profile.LoadDefaultProfile();
        using PeriodicDamageRoomSpellsSectionViewModel vm = new(tab.Profile, () => tab.Listed, () => tab.ActiveSet, _ => null, cache, tab.Log);
        vm.Spells[0].BarsResting = false;   // magma heat, not saved
        vm.Spells[1].BarsResting = true;    // fungus spell, not saved

        tab.Listed.RemoveAt(1);             // the new set has no fungus spell
        cache.SwitchSet("empty");

        PeriodicDamageRoomSpellRowViewModel magma = Assert.Single(vm.Spells);
        Assert.False(magma.BarsResting);    // the tick the user cleared is still cleared
        Assert.True(vm.IsDirty);
        Assert.Single(tab.Info, l => l == "Periodic Damage Room Spells: unsaved change(s) dropped for fungus spell (#1205) — the game data changed.");

        vm.Apply();
        Assert.Equal(new Dictionary<int, bool> { [526] = false }, tab.Stored());
    }

    [Fact]
    public void Tab_ProfileLoadUnderUnsavedTicks_ReloadsForTheNewCharacter_AndNamesWhatItDropped()
    {
        // As every Settings section does: the edits were the old character's.
        Tab tab = new();
        tab.Profile.LoadDefaultProfile();
        using PeriodicDamageRoomSpellsSectionViewModel vm = tab.Open();
        vm.Spells[0].BarsResting = false;

        tab.Profile.LoadDefaultProfile();

        Assert.True(vm.Spells[0].BarsResting);
        Assert.False(vm.IsDirty);
        Assert.Single(tab.Info, l => l.StartsWith("Periodic Damage Room Spells: unsaved change(s) dropped for magma heat (#526)"));
    }

    [Fact]
    public void Tab_WearCounterBox_StartsTicked_IsSavedWithTheCharacter_AndSaysWhenItChanges()
    {
        Tab tab = new();
        tab.Profile.LoadDefaultProfile();
        using PeriodicDamageRoomSpellsSectionViewModel vm = tab.Open();
        Assert.True(vm.WearCounterBeforeEntering);
        Assert.True(PeriodicDamageRoomSpellsSectionViewModel.ReadOrDefault(null).WearCounterBeforeEntering);
        Assert.False(vm.IsDirty);

        vm.WearCounterBeforeEntering = false;
        Assert.True(vm.IsDirty);
        vm.Apply();

        Assert.False(PeriodicDamageRoomSpellsSectionViewModel.ReadOrDefault(tab.Profile.Current).WearCounterBeforeEntering);
        Assert.Empty(tab.Stored());   // the per-spell choices are untouched
        Assert.False(vm.IsDirty);
        Assert.Single(tab.Info, l => l.Contains("is no longer put on, and routes no longer count one that isn't worn"));

        // Cancel puts an unsaved change back.
        vm.WearCounterBeforeEntering = true;
        vm.Discard();
        Assert.False(vm.WearCounterBeforeEntering);
        Assert.False(vm.IsDirty);

        // A save that only touches the spells leaves the box as stored.
        vm.Spells[0].BarsResting = false;
        vm.Apply();
        Assert.False(PeriodicDamageRoomSpellsSectionViewModel.ReadOrDefault(tab.Profile.Current).WearCounterBeforeEntering);
    }

    [Fact]
    public void Tab_ATimerSpell_SaysSoInComes_AndStartsTicked()
    {
        Tab tab = new();
        tab.Listed.Add(new PeriodicDamageRoomSpell(
            512, "holding breath",
            new RoomSpellDamageReading(RoomSpellDamage.AfterATimer, 5, 20, false, 0, false, [], null)
            {
                Timed = [new RoomSpellDamageStage(513, 5, 20, 25), new RoomSpellDamageStage(514, 9999, 9999, 30)],
            },
            "gnomish fish-helm (worn)",
            new[] { NewRoom(6, 1139, "Underwater Passage", 512) }));
        tab.Profile.LoadDefaultProfile();
        using PeriodicDamageRoomSpellsSectionViewModel vm = new(
            tab.Profile, () => tab.Listed, () => tab.ActiveSet, _ => null, gameData: null, tab.Log,
            spellName: n => n == 513 ? "drowning" : n == 514 ? "drowned to death" : null);

        PeriodicDamageRoomSpellRowViewModel row = vm.Spells.Single(r => r.Number == 512);
        Assert.True(row.BarsResting);
        Assert.Equal("5–20", row.Damage);
        Assert.Equal("on a timer: drowning 5–20 after 25 rounds; drowned to death 9999 after 30 rounds", row.How);
    }

    [Fact]
    public void Tab_PickedSpell_ListsItsRoomsByMapAndName_LargestFirst_AsLinks()
    {
        Tab tab = new();
        tab.Listed[0] = MagmaHeat(rooms: 30);
        tab.Profile.LoadDefaultProfile();
        using PeriodicDamageRoomSpellsSectionViewModel vm = tab.Open();
        Assert.Equal("Pick a spell above to list its rooms.", vm.RoomsTitle);
        Assert.Empty(vm.Areas);

        vm.SelectedSpell = vm.Spells[0];

        // 30 rooms: odd numbers on map 16, even on map 6; every third an Infernal Cavern.
        Assert.Equal(
            new[] { "Map 6 · Lava Tube  (10)", "Map 16 · Lava Tube  (10)", "Map 6 · Infernal Cavern  (5)", "Map 16 · Infernal Cavern  (5)" },
            vm.Areas.Select(a => a.Label));
        Assert.Same(vm.Areas[0], vm.SelectedArea);
        Assert.Equal(new[] { 8, 2 }, vm.RoomLines.Select(line => line.Count));
        Assert.Equal("6/2", vm.RoomLines[0][0].Label);
        Assert.StartsWith("Rooms with magma heat (#526): 30, in 4 area(s).", vm.RoomsTitle);

        vm.SelectedArea = vm.Areas[2];
        Assert.Equal(new[] { "6/6", "6/12", "6/18", "6/24", "6/30" }, vm.RoomLines.Single().Select(l => l.Label));

        vm.SelectedSpell = null;
        Assert.Empty(vm.RoomLines);
    }

    [Fact]
    public void NoEntry_OrABrokenOne_ReadsAsEverySpellAtItsDefault()
    {
        Assert.Empty(PeriodicDamageRoomSpellsSectionViewModel.ReadOrDefault(null).BarsResting);
        CharacterProfile profile = new()
        {
            Settings = new() { [PeriodicDamageRoomSpellSettings.TabKey] = JsonSerializer.SerializeToElement("not an object") },
        };
        Assert.Empty(PeriodicDamageRoomSpellsSectionViewModel.ReadOrDefault(profile).BarsResting);
    }
}
