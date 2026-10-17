using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Tables;
using Xunit;

namespace MudPlay.Tests;

public sealed class GameDataTableSectionTests : IDisposable
{
    private readonly string _root;
    private readonly GameDataCache _cache;

    public GameDataTableSectionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-table-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _cache = new GameDataCache(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Fact]
    public void FormatToggleSummary_JoinsEnabledInOrder_BlankWhenNone()
    {
        // The shared "Toggles" column backer: joins the enabled flags' labels in order,
        // and returns null (blank cell) when nothing is on.
        Assert.Equal("Collect, Stash", GameDataTableSectionViewModel.FormatToggleSummary(
            (true, "Collect"), (false, "Discard"), (true, "Stash")));
        Assert.Null(GameDataTableSectionViewModel.FormatToggleSummary(
            (false, "Collect"), (false, "Stash")));
        Assert.Null(GameDataTableSectionViewModel.FormatToggleSummary());
    }

    private void SeedMonsters(string setName, string json) => SeedTable(setName, "Monsters", json);

    private void SeedTable(string setName, string tableName, string json)
    {
        string dir = Path.Combine(_root, setName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, tableName + ".json"), json);
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

    // Minimal Rooms.json row with a lair tag naming one monster at a given (Max N).
    private static string LairRoom(int map, int room, int max, int monsterId)
        => $"{{\"Map Number\":{map},\"Room Number\":{room},\"Name\":\"R{room}\",\"Light\":0," +
           $"\"Shop\":0,\"Lair\":\"(Max {max}): {monsterId},[a]\",\"Delay\":0," +
           "\"N\":\"0\",\"S\":\"0\",\"E\":\"0\",\"W\":\"0\",\"NE\":\"0\",\"NW\":\"0\"," +
           "\"SE\":\"0\",\"SW\":\"0\",\"U\":\"0\",\"D\":\"0\"}";

    [Fact]
    public async Task LairColumns_ComeFromRoomTagMaxRegen()
    {
        // Monster #830 spawns in three lair rooms with per-room (Max N) of 2, 6, 3.
        // # Lairs = 3 rooms; Biggest Lair = max cap (6); Avg Lair Size = (2+6+3)/3 → 3.7.
        // A monster in no lair room has blank lair cells.
        SeedMonsters("v1.11p",
            "[{\"Number\":830,\"Name\":\"spectre\"},{\"Number\":9,\"Name\":\"Loner\"}]");
        SeedTable("v1.11p", "Rooms",
            "[" + LairRoom(17, 61, 2, 830) + "," + LairRoom(17, 62, 6, 830) + ","
                + LairRoom(17, 63, 3, 830) + "]");
        _cache.SwitchSet("v1.11p");

        RoomGraphManager graph = new(_cache);
        graph.OnActiveSetChanged("v1.11p");
        MonstersSectionViewModel vm = new(_cache, roomGraph: graph);
        await vm.LoadAsync();

        GameDataRow spectre = vm.AllRows.Single(r => r.Get("Name") == "spectre");
        Assert.Equal("3", spectre.Get("Lairs"));
        Assert.Equal("6", spectre.Get("BiggestLair"));
        Assert.Equal("3.7", spectre.Get("AvgLairSize"));

        GameDataRow loner = vm.AllRows.Single(r => r.Get("Name") == "Loner");
        Assert.True(string.IsNullOrEmpty(loner.Get("Lairs")));
        Assert.True(string.IsNullOrEmpty(loner.Get("BiggestLair")));
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
        vm.ApplySearchCommand.Execute(null);   // the box searches on Enter

        Assert.Equal(2, vm.FilteredRows.Count);
        Assert.All(vm.FilteredRows, r => Assert.Contains("Goblin", r.Get("Name")!));
    }

    [Fact]
    public async Task SearchText_WaitsForEnter_ClearingReShowsAtOnce()
    {
        SeedMonsters("v1.11p",
            "[{\"Id\":1,\"Name\":\"Goblin Warrior\"}," +
             "{\"Id\":2,\"Name\":\"Orc Chieftain\"}]");
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        vm.SearchText = "goblin";                 // typing alone doesn't filter
        Assert.Equal(2, vm.FilteredRows.Count);

        vm.ApplySearchCommand.Execute(null);      // Enter
        Assert.Single(vm.FilteredRows);

        vm.SearchText = string.Empty;             // the clear button — no Enter needed
        Assert.Equal(2, vm.FilteredRows.Count);
    }

    private static RangeFilter Range(MonstersSectionViewModel vm, string column)
        => vm.FilterGroups.SelectMany(g => g.Ranges).Single(r => r.Column == column);

    [Fact]
    public async Task SearchText_MatchesFormattedEnumLabel_NotJustRawCode()
    {
        // Items tab: ItemType / Worn render friendly labels ("Weapon", "Feet") via
        // formatters, while the raw cell holds the numeric code. Typing the label the
        // user actually sees must filter — the base match now checks display values
        // as well as raw, so "weapon" / "feet" hit even though the raw is "1" / "5".
        SeedTable("v1.11p", "Items",
            "[{\"Number\":1,\"Name\":\"long sword\",\"ItemType\":1,\"Worn\":0}," +
             "{\"Number\":2,\"Name\":\"leather boots\",\"ItemType\":0,\"Worn\":5}]");
        _cache.SwitchSet("v1.11p");
        ItemsSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        vm.SearchText = "weapon";          // ItemType 1 -> "Weapon"
        vm.ApplySearchCommand.Execute(null);   // the box searches on Enter
        Assert.Single(vm.FilteredRows);
        Assert.Equal("long sword", vm.FilteredRows[0].Get("Name"));

        vm.SearchText = "feet";            // Worn 5 -> "Feet"
        vm.ApplySearchCommand.Execute(null);   // the box searches on Enter
        Assert.Single(vm.FilteredRows);
        Assert.Equal("leather boots", vm.FilteredRows[0].Get("Name"));
    }

    [Fact]
    public async Task RangeFilters_StackWithAnd()
    {
        // Range filters are pending until Apply, then stack with AND. EXP min then HP
        // min narrows the set — report paradigm-20260814-103219's scenario.
        SeedMonsters("v1.11p",
            "[{\"Number\":1,\"Name\":\"Goblin\",\"HP\":10,\"EXP\":3}," +
             "{\"Number\":2,\"Name\":\"Orc\",\"HP\":25,\"EXP\":9000}," +
             "{\"Number\":3,\"Name\":\"Dragon\",\"HP\":500,\"EXP\":9000}]");
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        // Editing a box does nothing until Apply.
        Range(vm, "EXP").Min = 9000;
        Assert.Equal(3, vm.FilteredRows.Count);   // not yet applied
        vm.ApplyFiltersCommand.Execute(null);
        Assert.Equal(2, vm.FilteredRows.Count);   // Orc, Dragon
        Assert.DoesNotContain(vm.FilteredRows, r => r.Get("Name") == "Goblin");

        // HP min 100 stacks on top: only Dragon (HP 500) survives.
        Range(vm, "HP").Min = 100;
        vm.ApplyFiltersCommand.Execute(null);
        Assert.Single(vm.FilteredRows);
        Assert.Equal("Dragon", vm.FilteredRows[0].Get("Name"));
    }

    [Fact]
    public async Task RangeFilter_Max_FindsEasyTargets()
    {
        // A max bound (no min) brackets the low end — "AC ≤ 20" for easy kills.
        SeedMonsters("v1.11p",
            "[{\"Number\":1,\"Name\":\"Goblin\",\"ArmourClass\":5}," +
             "{\"Number\":2,\"Name\":\"Dragon\",\"ArmourClass\":80}]");
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        Range(vm, "ArmourClass").Max = 20;
        vm.ApplyFiltersCommand.Execute(null);
        Assert.Single(vm.FilteredRows);
        Assert.Equal("Goblin", vm.FilteredRows[0].Get("Name"));
    }

    [Fact]
    public async Task UndeadCheckbox_KeepsUndeadOnly()
    {
        SeedMonsters("v1.11p",
            "[{\"Number\":1,\"Name\":\"Goblin\",\"Undead\":0}," +
             "{\"Number\":2,\"Name\":\"Skeleton\",\"Undead\":255}," +
             "{\"Number\":3,\"Name\":\"Zombie\",\"Undead\":1}]");
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        BoolFilter undead = vm.FilterGroups.SelectMany(g => g.Bools).Single(b => b.Column == "Undead");
        undead.IsChecked = true;
        vm.ApplyFiltersCommand.Execute(null);
        Assert.Equal(2, vm.FilteredRows.Count);    // Skeleton, Zombie
        Assert.All(vm.FilteredRows, r => Assert.Equal("✗", r.GetDisplay("Undead")));
    }

    [Fact]
    public async Task SynthesizedColumns_MatchMmeTransforms()
    {
        // white-king-like: EXP 30000 ×10; AC 80 / DR 10; AvgDmg 290.4; HP 3000;
        // ability 28 (Magical) = 4 → Mag; no ability 34 → blank Dodge.
        SeedMonsters("v1.11p",
            "[{\"Number\":1,\"Name\":\"King\",\"EXP\":30000,\"ExpMulti\":10,\"HP\":3000," +
             "\"ArmourClass\":80,\"DamageResist\":10,\"AvgDmg\":290.4," +
             "\"AttType-0\":1,\"Att%-0\":100,\"AttAcc-0\":250,\"AttTrue%-0\":100," +
             "\"Abil-0\":28,\"AbilVal-0\":4}]");
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        GameDataRow r = vm.AllRows.Single();
        Assert.Equal("300,000", r.GetDisplay("EXP"));       // actual earned exp = 30000 × 10
        Assert.Equal("80/10", r.GetDisplay("AcDr"));
        Assert.Equal("290", r.GetDisplay("Damage"));        // round(290.4)
        Assert.Equal("250", r.GetDisplay("Accuracy"));      // maj == max → single
        Assert.Equal("4", r.GetDisplay("Mag"));
        Assert.True(string.IsNullOrEmpty(r.GetDisplay("Dodge")));
        // effective exp 300000 → round(300000×100 / (2×290 + 3000)) = 8380.
        Assert.Equal("8,380", r.GetDisplay("Efficiency"));
    }

    [Fact]
    public async Task AlignmentDropdown_FiltersOnFilterOnlyColumn()
    {
        // Alignment isn't a grid column but is carried for its dropdown filter.
        SeedMonsters("v1.11p",
            "[{\"Number\":1,\"Name\":\"Paladin\",\"Align\":1}," +
             "{\"Number\":2,\"Name\":\"Knight\",\"Align\":1}," +
             "{\"Number\":3,\"Name\":\"Fiend\",\"Align\":6}]");
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        CategoryFilter align = vm.FilterGroups.SelectMany(g => g.Categories).Single(c => c.Column == "Align");
        string fiendAlign = vm.AllRows.Single(r => r.Get("Name") == "Fiend").GetDisplay("Align")!;
        Assert.Contains(fiendAlign, align.Options);   // fixed option list includes every alignment

        align.Selected = fiendAlign;
        vm.ApplyFiltersCommand.Execute(null);
        Assert.Single(vm.FilteredRows);
        Assert.Equal("Fiend", vm.FilteredRows[0].Get("Name"));
    }

    private static BoolFilter Bool(MonstersSectionViewModel vm, string column)
        => vm.FilterGroups.SelectMany(g => g.Bools).Single(b => b.Column == column);

    [Fact]
    public async Task ElementalResistFacet_FiltersOnAbilityValue_IncludingNegative()
    {
        // Resist-Cold is ability code 3; the value is signed (negative = vulnerable).
        SeedMonsters("v1.11p",
            "[{\"Number\":1,\"Name\":\"Iceling\",\"Abil-0\":3,\"AbilVal-0\":80}," +
             "{\"Number\":2,\"Name\":\"Flamewisp\",\"Abil-0\":3,\"AbilVal-0\":-20}," +
             "{\"Number\":3,\"Name\":\"Plainrat\"}]");
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        Range(vm, "ResCold").Min = 50;                    // resistant
        vm.ApplyFiltersCommand.Execute(null);
        Assert.Single(vm.FilteredRows);
        Assert.Equal("Iceling", vm.FilteredRows[0].Get("Name"));

        Range(vm, "ResCold").Min = null;
        Range(vm, "ResCold").Max = -1;                    // vulnerable
        vm.ApplyFiltersCommand.Execute(null);
        Assert.Single(vm.FilteredRows);
        Assert.Equal("Flamewisp", vm.FilteredRows[0].Get("Name"));
    }

    [Fact]
    public async Task FlagFacets_KeepMatchingMonsters()
    {
        // Animal (code 78), loot (DropItem-N), and casts (MidSpell-N) each surface a
        // synthesised flag facet the checkbox filters on.
        SeedMonsters("v1.11p",
            "[{\"Number\":1,\"Name\":\"Wolf\",\"Abil-0\":78}," +
             "{\"Number\":2,\"Name\":\"Looter\",\"DropItem-0\":500,\"DropItem%-0\":50}," +
             "{\"Number\":3,\"Name\":\"Caster\",\"MidSpell-0\":42,\"MidSpell%-0\":30}]");
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        Bool(vm, "Animal").IsChecked = true;
        vm.ApplyFiltersCommand.Execute(null);
        Assert.Equal("Wolf", Assert.Single(vm.FilteredRows).Get("Name"));
        Bool(vm, "Animal").IsChecked = false;

        Bool(vm, "HasLoot").IsChecked = true;
        vm.ApplyFiltersCommand.Execute(null);
        Assert.Equal("Looter", Assert.Single(vm.FilteredRows).Get("Name"));
        Bool(vm, "HasLoot").IsChecked = false;

        Bool(vm, "CastsSpells").IsChecked = true;
        vm.ApplyFiltersCommand.Execute(null);
        Assert.Equal("Caster", Assert.Single(vm.FilteredRows).Get("Name"));
    }

    [Fact]
    public async Task MissingColumn_RendersAsNull()
    {
        SeedMonsters("v1.11p", "[{\"Name\":\"Goblin\"}]"); // no HP / MR
        _cache.SwitchSet("v1.11p");
        MonstersSectionViewModel vm = new(_cache);
        await vm.LoadAsync();

        // Pass-through columns absent from the source render null (EXP is now
        // always synthesised, so it's no longer a null-render probe).
        Assert.Null(vm.AllRows[0].Get("HP"));
        Assert.Null(vm.AllRows[0].Get("MagicRes"));
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
        Assert.Equal("true",   vm.AllRows[0].Get("Undead"));
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
        vm.ApplySearchCommand.Execute(null);   // the box searches on Enter
        Assert.Contains("1 / 3 rows", vm.StatusText);
    }
}
