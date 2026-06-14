using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FujinTerm.Game.Calculators;
using FujinTerm.Game.Inventory;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="CharacterCalculator.AggregateEquipmentStats"/> folds every worn
/// item's base AC/DR + <c>Abil-0..19</c> bonuses against a game-data
/// <c>Items</c> table. These tests seed an isolated set, build worn-item lists,
/// and assert the totals + per-stat tooltip sources match the MMUD ability map.
/// </summary>
public sealed class EquipmentStatAggregationTests : IDisposable
{
    private readonly string _root;

    public EquipmentStatAggregationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-eqstat-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // Builds an Items table from field dictionaries (hyphenated Abil-N keys
    // match the game-data schema the aggregation reads) and activates the set.
    private GameDataCache CacheWithItems(params Dictionary<string, object>[] items)
    {
        string dir = Path.Combine(_root, "test-set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Items.json"),
            JsonSerializer.Serialize(items));
        GameDataCache cache = new(_root);
        cache.SwitchSet("test-set");
        return cache;
    }

    private static Dictionary<string, object> Item(string name, params (string Key, int Value)[] fields)
    {
        Dictionary<string, object> d = new() { ["Name"] = name };
        foreach ((string key, int value) in fields) d[key] = value;
        return d;
    }

    [Fact]
    public void BaseArmourAndDamageResist_DivideByTen()
    {
        GameDataCache cache = CacheWithItems(
            Item("padded vest", ("ArmourClass", 50), ("DamageResist", 30)));

        EquipmentStatBreakdown b = CharacterCalculator.AggregateEquipmentStats(
            new[] { new EquippedItem("padded vest", "Torso") }, cache);

        Assert.Equal(5.0, b.Totals.PlusAC);
        Assert.Equal(3.0, b.Totals.PlusDR);
        Assert.True(b.PerStatSources.ContainsKey("Armour Class"));
        Assert.Equal("padded vest", b.PerStatSources["Armour Class"][0].ItemName);
    }

    [Fact]
    public void AbilityBonuses_MapToStatFields()
    {
        // Abil 46 = +Strength, 2 = +AC (raw), 88 = +Max HP.
        GameDataCache cache = CacheWithItems(Item("girdle of might",
            ("Abil-0", 46), ("AbilVal-0", 5),
            ("Abil-1", 2), ("AbilVal-1", 10),
            ("Abil-2", 88), ("AbilVal-2", 25)));

        EquipmentStatBreakdown b = CharacterCalculator.AggregateEquipmentStats(
            new[] { new EquippedItem("girdle of might", "Waist") }, cache);

        Assert.Equal(5, b.Totals.PlusStrength);
        Assert.Equal(10, b.Totals.PlusAC);
        Assert.Equal(25, b.Totals.PlusMaxHp);
        Assert.Contains("Strength", b.PerStatSources.Keys);
        Assert.Equal("+5", b.PerStatSources["Strength"][0].DisplayValue);
    }

    [Fact]
    public void DamageResistAbility_StoredTimesTen_DisplaysDivided()
    {
        // Abil 7 = +DR, stored ×10 in game data → +2.5 DR.
        GameDataCache cache = CacheWithItems(Item("ring of warding",
            ("Abil-0", 7), ("AbilVal-0", 25)));

        EquipmentStatBreakdown b = CharacterCalculator.AggregateEquipmentStats(
            new[] { new EquippedItem("ring of warding", "Finger") }, cache);

        Assert.Equal(2.5, b.Totals.PlusDR);
        Assert.Equal("+2.5", b.PerStatSources["Damage Resist"][0].DisplayValue);
    }

    [Fact]
    public void WeaponHand_CapturesAccuracyAndStrReq()
    {
        GameDataCache cache = CacheWithItems(Item("quarterstaff",
            ("Accy", 7), ("StrReq", 40)));

        EquipmentStatBreakdown b = CharacterCalculator.AggregateEquipmentStats(
            new[] { new EquippedItem("quarterstaff", "Weapon Hand") }, cache);

        Assert.Equal(7, b.Totals.WeaponHandAccy);
        Assert.Equal(40, b.Totals.WeaponStrReq);
        Assert.Equal(7, b.Totals.TotalWornAccy);
    }

    [Fact]
    public void HitMagic_SumsAllItems_ButWeaponBreakdownOnly()
    {
        // Abil 28 = Hit Magic. Both pieces add to PlusHitMagic; only the
        // weapon-hand item surfaces in WeaponHitMagic + the breakdown.
        GameDataCache cache = CacheWithItems(
            Item("flaming sword", ("Abil-0", 28), ("AbilVal-0", 3)),
            Item("magic ring", ("Abil-0", 28), ("AbilVal-0", 2)));

        EquipmentStatBreakdown b = CharacterCalculator.AggregateEquipmentStats(
            new[]
            {
                new EquippedItem("flaming sword", "Weapon Hand"),
                new EquippedItem("magic ring", "Finger"),
            }, cache);

        Assert.Equal(5, b.Totals.PlusHitMagic);
        Assert.Equal(3, b.Totals.WeaponHitMagic);
        Assert.Single(b.PerStatSources["Hit Magic"]);
        Assert.Equal("flaming sword", b.PerStatSources["Hit Magic"][0].ItemName);
    }

    [Fact]
    public void UnknownItem_SkippedSilently()
    {
        GameDataCache cache = CacheWithItems(
            Item("padded vest", ("ArmourClass", 50)));

        EquipmentStatBreakdown b = CharacterCalculator.AggregateEquipmentStats(
            new[] { new EquippedItem("nonexistent gizmo", "Worn") }, cache);

        Assert.Equal(0.0, b.Totals.PlusAC);
        Assert.Empty(b.PerStatSources);
    }

    [Fact]
    public void ApplyAbilityBonuses_FoldsRaceOrClassAbilities()
    {
        // Race/class records iterate Abil-0..9; 48 = +Agility.
        using JsonDocument doc = JsonDocument.Parse(
            """{ "Name": "Halfling", "Abil-0": 48, "AbilVal-0": 4 }""");

        EquipmentStatBreakdown b = new();
        CharacterCalculator.ApplyAbilityBonuses(b, doc.RootElement, "Halfling");

        Assert.Equal(4, b.Totals.PlusAgility);
        Assert.Equal("Halfling", b.PerStatSources["Agility"][0].ItemName);
    }
}
