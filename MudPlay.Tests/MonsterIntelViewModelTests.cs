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
using MudPlay.Terminal;
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
        => new(new SettingsService(), new BbsProfileStore(), new ProfileService());

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

        var profile = new ProfileService();
        profile.LoadBlank();   // non-null Current; Save() is a no-op for a blank draft
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
    // (Normal + Bash at least), exactly one is the rounds-to-kill basis (default
    // Normal), the radio is single-select, and both the pick and a hide persist
    // to the Character tier.
    [Fact]
    public void EditAttacks_DefaultsToNormal_SingleSelect_AndPersists()
    {
        var cache = new GameDataCache(_root);
        cache.SwitchSet("test-set");
        var catalog = new MonsterCatalog(cache);
        var stats = new PlayerStats { Name = "Tester", Level = 10, ArmourClass = 10, Agility = 50, Charm = 50 };
        using var inventory = new InventoryManager(log: null, itemWeightResolver: null, slotResolver: null);
        var spellbook = new SpellbookState(new KnownSpellCatalog(cache));
        var itemMagic = new ItemMagicIndex(cache);

        var profile = new ProfileService();
        profile.LoadBlank();
        var resolver = new SettingsResolver(new SettingsService(), new BbsProfileStore(), profile);

        using (var vm = new MonsterIntelViewModel(
            cache, catalog, resolver, stats, inventory, spellbook, itemMagic,
            observations: null, playerState: null))
        {
            Assert.NotEmpty(vm.AttackOptions);
            AttackPickRow normal = vm.AttackOptions.Single(o => o.Label == "Normal");
            Assert.True(normal.IsRoundsAttack);                                 // default basis
            Assert.Single(vm.AttackOptions.Where(o => o.IsRoundsAttack));

            AttackPickRow bash = vm.AttackOptions.Single(o => o.Label == "Bash");
            bash.IsRoundsAttack = true;                                         // switch basis
            Assert.False(normal.IsRoundsAttack);                               // single-select enforced
            Assert.Single(vm.AttackOptions.Where(o => o.IsRoundsAttack));

            normal.Shown = false;                                               // hide from Your Matchup
        }

        OtherSettings saved = resolver.Resolve<OtherSettings>("Other");
        Assert.Equal("melee:Bash", saved.MonsterIntelRoundsAttack);
        Assert.Contains("melee:Normal", saved.MonsterIntelHiddenAttacks);
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
        var lines = new LineExtractor(new TerminalEmulator(80, 24));
        inventory.AttachLineExtractor(lines);
        FieldInfo field = typeof(LineExtractor).GetField(
            "LineEmitted", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var handler = (Action<LineExtractor.EmittedLine>)field.GetValue(lines)!;
        void Feed(string text) => handler(new LineExtractor.EmittedLine(
            text, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));
        // PatchEquipped (which the wearing line below drives) is a no-op
        // until a full 'i' dump sets InventoryManager._loaded -- establish
        // that baseline first, then apply the incremental wear.
        Feed("You are carrying 0 copper farthings.");
        Feed("Wealth:    0 copper farthings");
        Feed("Encumbrance:    0/100  -  Light  [0%]");
        Feed("You are now wearing wraith ward.");
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

        var vm = new MonsterIntelViewModel(
            cache, catalog, NewResolver(), stats, inventory, spellbook, itemMagic,
            observations: null, playerState: null);
        inventory.Dispose();

        // Mutate the VM's backing list in place (same object RowsView was
        // constructed over) rather than replacing the field -- RowsView
        // wraps that exact List<T> by reference, so a field swap wouldn't
        // reach it, but Refresh() re-enumerates its current contents.
        FieldInfo allField = typeof(MonsterIntelViewModel).GetField("_all", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var all = (List<MonsterIntelEntry>)allField.GetValue(vm)!;
        MonsterIntelEntry synthetic = all.First(e => e.Name == "test goblin");
        synthetic.IncomingHitPercent = incomingHitPercent;
        all.Clear();
        all.Add(synthetic);
        vm.RowsView.Refresh();
        return vm;
    }
}
