using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;
using Xunit;

namespace MudPlay.Tests;

// Pins the derived cross-reference rows SpellInfoRowsBuilder adds to a spell's
// Game Data tab: the "Negated by" reverse lookup (items whose NegateSpell-0..9
// list the spell) and the clickable record links (name text + a link per
// resolved record). Uses the same synthetic-table shape as the other
// GameDataCache tests.
public sealed class SpellInfoRowsBuilderTests : IDisposable
{
    private readonly string _root;

    public SpellInfoRowsBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-spellinfo-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private GameDataCache NewCache(object[] spells, object[]? items = null, object[]? monsters = null)
    {
        string dir = Path.Combine(_root, "set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), JsonSerializer.Serialize(spells));
        if (items is not null) File.WriteAllText(Path.Combine(dir, "Items.json"), JsonSerializer.Serialize(items));
        if (monsters is not null) File.WriteAllText(Path.Combine(dir, "Monsters.json"), JsonSerializer.Serialize(monsters));
        GameDataCache cache = new(_root);
        cache.SwitchSet("set");
        return cache;
    }

    private static Dictionary<string, object> SpellRow(int number, string name)
        => new() { ["Number"] = number, ["Name"] = name };

    private static Dictionary<string, object> NamedRow(int number, string name)
        => new() { ["Number"] = number, ["Name"] = name };

    private static Dictionary<string, object> ItemRow(int number, string name, params int[] negates)
    {
        var row = new Dictionary<string, object> { ["Number"] = number, ["Name"] = name };
        for (int i = 0; i < 10; i++) row[$"NegateSpell-{i}"] = i < negates.Length ? negates[i] : 0;
        return row;
    }

    [Fact]
    public void Build_ListsItemsThatNegateTheSpell_AsLinks()
    {
        GameDataCache cache = NewCache(
            spells: [SpellRow(50, "hold person"), SpellRow(51, "blindness")],
            items:
            [
                ItemRow(100, "Ring of Free Action", 50),   // negates hold person
                ItemRow(101, "Amulet of Clarity", 51, 50), // negates blindness AND hold person
                ItemRow(102, "Plain Dagger"),              // negates nothing
            ]);

        GameDataInfoRow negated = Assert.Single(
            new SpellInfoRowsBuilder(cache).Build(50).Where(r => r.Label == "Negated by"));

        // Value keeps the plain names (text fallback + what a reader scans).
        Assert.Contains("Ring of Free Action", negated.Value);
        Assert.Contains("Amulet of Clarity", negated.Value);
        Assert.DoesNotContain("Plain Dagger", negated.Value);

        // …and each item is a clickable link.
        Assert.True(negated.HasLinks);
        Assert.Equal(
            new[] { "Ring of Free Action", "Amulet of Clarity" },
            negated.Links!.Select(l => l.Name));
        Assert.All(negated.Links!, l => Assert.True(l.IsLinked));
    }

    [Fact]
    public void Build_NoNegatingItems_OmitsRow()
    {
        GameDataCache cache = NewCache(
            spells: [SpellRow(50, "hold person")],
            items: [ItemRow(102, "Plain Dagger")]);

        Assert.DoesNotContain(new SpellInfoRowsBuilder(cache).Build(50), r => r.Label == "Negated by");
    }

    [Fact]
    public void Build_CastedBySourceList_ResolvesMonsterLinks()
    {
        var spell = SpellRow(50, "vampire kill");
        spell["Casted By"] = "Monster #200, Monster #201";
        GameDataCache cache = NewCache(
            spells: [spell],
            monsters: [NamedRow(200, "vampire magus"), NamedRow(201, "vampire acolyte")]);

        GameDataInfoRow row = Assert.Single(
            new SpellInfoRowsBuilder(cache).Build(50).Where(r => r.Label == "Cast By"));

        Assert.True(row.HasLinks);
        Assert.Equal(new[] { "vampire magus [#200]", "vampire acolyte [#201]" }, row.Links!.Select(l => l.Name));
        Assert.All(row.Links!, l => Assert.True(l.IsLinked));
    }

    // The ethereal shield shape: a level-scaling AC Blur (code 10, stored 0), a
    // flat DR (code 7, stored 10), difficulty, and removed spells.
    private static Dictionary<string, object> EtherealShieldRow() => new()
    {
        ["Number"] = 4, ["Name"] = "ethereal shield", ["Short"] = "shld",
        ["ReqLevel"] = 5, ["ManaCost"] = 6, ["Diff"] = -5, ["Learnable"] = 1,
        ["MinBase"] = 3, ["MaxBase"] = 3, ["MinInc"] = 1, ["MinIncLVLs"] = 2,
        ["MaxInc"] = 1, ["MaxIncLVLs"] = 2, ["Cap"] = 18,
        ["Abil-0"] = 10, ["AbilVal-0"] = 0,       // AC Blur, scales
        ["Abil-1"] = 115, ["AbilVal-1"] = 8531,   // DescMsg — suppressed
        ["Abil-2"] = 122, ["AbilVal-2"] = 132,    // RemovesSpell mageshield
        ["Abil-3"] = 7, ["AbilVal-3"] = 10,       // DR 10 -> +1.0
        ["Abil-4"] = 122, ["AbilVal-4"] = 133,    // RemovesSpell protective shell
    };

    private static string? ValueOf(IReadOnlyList<GameDataInfoRow> rows, string label)
        => rows.FirstOrDefault(r => r.Label == label)?.Value;

    [Fact]
    public void Build_FriendlyLabels_ReplaceRawColumnNames()
    {
        GameDataCache cache = NewCache(spells: [EtherealShieldRow()]);
        var rows = new SpellInfoRowsBuilder(cache).Build(4);

        Assert.Equal("5", ValueOf(rows, "Required Level"));
        Assert.Equal("6", ValueOf(rows, "Mana Cost"));
        Assert.Equal("-5", ValueOf(rows, "Difficulty"));   // Diff renamed
        Assert.Equal("shld", ValueOf(rows, "Cast Code"));
        Assert.Equal("Yes", ValueOf(rows, "Learnable"));
        Assert.DoesNotContain(rows, r => r.Label is "Diff" or "ReqLevel" or "ManaCost" or "Short");
    }

    [Fact]
    public void Build_DrAbility_ShownAppliedToTheTenth()
    {
        GameDataCache cache = NewCache(spells: [EtherealShieldRow()]);
        var rows = new SpellInfoRowsBuilder(cache).Build(4);
        Assert.Equal("+1.0", ValueOf(rows, "DR"));   // raw 10 / 10
    }

    [Fact]
    public void Build_ScalingAffect_ShownAsRange_NotZero_AndNoMagnitudeRow()
    {
        GameDataCache cache = NewCache(spells: [EtherealShieldRow()]);
        var rows = new SpellInfoRowsBuilder(cache).Build(4);

        // AC Blur: 3 + floor(5/2) = 5 at req level, 3 + floor(18/2) = 12 at cap.
        Assert.Equal("Min: +5, Max: +12", ValueOf(rows, "AC Blur"));
        // The generic "Magnitude" growth row is gone — the affect row carries it.
        Assert.DoesNotContain(rows, r => r.Label == "Magnitude");
    }

    [Fact]
    public void Build_RemovesSpells_CollapseToOneLinkedRow()
    {
        GameDataCache cache = NewCache(
            spells:
            [
                EtherealShieldRow(),
                SpellRow(132, "mageshield"),
                SpellRow(133, "protective shell"),
            ]);
        var rows = new SpellInfoRowsBuilder(cache).Build(4);

        GameDataInfoRow removes = Assert.Single(rows.Where(r => r.Label == "Removes"));
        Assert.True(removes.HasLinks);
        Assert.Equal(new[] { "mageshield", "protective shell" }, removes.Links!.Select(l => l.Name));
        // No raw per-slot "RemovesSpell" rows survive the collapse.
        Assert.DoesNotContain(rows, r => r.Label == "RemovesSpell");
    }

    [Fact]
    public void Build_MessageOnlyAbility_IsDropped()
    {
        GameDataCache cache = NewCache(spells: [EtherealShieldRow()]);
        Assert.DoesNotContain(new SpellInfoRowsBuilder(cache).Build(4), r => r.Label == "DescMsg");
    }

    [Theory]
    [InlineData(0, "0 (between rounds)")]
    [InlineData(500, "500 (up to 2 times per round)")]   // 1000/500 fires twice
    [InlineData(334, "334 (up to 2 times per round)")]   // floor(1000/334) = 2
    [InlineData(1000, "1000 (once per round)")]
    [InlineData(700, "700 (once per round)")]            // floor(1000/700) = 1
    public void Build_EnergyCost_AnnotatesFireRate(int energy, string expected)
    {
        var spell = new Dictionary<string, object>
        {
            ["Number"] = 70, ["Name"] = "test spell", ["EnergyCost"] = energy,
        };
        var rows = new SpellInfoRowsBuilder(NewCache(spells: [spell])).Build(70);
        Assert.Equal(expected, ValueOf(rows, "Energy Cost"));
    }

    [Fact]
    public void Build_FlagBesideDamage_NotGivenScaledRange()
    {
        // A NonMagicalSpell (144) flag alongside a Damage (1) spell must render
        // name-only, never inherit the damage magnitude as a bogus scaled range.
        var spell = new Dictionary<string, object>
        {
            ["Number"] = 60, ["Name"] = "nonmagic bolt", ["ReqLevel"] = 8,
            ["MinBase"] = 13, ["MaxBase"] = 14,
            ["Abil-0"] = 1, ["AbilVal-0"] = 0,     // Damage — scales
            ["Abil-1"] = 144, ["AbilVal-1"] = 0,   // NonMagicalSpell flag
        };
        var rows = new SpellInfoRowsBuilder(NewCache(spells: [spell])).Build(60);

        string? flag = ValueOf(rows, "NonMagicalSpell");
        Assert.NotNull(flag);
        Assert.DoesNotContain("→", flag);   // never a scaled range
    }
}
