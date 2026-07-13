using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FujinTerm.Game;
using FujinTerm.Game.Calculators;
using FujinTerm.Game.Inventory;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

// Coverage for ItemFinderEntry.BuildCatalog — the two behaviours the Item Finder
// depends on that compile-time checking can't catch: dropping items the realm
// never puts in play (In Game == 0), and folding the live character's swing
// context into a per-weapon 10-round swing average.
public sealed class ItemFinderCatalogTests : IDisposable
{
    private readonly string _root;

    public ItemFinderCatalogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-itemfinder-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // - keen dagger  : 1H Sharp weapon, In Game 1 — kept.
    // - phantom blade: 2H Sharp weapon, In Game 0 — sysop/unobtainable, dropped.
    // - legacy mace  : 1H Blunt weapon, no "In Game" field — treated as obtainable.
    // - amber amulet : neck armour (Worn 8), In Game 1 — kept, never a weapon.
    // - silver bracer: wrist armour (ItemType 0, Worn 14) — kept; SlotLabel "Wrist".
    // - bright torch : light (ItemType 6) worn on the wrist (Worn 14) — resolves to a
    //                  slot but is limited-use, so the ItemType gate drops it.
    private const string Items =
        "[{\"Number\":1,\"Name\":\"keen dagger\",\"ItemType\":1,\"WeaponType\":2,\"Speed\":30,\"StrReq\":0,\"Min\":5,\"Max\":10,\"In Game\":1}," +
        " {\"Number\":2,\"Name\":\"phantom blade\",\"ItemType\":1,\"WeaponType\":3,\"Speed\":40,\"StrReq\":0,\"Min\":8,\"Max\":20,\"In Game\":0}," +
        " {\"Number\":3,\"Name\":\"legacy mace\",\"ItemType\":1,\"WeaponType\":0,\"Speed\":25,\"StrReq\":0,\"Min\":4,\"Max\":9}," +
        " {\"Number\":4,\"Name\":\"amber amulet\",\"ItemType\":0,\"Worn\":8,\"In Game\":1}," +
        " {\"Number\":5,\"Name\":\"silver bracer\",\"ItemType\":0,\"Worn\":14,\"In Game\":1}," +
        " {\"Number\":6,\"Name\":\"bright torch\",\"ItemType\":6,\"Worn\":14,\"In Game\":1}]";

    private static ItemFinderEntry.SwingContext UsableContext() => new(
        CombatLevel: 5, Level: 30, Agility: 60, Strength: 60,
        CurrentEncum: 0, MaxEncum: 100, Realm: RealmType.ParaMud);

    private GameDataCache SeededCache()
    {
        string dir = Path.Combine(_root, "realm");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Items.json"), Items);
        GameDataCache cache = new(_root);
        cache.SwitchSet("realm");
        return cache;
    }

    [Fact]
    public void BuildCatalog_ExcludesInGameZero_KeepsObtainableAndMissingFlag()
    {
        IReadOnlyList<ItemFinderEntry> catalog = ItemFinderEntry.BuildCatalog(SeededCache());

        HashSet<string> names = catalog.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("keen dagger", names);   // In Game 1
        Assert.Contains("legacy mace", names);   // no flag -> obtainable
        Assert.Contains("amber amulet", names);  // In Game 1
        Assert.DoesNotContain("phantom blade", names); // In Game 0 -> dropped
    }

    [Fact]
    public void BuildCatalog_NoSwingContext_LeavesWeaponSwingsBlank()
    {
        IReadOnlyList<ItemFinderEntry> catalog = ItemFinderEntry.BuildCatalog(SeededCache());

        ItemFinderEntry dagger = catalog.Single(e => e.Name == "keen dagger");
        Assert.Equal(0, dagger.AvgSwings);
        Assert.Equal(string.Empty, dagger.AvgSwingsText);
    }

    [Fact]
    public void BuildCatalog_WithSwingContext_WeaponCarriesTenRoundMean()
    {
        var ctx = new ItemFinderEntry.SwingContext(
            CombatLevel: 5, Level: 30, Agility: 60, Strength: 60,
            CurrentEncum: 0, MaxEncum: 100, Realm: RealmType.ParaMud);

        IReadOnlyList<ItemFinderEntry> catalog = ItemFinderEntry.BuildCatalog(SeededCache(), ctx);

        // Expected = the mean of the same 10-round swing sim the finder folds in.
        SwingCalcResult sim = CombatCalculator.CalcSwings(
            combatLevel: 5, level: 30, attackSpeed: 30, agility: 60,
            strength: 60, weaponStrReq: 0, currentEncum: 0, maxEncum: 100,
            realmType: RealmType.ParaMud);
        double expected = sim.SwingsPerRound.Average();

        Assert.True(expected > 0, "fixture should produce at least one swing per round");

        ItemFinderEntry dagger = catalog.Single(e => e.Name == "keen dagger");
        Assert.Equal(expected, dagger.AvgSwings, 5);
    }

    [Fact]
    public void BuildCatalog_WithSwingContext_ArmourHasNoSwings()
    {
        var ctx = new ItemFinderEntry.SwingContext(
            CombatLevel: 5, Level: 30, Agility: 60, Strength: 60,
            CurrentEncum: 0, MaxEncum: 100, Realm: RealmType.ParaMud);

        IReadOnlyList<ItemFinderEntry> catalog = ItemFinderEntry.BuildCatalog(SeededCache(), ctx);

        ItemFinderEntry amulet = catalog.Single(e => e.Name == "amber amulet");
        Assert.Equal(0, amulet.AvgSwings);
        Assert.Equal(string.Empty, amulet.AvgSwingsText);
    }

    [Fact]
    public void SwingContext_WithoutUsableLevel_IsNotUsable_AndYieldsZero()
    {
        // Level 0 (no character loaded) or an unresolved class combat level must not
        // fabricate swings — the column stays blank rather than dividing toward junk.
        var noLevel = new ItemFinderEntry.SwingContext(
            CombatLevel: 5, Level: 0, Agility: 60, Strength: 60,
            CurrentEncum: 0, MaxEncum: 100, Realm: RealmType.ParaMud);
        var noCombat = new ItemFinderEntry.SwingContext(
            CombatLevel: 0, Level: 30, Agility: 60, Strength: 60,
            CurrentEncum: 0, MaxEncum: 100, Realm: RealmType.ParaMud);

        Assert.False(noLevel.IsUsable);
        Assert.False(noCombat.IsUsable);
        Assert.Equal(0, noLevel.AvgSwingsFor(30, 0));
        Assert.Equal(0, noCombat.AvgSwingsFor(30, 0));
    }

    [Fact]
    public void BuildCatalog_DropsWornLimitedUseItems_KeepsArmourAndWeapons()
    {
        IReadOnlyList<ItemFinderEntry> catalog = ItemFinderEntry.BuildCatalog(SeededCache());

        HashSet<string> names = catalog.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("silver bracer", names);       // ItemType 0 armour -> kept
        Assert.Contains("keen dagger", names);         // ItemType 1 weapon -> kept
        Assert.DoesNotContain("bright torch", names);  // ItemType 6 light -> dropped
    }

    [Fact]
    public void BuildCatalog_WristSlot_UsesFamilyLabelWithoutDisambiguator()
    {
        IReadOnlyList<ItemFinderEntry> catalog = ItemFinderEntry.BuildCatalog(SeededCache());

        ItemFinderEntry bracer = catalog.Single(e => e.Name == "silver bracer");
        Assert.Equal("Wrist", bracer.SlotLabel);       // not "Wrist (1)"
    }

    [Fact]
    public void BuildCatalog_BashAttackType_DoublesEnergyHalvingSwings()
    {
        ItemFinderEntry.SwingContext ctx = UsableContext();

        IReadOnlyList<ItemFinderEntry> bash =
            ItemFinderEntry.BuildCatalog(SeededCache(), ctx, MudAttackType.Bash);

        SwingCalcResult sim = CombatCalculator.CalcSwings(
            combatLevel: 5, level: 30, attackSpeed: 30, agility: 60,
            strength: 60, weaponStrReq: 0, currentEncum: 0, maxEncum: 100,
            isBashing: true, realmType: RealmType.ParaMud);
        double expected = sim.SwingsPerRound.Average();

        ItemFinderEntry dagger = bash.Single(e => e.Name == "keen dagger");
        Assert.Equal(expected, dagger.AvgSwings, 5);
    }

    [Fact]
    public void BuildCatalog_SmashAttackType_LocksToOneSwingPerWeapon()
    {
        ItemFinderEntry.SwingContext ctx = UsableContext();

        IReadOnlyList<ItemFinderEntry> smash =
            ItemFinderEntry.BuildCatalog(SeededCache(), ctx, MudAttackType.Smash);

        ItemFinderEntry dagger = smash.Single(e => e.Name == "keen dagger");
        Assert.Equal(1, dagger.AvgSwings);
    }

    [Fact]
    public void BuildCatalog_MartialArts_AppendsBareHandedRow_AndBlanksWeaponSwings()
    {
        ItemFinderEntry.SwingContext ctx = UsableContext();

        IReadOnlyList<ItemFinderEntry> kick =
            ItemFinderEntry.BuildCatalog(SeededCache(), ctx, MudAttackType.Kick);

        // Real weapons don't swing under a martial-arts type — their column is blank.
        ItemFinderEntry dagger = kick.Single(e => e.Name == "keen dagger");
        Assert.Equal(0, dagger.AvgSwings);
        Assert.False(dagger.IsSynthetic);

        // The bare-handed Kick row is appended, flagged synthetic, and carries the
        // fixed-speed (1400) martial-arts swing rate.
        ItemFinderEntry kickRow = kick.Single(e => e.IsSynthetic);
        Assert.Equal("Kick", kickRow.Name);
        SwingCalcResult sim = CombatCalculator.CalcSwings(
            combatLevel: 5, level: 30, attackSpeed: 1400, agility: 60,
            strength: 60, weaponStrReq: 0, currentEncum: 0, maxEncum: 100,
            realmType: RealmType.ParaMud);
        Assert.Equal(sim.SwingsPerRound.Average(), kickRow.AvgSwings, 5);
    }

    [Fact]
    public void BuildCatalog_JumpkickSpeed_DiffersByRealm()
    {
        var para = new ItemFinderEntry.SwingContext(
            CombatLevel: 5, Level: 30, Agility: 60, Strength: 60,
            CurrentEncum: 0, MaxEncum: 100, Realm: RealmType.ParaMud);
        var stock = para with { Realm = RealmType.Stock };

        // Paradigm's highest-version jumpkick is slower (2800) than Stock's (1900),
        // so it lands fewer swings — the two must not model to the same rate.
        double paraRate = para.AvgSwingsForMartialArts(MudAttackType.Jumpkick);
        double stockRate = stock.AvgSwingsForMartialArts(MudAttackType.Jumpkick);

        double paraExpected = CombatCalculator.CalcSwings(
            5, 30, 2800, 60, 60, 0, 0, 100, realmType: RealmType.ParaMud)
            .SwingsPerRound.Average();
        double stockExpected = CombatCalculator.CalcSwings(
            5, 30, 1900, 60, 60, 0, 0, 100, realmType: RealmType.Stock)
            .SwingsPerRound.Average();

        Assert.Equal(paraExpected, paraRate, 5);
        Assert.Equal(stockExpected, stockRate, 5);
        Assert.True(stockRate > paraRate, "Stock's faster jumpkick should out-swing Paradigm's");
    }

    [Fact]
    public void BuildCatalog_MartialArts_NoSwingContext_AddsNoSyntheticRow()
    {
        IReadOnlyList<ItemFinderEntry> punch =
            ItemFinderEntry.BuildCatalog(SeededCache(), swing: null, MudAttackType.Punch);

        Assert.DoesNotContain(punch, e => e.IsSynthetic);
    }
}
