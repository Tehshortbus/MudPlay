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
    private const string Items =
        "[{\"Number\":1,\"Name\":\"keen dagger\",\"ItemType\":1,\"WeaponType\":2,\"Speed\":30,\"StrReq\":0,\"Min\":5,\"Max\":10,\"In Game\":1}," +
        " {\"Number\":2,\"Name\":\"phantom blade\",\"ItemType\":1,\"WeaponType\":3,\"Speed\":40,\"StrReq\":0,\"Min\":8,\"Max\":20,\"In Game\":0}," +
        " {\"Number\":3,\"Name\":\"legacy mace\",\"ItemType\":1,\"WeaponType\":0,\"Speed\":25,\"StrReq\":0,\"Min\":4,\"Max\":9}," +
        " {\"Number\":4,\"Name\":\"amber amulet\",\"ItemType\":0,\"Worn\":8,\"In Game\":1}]";

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
}
