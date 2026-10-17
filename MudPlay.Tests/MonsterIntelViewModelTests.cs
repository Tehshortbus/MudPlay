using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels;
using Xunit;

namespace MudPlay.Tests;

// Regression coverage for a bug where the master list opened empty: RowsView
// is constructed with Filter = PassesFilter before RebuildCharacterCapabilities
// has computed any entry's IncomingHitPercent (every entry still holds the -1
// "no data" sentinel), and nothing refreshed the view afterward, so the
// character-context drop rule filtered out the entire catalog on open.
public sealed class MonsterIntelViewModelTests : IDisposable
{
    private readonly string _root;

    public MonsterIntelViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-intel-tests-" + Path.GetRandomFileName());
        string setDir = Path.Combine(_root, "test-set");
        Directory.CreateDirectory(setDir);
        File.WriteAllText(Path.Combine(setDir, "Monsters.json"), """
        [
          {
            "Number": 1, "Name": "test goblin", "Type": 1, "Align": 2, "HP": 10, "EXP": 50,
            "AttType-0": 1, "AttName-0": "hits you", "Att%-0": 100, "AttTrue%-0": 100,
            "AttAcc-0": 50, "AttMin-0": 1, "AttMax-0": 5, "AttEnergy-0": 100, "AttHitSpell-0": 0
          },
          {
            "Number": 2, "Name": "test wraith", "Type": 1, "Align": 2, "HP": 10, "EXP": 50,
            "AttType-0": 2, "AttName-0": "casts at you", "Att%-0": 100, "AttTrue%-0": 100,
            "AttAcc-0": 501, "AttMin-0": 5, "AttMax-0": 80, "AttEnergy-0": 100, "AttHitSpell-0": 0
          }
        ]
        """);
        File.WriteAllText(Path.Combine(setDir, "Items.json"), """
        [
          { "Name": "wraith ward", "ArmourClass": 200, "Abil-0": 24, "AbilVal-0": 15, "Abil-1": 9, "AbilVal-1": 50 }
        ]
        """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // No profile/BBS is loaded in these tests, so Resolve<T> falls through to
    // plain defaults -- same pattern several other tests already use to
    // construct these three services directly (no isolation ceremony needed;
    // Resolve<T> tolerates a null active profile/BBS via null-conditional).
    private static SettingsResolver NewResolver()
        => new(new SettingsService(), new BbsProfileStore(), TestProfiles.Service());

    // Regression: RoundsToKillCap moved from Settings -> Other into Monster
    // Intel's own window. Pins that editing it there actually persists to
    // the Character tier (the one storage location OtherSettings.RoundsToKillCap
    // still lives at) via SettingsResolver.WriteAt, and that re-resolving
    // afterward (e.g. on the next window open) sees the new value.
    [Fact]
    public void RoundsToKillCap_EditInWindow_PersistsToCharacterTier()
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);
        var stats = new PlayerStats { Name = "Tester", Level = 10, ArmourClass = 10, Agility = 50, Charm = 50 };
        using var inventory = new InventoryManager(log: null, itemWeightResolver: null, slotResolver: null);
        var spellbook = new SpellbookState(new KnownSpellCatalog(cache));
        var itemMagic = new ItemMagicIndex(cache);

        var profile = TestProfiles.Service();
        profile.LoadDefaultProfile();   // non-null Current; Save() is a no-op for a blank draft
        var resolver = new SettingsResolver(new SettingsService(), new BbsProfileStore(), profile);

        using (var vm = new MonsterIntelViewModel(
            cache, catalog, resolver, stats, inventory, spellbook, itemMagic,
            observations: null, playerState: null))
        {
            Assert.Equal(999, vm.RoundsToKillCap);   // default, nothing persisted yet
            vm.RoundsToKillCap = 42;
        }

        Assert.Equal(42, resolver.Resolve<OtherSettings>("Other").RoundsToKillCap);
    }

    // Edit Attacks picker: the roster always offers the usable melee attacks
    // (Normal + Bash at least) behind a leading "fastest of all" basis row, exactly
    // one row is the rounds-to-kill basis (default: the fastest-of-all row), the
    // radio is single-select, and both the pick and a hide persist to the
    // Character tier.
    [Fact]
    public void EditAttacks_DefaultsToFastestOfAll_SingleSelect_AndPersists()
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);
        var stats = new PlayerStats { Name = "Tester", Level = 10, ArmourClass = 10, Agility = 50, Charm = 50 };
        using var inventory = new InventoryManager(log: null, itemWeightResolver: null, slotResolver: null);
        var spellbook = new SpellbookState(new KnownSpellCatalog(cache));
        var itemMagic = new ItemMagicIndex(cache);

        var profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        var resolver = new SettingsResolver(new SettingsService(), new BbsProfileStore(), profile);

        using (var vm = new MonsterIntelViewModel(
            cache, catalog, resolver, stats, inventory, spellbook, itemMagic,
            observations: null, playerState: null))
        {
            Assert.NotEmpty(vm.AttackOptions);
            AttackPickRow fastest = vm.AttackOptions.First();
            Assert.Equal("best", fastest.Key);
            Assert.True(fastest.BasisOnly);                                     // not an attack: no Matchup toggle
            Assert.True(fastest.IsRoundsAttack);                                // default basis
            Assert.Single(vm.AttackOptions.Where(o => o.IsRoundsAttack));

            AttackPickRow normal = vm.AttackOptions.Single(o => o.Label == "Normal");
            Assert.False(normal.IsRoundsAttack);
            AttackPickRow bash = vm.AttackOptions.Single(o => o.Label == "Bash");
            bash.IsRoundsAttack = true;                                         // switch basis
            Assert.False(fastest.IsRoundsAttack);                               // single-select enforced
            Assert.Single(vm.AttackOptions.Where(o => o.IsRoundsAttack));

            normal.Shown = false;                                               // hide from Your Matchup
        }

        OtherSettings saved = resolver.Resolve<OtherSettings>("Other");
        Assert.Equal("melee:Bash", saved.MonsterIntelRoundsAttack);
        Assert.Contains("melee:Normal", saved.MonsterIntelHiddenAttacks);
    }

    // The reported failure: a caster whose spell kills a monster in a few casts saw
    // it filtered out because Est. Rounds to Kill defaulted to the Normal melee
    // swing. With the fastest-of-all basis the spell counts, so a monster melee
    // could never finish inside the cap still shows; picking the melee row instead
    // restores the old melee-only figure.
    [Fact]
    public void RoundsToKill_DefaultsToFastestAttack_SoACastersSpellCounts()
    {
        WriteCasterFixtures();
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);
        var stats = new PlayerStats { Name = "Tester", Level = 10, ArmourClass = 10, Agility = 50, Charm = 50 };
        using var inventory = new InventoryManager(log: null, itemWeightResolver: null, slotResolver: null);
        var spellbook = new SpellbookState(new KnownSpellCatalog(cache));
        spellbook.Reseed(classNumber: 12, level: 10);
        spellbook.SetObtainedByNames(new[] { "smite" });
        Assert.Equal(1, spellbook.ObtainedCount);
        var itemMagic = new ItemMagicIndex(cache);

        var profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        var resolver = new SettingsResolver(new SettingsService(), new BbsProfileStore(), profile);

        using var vm = new MonsterIntelViewModel(
            cache, catalog, resolver, stats, inventory, spellbook, itemMagic,
            observations: null, playerState: null);
        Assert.Equal(1, vm.KnownAttackSpellCount);

        vm.RoundsToKillCap = 6;
        MonsterIntelEntry tank = Assert.Single(
            vm.RowsView.Cast<MonsterIntelEntry>().Where(e => e.Name == "test tank"));
        Assert.InRange(tank.EstimatedRoundsToKill, 1, 6);     // the spell's figure, not bare-handed melee's

        // Explicitly basing it on a melee swing ignores the spell again.
        vm.AttackOptions.Single(o => o.Label == "Normal").IsRoundsAttack = true;
        Assert.NotInRange(tank.EstimatedRoundsToKill, 1, 6);

        // ...and picking the spell row uses that spell alone.
        vm.AttackOptions.Single(o => o.Label == "smite").IsRoundsAttack = true;
        Assert.InRange(tank.EstimatedRoundsToKill, 1, 6);
    }

    // The rounds cap is otherwise a silent filter, so the window says how many
    // monsters it is hiding — counting only those the cap alone removes, never ones
    // another filter (here the name box) already dropped.
    [Fact]
    public void RoundsCap_ReportsHowManyMonstersItHides()
    {
        using MonsterIntelViewModel vm = BuildViewModelWithSyntheticEntry(50);
        MonsterIntelEntry goblin = Assert.Single(vm.RowsView.Cast<MonsterIntelEntry>());
        goblin.EstimatedRoundsToKill = 50;
        Assert.False(vm.HasCapHidden);                        // default cap 999: nothing over it

        vm.RoundsToKillCap = 6;
        Assert.Empty(vm.RowsView.Cast<MonsterIntelEntry>());
        Assert.True(vm.HasCapHidden);
        Assert.Equal("1 more hidden by this cap", vm.CapHiddenText);

        vm.NameFilter = "no such monster";                   // dropped by the name box, not the cap
        Assert.False(vm.HasCapHidden);
        vm.NameFilter = null;
        Assert.True(vm.HasCapHidden);

        vm.RoundsToKillCap = 100;
        Assert.Single(vm.RowsView.Cast<MonsterIntelEntry>());
        Assert.False(vm.HasCapHidden);
    }

    // A monster the rounds attack can't kill ("—") stays listed at the default cap,
    // but a lowered cap asks what can be finished in N rounds, so it drops out and
    // counts as hidden (report paradigm-20260928-105509: Backstab at 1 round still
    // listed monsters one stab can't drop).
    [Fact]
    public void RoundsCap_Lowered_HidesMonstersTheAttackCantKill()
    {
        using MonsterIntelViewModel vm = BuildViewModelWithSyntheticEntry(50);
        MonsterIntelEntry goblin = Assert.Single(vm.RowsView.Cast<MonsterIntelEntry>());
        goblin.EstimatedRoundsToKill = 0;
        vm.RoundsToKillCap = 999;
        Assert.Single(vm.RowsView.Cast<MonsterIntelEntry>());

        vm.RoundsToKillCap = 1;
        Assert.Empty(vm.RowsView.Cast<MonsterIntelEntry>());
        Assert.True(vm.HasCapHidden);
    }

    // A caster class with one single-target damage spell (1000 dmg/round at any
    // level) it has learned, plus a monster only that spell can drop quickly. Written
    // over the shared fixture set before its GameDataCache is constructed.
    private void WriteCasterFixtures()
    {
        string setDir = Path.Combine(_root, "test-set");
        File.WriteAllText(Path.Combine(setDir, "Monsters.json"), """
        [
          {
            "Number": 3, "Name": "test tank", "Type": 1, "Align": 2, "HP": 5000, "EXP": 50,
            "AttType-0": 1, "AttName-0": "hits you", "Att%-0": 100, "AttTrue%-0": 100,
            "AttAcc-0": 50, "AttMin-0": 1, "AttMax-0": 5, "AttEnergy-0": 100, "AttHitSpell-0": 0
          }
        ]
        """);
        File.WriteAllText(Path.Combine(setDir, "Classes.json"), """
        [ { "Number": 12, "Name": "Mage", "MageryType": 1, "MageryLVL": 3 } ]
        """);
        File.WriteAllText(Path.Combine(setDir, "Spells.json"), """
        [
          {
            "Number": 100, "Name": "smite", "Short": "smit", "Magery": 1, "MageryLVL": 1, "ReqLevel": 1,
            "Learnable": 1, "Learned From": "\u0000", "Classes": "(*)", "Targets": 8, "AttType": 4,
            "MinBase": 1000, "MaxBase": 1000, "MinInc": 0, "MinIncLVLs": 0, "MaxInc": 0, "MaxIncLVLs": 0,
            "Dur": 0, "DurInc": 0, "DurIncLVLs": 0, "Cap": 0, "EnergyCost": 1000, "ManaCost": 5,
            "Abil-0": 1, "AbilVal-0": 0, "Abil-1": 0, "AbilVal-1": 0, "Abil-2": 0, "AbilVal-2": 0,
            "Abil-3": 0, "AbilVal-3": 0, "Abil-4": 0, "AbilVal-4": 0, "Abil-5": 0, "AbilVal-5": 0,
            "Abil-6": 0, "AbilVal-6": 0, "Abil-7": 0, "AbilVal-7": 0, "Abil-8": 0, "AbilVal-8": 0,
            "Abil-9": 0, "AbilVal-9": 0
          }
        ]
        """);
    }

    [Fact]
    public void MasterList_ShowsEntries_ImmediatelyOnConstruction_WithCharacterContext()
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);

        var stats = new PlayerStats { Name = "Tester", Level = 10, ArmourClass = 10, Agility = 50, Charm = 50 };
        var inventory = new InventoryManager(log: null, itemWeightResolver: null, slotResolver: null);
        var spellbook = new SpellbookState(new KnownSpellCatalog(cache));
        var itemMagic = new ItemMagicIndex(cache);

        using var vm = new MonsterIntelViewModel(
            cache, catalog, NewResolver(), stats, inventory, spellbook, itemMagic,
            observations: null, playerState: null);

        Assert.True(vm.HasCharacterContext);
        MonsterIntelEntry entry = Assert.Single(
            vm.RowsView.Cast<MonsterIntelEntry>().Where(e => e.Name == "test goblin"));
        Assert.Equal("50", entry.ExpText);
        Assert.NotEqual(string.Empty, entry.EstimatedRoundsToKillText);

        inventory.Dispose();
    }

    // EstimatedRoundsToKillText renders the raw projection: blank for no-context
    // (-1), "—" for can't-kill (0), else the plain number. The rounds-to-kill cap
    // is a LIST FILTER now (MonsterIntelViewModel.PassesFilter drops monsters over
    // it), not a per-row display clamp, so this text is never "<cap>+".
    [Theory]
    [InlineData(-1, "")]
    [InlineData(0, "—")]
    [InlineData(5, "5")]
    [InlineData(999, "999")]
    [InlineData(2_200_000, "2200000")]
    public void EstimatedRoundsToKillText_RendersRawProjection(int rounds, string expected)
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);
        MonsterIntelEntry entry = MonsterIntelEntry.BuildCatalog(catalog).First();

        entry.EstimatedRoundsToKill = rounds;

        Assert.Equal(expected, entry.EstimatedRoundsToKillText);
    }

    // Accuracy/AccuracyText surface the monster's own physical-attack
    // accuracy directly (the same value IncomingHitPercent already feeds
    // into CombatCalculator as attackerAccuracy) -- empty for a spell-only
    // monster with no physical slot, matching HpText/ExpText's "no data"
    // convention rather than showing 0.
    [Fact]
    public void AccuracyText_PhysicalAttacker_ShowsMajoritySlotAccuracy()
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);
        MonsterIntelEntry entry = MonsterIntelEntry.BuildCatalog(catalog).Single(e => e.Name == "test goblin");

        Assert.Equal(50, entry.Accuracy);
        Assert.Equal("50", entry.AccuracyText);
    }

    [Fact]
    public void AccuracyText_SpellOnlyMonster_IsEmpty()
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);
        MonsterIntelEntry entry = MonsterIntelEntry.BuildCatalog(catalog).Single(e => e.Name == "test wraith");

        Assert.Equal(0, entry.Accuracy);
        Assert.Equal(string.Empty, entry.AccuracyText);
    }

    // The defense simulator seeds to the live loadout on open: SimAc is the plain
    // AC that applies vs every attacker (worn gear + buffs, NOT Shadow — that's
    // the separate SimShadow toggle), while the evil-only wards seed into their
    // own fields. Worn "wraith ward" grants Shadow (Abil 9) + 15 Prot Evil (Abil
    // 24), so Shadow lands as the toggle, Prot Evil as its own value, and neither
    // inflates the plain AC.
    [Fact]
    public void DefenseSimulator_SeedsFromLiveLoadout_OnOpen()
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);
        var stats = new PlayerStats { Name = "Tester", Level = 10, ArmourClass = 30, Agility = 50, Charm = 50 };
        using var inventory = new InventoryManager(log: null, itemWeightResolver: null, slotResolver: null);
        ServerFeed feed = new();
        inventory.AttachLineExtractor(feed.Lines);
        // PatchEquipped (which the wearing line below drives) is a no-op
        // until a full 'i' dump sets InventoryManager._loaded -- establish
        // that baseline first, then apply the incremental wear.
        feed.Line("You are carrying 0 copper farthings.");
        feed.Line("Wealth:    0 copper farthings");
        feed.Line("Encumbrance:    0/100  -  Light  [0%]");
        feed.Line("You are now wearing wraith ward.");
        var spellbook = new SpellbookState(new KnownSpellCatalog(cache));
        var itemMagic = new ItemMagicIndex(cache);

        using var vm = new MonsterIntelViewModel(
            cache, catalog, NewResolver(), stats, inventory, spellbook, itemMagic,
            observations: null, playerState: null);

        // SimAc is the WORN-GEAR + buff AC (20 = wraith ward's ArmourClass 200 ÷10,
        // no buffs configured), NOT the live `stat` ArmourClass (30) — using the stat
        // double-counts any buffs already active when it was captured (the reported
        // 57→79 bug). Shadow lands as its own toggle (Abil 9); Prot Evil seeds to
        // 15 (Abil 24, evil-only). None of those are folded into the plain AC.
        Assert.Equal(20, vm.SimAc);
        Assert.True(vm.SimShadow);
        Assert.Equal(15, vm.SimProtEvil);
    }

    // The Hits-You-% filter bands are contiguous + non-overlapping: exactly one
    // band contains any given hit%, a monster shows only under the band that
    // contains its hit% (never a neighbour), and selecting no band shows all.
    [Theory]
    [InlineData(2)]
    [InlineData(15)]
    [InlineData(40)]
    [InlineData(100)]
    public void HitsFilterBands_ShowOnlyMonstersInTheSelectedBand(int hp)
    {
        using MonsterIntelViewModel vm = BuildViewModelWithSyntheticEntry(hp);
        bool Shown() => vm.RowsView.Cast<MonsterIntelEntry>().Any(e => e.Name == "test goblin");

        // Nothing selected → no band restriction, the entry shows.
        Assert.True(Shown());

        // Exactly one band contains the hp (contiguous, non-overlapping).
        HitsFilterBucket containing = Assert.Single(vm.HitsFilterBuckets.Where(b => b.Contains(hp)));

        // The containing band shows it; every other band hides it.
        foreach (HitsFilterBucket b in vm.HitsFilterBuckets)
        {
            b.Selected = true;
            Assert.Equal(b == containing, Shown());
            b.Selected = false;
        }
    }

    // The headline "hide unfightable mobs" rule: with a character loaded, a
    // monster with no computable Hits You % (IncomingHitPercent -1 — an NPC /
    // caster-only record with no physical attack, e.g. a trainer or quest-giver)
    // is dropped from the list entirely, even with no Hits-You-% box checked
    // (which otherwise shows everything).
    [Fact]
    public void UnfightableMonster_DroppedFromList_WhenCharacterLoaded()
    {
        using MonsterIntelViewModel vm = BuildViewModelWithSyntheticEntry(-1);
        Assert.DoesNotContain(vm.RowsView.Cast<MonsterIntelEntry>(), e => e.Name == "test goblin");
    }

    private MonsterIntelViewModel BuildViewModelWithSyntheticEntry(int incomingHitPercent)
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);
        var stats = new PlayerStats { Name = "Tester", Level = 10, ArmourClass = 10, Agility = 50, Charm = 50 };
        var inventory = new InventoryManager(log: null, itemWeightResolver: null, slotResolver: null);
        var spellbook = new SpellbookState(new KnownSpellCatalog(cache));
        var itemMagic = new ItemMagicIndex(cache);
        // A loaded (blank) profile so tests that edit the persisted rounds cap can write it.
        var profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        var resolver = new SettingsResolver(new SettingsService(), new BbsProfileStore(), profile);

        var vm = new MonsterIntelViewModel(
            cache, catalog, resolver, stats, inventory, spellbook, itemMagic,
            observations: null, playerState: null);
        inventory.Dispose();

        // Mutate the VM's backing list in place (same object RowsView was
        // constructed over) rather than replacing the field -- RowsView
        // wraps that exact List<T> by reference, so a field swap wouldn't
        // reach it, but Refresh() re-enumerates its current contents.
        FieldInfo allField = typeof(MonsterIntelViewModel).GetField("_all", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var all = (List<MonsterIntelEntry>)allField.GetValue(vm)!;
        MonsterIntelEntry synthetic = all.First(e => e.Name == "test goblin");
        synthetic.IncomingHitChance = incomingHitPercent;
        all.Clear();
        all.Add(synthetic);
        vm.RowsView.Refresh();
        return vm;
    }

    // Hide 0 exp drops the monsters worth no experience and nothing else. A monster
    // whose exp only comes from its multiplier still counts as worth something.
    [Fact]
    public void HideZeroExp_DropsOnlyMonstersWorthNoExperience()
    {
        string setDir = Path.Combine(_root, "exp-set");
        Directory.CreateDirectory(setDir);
        File.WriteAllText(Path.Combine(setDir, "Monsters.json"), """
        [
          { "Number": 1, "Name": "test goblin", "Type": 1, "Align": 2, "HP": 10, "EXP": 50 },
          { "Number": 2, "Name": "test shopkeeper", "Type": 1, "Align": 2, "HP": 10, "EXP": 0 },
          { "Number": 3, "Name": "test champion", "Type": 1, "Align": 2, "HP": 10, "EXP": 20, "ExpMulti": 3 }
        ]
        """);
        var cache = new GameDataCache(_root);
        cache.SwitchSet("exp-set");
        using var vm = new MonsterIntelViewModel(cache, new MonsterCatalog(cache), NewResolver());

        Assert.Equal(3, vm.RowsView.Cast<MonsterIntelEntry>().Count());

        vm.HideZeroExpMonsters = true;
        Assert.Equal(new[] { "test champion", "test goblin" },
            vm.RowsView.Cast<MonsterIntelEntry>().Select(e => e.Name).OrderBy(n => n).ToArray());

        vm.HideZeroExpMonsters = false;
        Assert.Equal(3, vm.RowsView.Cast<MonsterIntelEntry>().Count());
    }
}
