using System;
using System.IO;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Game.Spells;
using MudPlay.Services;
using MudPlay.ViewModels;
using Xunit;

namespace MudPlay.Tests;

// Report paradigm-20261014-121847: a Paradigm Mage (Silk armour type, the 1% floor)
// against a brute zombie's two 150-accuracy fists, with ice storm (-6 accuracy)
// applied. The list showed one figure, Your Matchup another: the panel left out the
// class floor and the list left out the debuff, and the whole-percent math dropped
// dodge at the floor. Every Hits You % in the window now reads one calculation.
public sealed class MonsterIntelIncomingHitTests : IDisposable
{
    private readonly string _root;

    public MonsterIntelIncomingHitTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-intel-hit-tests-" + Path.GetRandomFileName());
        string setDir = Path.Combine(_root, "para-set");
        Directory.CreateDirectory(setDir);
        File.WriteAllText(Path.Combine(setDir, "Info.json"), """[ { "Legit": 2 } ]""");
        File.WriteAllText(Path.Combine(setDir, "Classes.json"), """
        [ { "Number": 12, "Name": "Mage", "MageryType": 1, "MageryLVL": 3, "ArmourType": 1, "CombatLVL": 3 } ]
        """);
        File.WriteAllText(Path.Combine(setDir, "Monsters.json"), """
        [
          {
            "Number": 2739, "Name": "brute zombie", "Type": 2, "Align": 2, "HP": 810, "EXP": 16250, "Energy": 1000,
            "AttType-0": 1, "AttName-0": "crushes you with a huge fist", "Att%-0": 90, "AttTrue%-0": 89.7,
            "AttAcc-0": 150, "AttMin-0": 21, "AttMax-0": 64, "AttEnergy-0": 250,
            "AttType-1": 1, "AttName-1": "pummels you with both fists", "Att%-1": 100, "AttTrue%-1": 10.3,
            "AttAcc-1": 150, "AttMin-1": 34, "AttMax-1": 90, "AttEnergy-1": 250
          }
        ]
        """);
        File.WriteAllText(Path.Combine(setDir, "Spells.json"), """
        [
          {
            "Number": 135, "Name": "ice storm", "Short": "isto", "ReqLevel": 1, "ManaCost": 20,
            "MinBase": 3, "MaxBase": 3, "Targets": 12, "Dur": 15, "Magery": 1, "MageryLVL": 1,
            "Learnable": 1, "Abil-0": 22, "AbilVal-0": -6
          }
        ]
        """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private MonsterIntelViewModel Build(out SpellbookState spellbook)
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("para-set");
        // Agility 150 at level 10: raw dodge 35, 7% against accuracy 150 on Paradigm.
        var stats = new PlayerStats { Name = "Ridic", Class = "Mage", Level = 10, Agility = 150, Charm = 50 };
        var inventory = new InventoryManager(log: null, itemWeightResolver: null, slotResolver: null);
        spellbook = new SpellbookState(new KnownSpellCatalog(cache));
        spellbook.Refresh(12, 10);
        var profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        var resolver = new SettingsResolver(new SettingsService(), new BbsProfileStore(), profile);
        var vm = new MonsterIntelViewModel(
            cache, new MonsterCatalog(cache), resolver, stats, inventory, spellbook, new ItemMagicIndex(cache),
            observations: null, playerState: null);
        inventory.Dispose();
        // AC far past what 150 accuracy can get through, so only the floor decides.
        vm.SimAc = 500;
        return vm;
    }

    private static MonsterIntelEntry Zombie(MonsterIntelViewModel vm)
        => vm.RowsView.Cast<MonsterIntelEntry>().Single(e => e.Name == "brute zombie");

    [Fact]
    public void List_UsesTheSilkFloor_AndCountsDodgeBelowIt()
    {
        using MonsterIntelViewModel vm = Build(out _);
        // 1% floor × (100 − 7)% dodge missed.
        Assert.Equal(0.93, Zombie(vm).IncomingHitChance, 6);
        Assert.Equal("0.9%", Zombie(vm).IncomingHitPercentText);
    }

    [Fact]
    public void Matchup_ShowsTheListFigure_ThenEachPhysicalAttack()
    {
        using MonsterIntelViewModel vm = Build(out _);
        MonsterIntelEntry zombie = Zombie(vm);
        vm.SelectedEntry = zombie;

        string[] lines = vm.IncomingThreatLines.ToArray();
        Assert.StartsWith($"Melee: {zombie.IncomingHitPercentText} to hit you over its attacks", lines[0]);
        Assert.Contains("crushes you with a huge fist (89.7% of its attacks): 0.9% to hit you · acc 150", lines[1]);
        Assert.Contains("pummels you with both fists (10.3% of its attacks): 0.9% to hit you · acc 150", lines[2]);
        // The Attacks rows read the same per-attack figure.
        Assert.All(vm.AttackRows, r => Assert.EndsWith("→ 0.9% to hit you", r.Detail));
    }

    [Fact]
    public void AppliedDebuff_MovesTheListWithTheMatchup()
    {
        using MonsterIntelViewModel vm = Build(out SpellbookState spellbook);
        spellbook.SetObtainedByNames(new[] { "ice storm" });
        SpellPickRow isto = Assert.Single(vm.DebuffOptions);

        // Off the floor, so an accuracy cut shows: AC 100 against accuracy 150.
        vm.SimAc = 100;
        MonsterIntelEntry zombie = Zombie(vm);
        double before = zombie.IncomingHitChance;
        vm.SelectedEntry = zombie;

        isto.Applied = true;

        Assert.True(zombie.IncomingHitChance < before, "the debuff must lower the list's Hits You %");
        Assert.StartsWith($"Melee: {zombie.IncomingHitPercentText} to hit you", vm.IncomingThreatLines[0]);
        Assert.Contains("acc 150→144", vm.IncomingThreatLines[1]);
    }
}
