using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FujinTerm.Game.Spells;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins <see cref="SpellbookState"/> — the available/obtained model that the
/// Spell Book window binds to — and <see cref="KnownSpellCatalog.GetByName"/>,
/// the name-keyed lookup the <c>spells</c>/<c>pow</c> list and learn-scroll
/// signal resolve through. Reuses the same synthetic Mage class + spell rows
/// shape as <see cref="KnownSpellCatalogTests"/>.
/// </summary>
public sealed class SpellbookStateTests : IDisposable
{
    private readonly string _root;

    public SpellbookStateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-spellbook-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static readonly object[] _classes =
    [
        ClassRow(1, "Warrior", magery: 0, mageryLvl: 0),
        ClassRow(12, "Mage", magery: 1, mageryLvl: 3),
        ClassRow(13, "Druid", magery: 3, mageryLvl: 3),
    ];

    private static readonly object[] _spells =
    [
        SpellRow(100, "starlight", "star", magery: 1, mageryLvl: 1, reqLevel: 2),
        SpellRow(101, "high arc", "high", magery: 1, mageryLvl: 3, reqLevel: 5),
        SpellRow(103, "gated", "lvlg", magery: 1, mageryLvl: 1, reqLevel: 20),
        // Druid-magery spell — never available to a Mage.
        SpellRow(200, "barkskin", "bark", magery: 3, mageryLvl: 1, reqLevel: 1),
    ];

    private (KnownSpellCatalog catalog, SpellbookState book) New()
    {
        string dir = Path.Combine(_root, "set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), JsonSerializer.Serialize(_spells));
        File.WriteAllText(Path.Combine(dir, "Classes.json"), JsonSerializer.Serialize(_classes));

        GameDataCache cache = new(_root);
        cache.SwitchSet("set");
        KnownSpellCatalog catalog = new(cache);
        return (catalog, new SpellbookState(catalog));
    }

    // ----- GetByName ----------------------------------------------------

    [Fact]
    public void GetByName_ResolvesUsableSpell_NullOtherwise()
    {
        KnownSpellCatalog catalog = New().catalog;

        Assert.Equal(100, catalog.GetByName("starlight", 12)!.Value.Number);
        Assert.Equal(100, catalog.GetByName("  StarLight ", 12)!.Value.Number); // trimmed + case-insensitive
        Assert.Null(catalog.GetByName("barkskin", 12));   // Druid magery — not a Mage spell
        Assert.Null(catalog.GetByName("starlight", 1));    // Warrior has no magery
        Assert.Null(catalog.GetByName("nope", 12));        // unknown name
    }

    // ----- Refresh / Available ------------------------------------------

    [Fact]
    public void Refresh_BuildsClassListIgnoringLevelGate()
    {
        SpellbookState book = New().book;
        book.Refresh(classNumber: 12, level: 1);

        // All Mage spells appear regardless of level (lvlg ReqLevel 20 incl.).
        string[] names = Names(book.Available);
        Assert.Contains("starlight", names);
        Assert.Contains("high arc", names);
        Assert.Contains("gated", names);
        Assert.DoesNotContain("barkskin", names); // Druid magery
    }

    [Fact]
    public void Refresh_NonMageryClass_EmptyBook()
    {
        SpellbookState book = New().book;
        book.Refresh(classNumber: 1, level: 50); // Warrior
        Assert.Empty(book.Available);
    }

    [Fact]
    public void Refresh_LevelOnlyChange_FiresChanged_KeepsAvailable()
    {
        SpellbookState book = New().book;
        book.Refresh(12, 1);
        IReadOnlyList<KnownSpell> first = book.Available;

        int fires = 0;
        book.Changed += () => fires++;
        book.Refresh(12, 5); // same class, new level

        Assert.Equal(1, fires);
        Assert.Same(first, book.Available); // class unchanged → list not rebuilt
        Assert.Equal(5, book.Level);
    }

    [Fact]
    public void Refresh_ClassChange_DropsObtainedOutsideNewClass()
    {
        SpellbookState book = New().book;
        book.Refresh(12, 5);
        book.MarkObtainedByName("starlight");
        Assert.Equal(1, book.ObtainedCount);

        book.Refresh(13, 1); // reroll into Druid — Mage spell no longer available
        Assert.Equal(0, book.ObtainedCount);
    }

    // ----- AvailablePicks (Settings spell-picker suggestion source) -----

    [Fact]
    public void AvailablePicks_NameOrdered_CarryCastCode()
    {
        SpellbookState book = New().book;
        book.Refresh(classNumber: 12, level: 1); // Mage

        // Ordered by name, distinct by cast-code, every Mage spell regardless
        // of level gate — each pick pairs the 4-letter code with the name.
        Assert.Equal(
            new[]
            {
                new SpellPick("lvlg", "gated"),
                new SpellPick("high", "high arc"),
                new SpellPick("star", "starlight"),
            },
            book.AvailablePicks);
    }

    [Fact]
    public void AvailablePicks_NonMageryClass_Empty()
    {
        SpellbookState book = New().book;
        book.Refresh(classNumber: 1, level: 50); // Warrior
        Assert.Empty(book.AvailablePicks);
    }

    [Fact]
    public void AvailablePicks_LevelOnlyChange_Unchanged()
    {
        SpellbookState book = New().book;
        book.Refresh(12, 1);
        IReadOnlyList<SpellPick> first = book.AvailablePicks;

        book.Refresh(12, 5); // same class, new level → picks not rebuilt
        Assert.Same(first, book.AvailablePicks);
    }

    // ----- obtained set -------------------------------------------------

    [Fact]
    public void SetObtainedByNames_ReplacesSet_IgnoresUnknown()
    {
        SpellbookState book = New().book;
        book.Refresh(12, 5);

        book.SetObtainedByNames(new[] { "starlight", "high arc", "phantom spell" });
        Assert.True(book.IsObtained(100));
        Assert.True(book.IsObtained(101));
        Assert.False(book.IsObtained(103));
        Assert.Equal(2, book.ObtainedCount); // unknown "phantom spell" dropped

        // A second snapshot that no longer lists high arc removes it.
        book.SetObtainedByNames(new[] { "starlight" });
        Assert.True(book.IsObtained(100));
        Assert.False(book.IsObtained(101));
    }

    [Fact]
    public void SetObtainedByNames_NoChange_NoEvent()
    {
        SpellbookState book = New().book;
        book.Refresh(12, 5);
        book.SetObtainedByNames(new[] { "starlight" });

        int fires = 0;
        book.Changed += () => fires++;
        book.SetObtainedByNames(new[] { "starlight" }); // identical resolved set
        Assert.Equal(0, fires);
    }

    [Fact]
    public void MarkObtainedByName_AddsOnce_ReturnsResolved()
    {
        SpellbookState book = New().book;
        book.Refresh(12, 5);

        int fires = 0;
        book.Changed += () => fires++;

        KnownSpell? first = book.MarkObtainedByName("high arc");
        Assert.Equal(101, first!.Value.Number);
        Assert.True(book.IsObtained(101));
        Assert.Equal(1, fires);

        // Re-learning the same spell resolves but fires no second event.
        Assert.NotNull(book.MarkObtainedByName("high arc"));
        Assert.Equal(1, fires);

        // Unknown name resolves to null, no event.
        Assert.Null(book.MarkObtainedByName("nope"));
        Assert.Equal(1, fires);
    }

    [Fact]
    public void ObtainedNames_RoundTripsThroughSetObtainedByNames()
    {
        // The persistence path: ObtainedNames captured at save, fed back through
        // SetObtainedByNames at load, must reconstruct the exact obtained set.
        SpellbookState book = New().book;
        book.Refresh(12, 5);
        book.SetObtainedByNames(new[] { "starlight", "high arc" });

        IReadOnlyList<string> saved = book.ObtainedNames;
        Assert.Equal(new[] { "starlight", "high arc" }, saved);

        book.ClearObtained();
        Assert.Equal(0, book.ObtainedCount);

        book.SetObtainedByNames(saved); // hydrate from the persisted names
        Assert.True(book.IsObtained(100));
        Assert.True(book.IsObtained(101));
        Assert.Equal(2, book.ObtainedCount);
    }

    [Fact]
    public void ObtainedNames_EmptyWhenNothingObtained()
    {
        SpellbookState book = New().book;
        book.Refresh(12, 5);
        Assert.Empty(book.ObtainedNames);
    }

    [Fact]
    public void ClearObtained_WipesSet_FiresOnceWhenNonEmpty()
    {
        SpellbookState book = New().book;
        book.Refresh(12, 5);
        book.SetObtainedByNames(new[] { "starlight", "high arc" });

        int fires = 0;
        book.Changed += () => fires++;
        book.ClearObtained();
        Assert.Equal(0, book.ObtainedCount);
        Assert.Equal(1, fires);

        book.ClearObtained(); // already empty → no event
        Assert.Equal(1, fires);
    }

    private static string[] Names(IEnumerable<KnownSpell> spells)
    {
        List<string> names = new();
        foreach (KnownSpell s in spells) names.Add(s.Name);
        return names.ToArray();
    }

    // ----- synthetic-row builders ---------------------------------------

    private static Dictionary<string, object> ClassRow(int number, string name, int magery, int mageryLvl)
        => new()
        {
            ["Number"] = number,
            ["Name"] = name,
            ["MageryType"] = magery,
            ["MageryLVL"] = mageryLvl,
        };

    private static Dictionary<string, object> SpellRow(
        int number, string name, string shortCode, int magery, int mageryLvl, int reqLevel)
    {
        Dictionary<string, object> row = new()
        {
            ["Number"] = number,
            ["Name"] = name,
            ["Short"] = shortCode,
            ["Magery"] = magery,
            ["MageryLVL"] = mageryLvl,
            ["ReqLevel"] = reqLevel,
            ["Learnable"] = 1,
            ["Learned From"] = "\0",
            ["Classes"] = "(*)",
            ["MinBase"] = 1,
            ["MaxBase"] = 0,
            ["MinInc"] = 0,
            ["MinIncLVLs"] = 0,
            ["MaxInc"] = 0,
            ["MaxIncLVLs"] = 0,
            ["Dur"] = 0,
            ["DurInc"] = 0,
            ["DurIncLVLs"] = 0,
            ["Cap"] = 0,
            ["EnergyCost"] = 0,
            ["ManaCost"] = 0,
        };
        for (int x = 0; x < 10; x++)
        {
            row[$"Abil-{x}"] = x == 0 ? 1 : 0;
            row[$"AbilVal-{x}"] = 0;
        }
        return row;
    }
}
