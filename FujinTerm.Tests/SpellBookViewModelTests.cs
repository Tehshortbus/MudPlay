using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FujinTerm.Game.Spells;
using FujinTerm.Services;
using FujinTerm.ViewModels;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins <see cref="SpellBookViewModel"/> — the projection of
/// <see cref="SpellbookState"/> into rendered, filtered rows. Reuses the
/// synthetic Mage / Warrior class + spell rows from the other spellbook tests.
/// </summary>
public sealed class SpellBookViewModelTests : IDisposable
{
    private readonly string _root;

    public SpellBookViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-spellbookvm-" + Path.GetRandomFileName());
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
    ];

    private static readonly object[] _spells =
    [
        SpellRow(100, "starlight", "star", magery: 1, mageryLvl: 1, reqLevel: 2),
        SpellRow(101, "high arc", "high", magery: 1, mageryLvl: 3, reqLevel: 5),
        SpellRow(103, "gated", "lvlg", magery: 1, mageryLvl: 1, reqLevel: 20),
    ];

    private SpellbookState NewBook(int classNumber, int level)
    {
        string dir = Path.Combine(_root, "set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), JsonSerializer.Serialize(_spells));
        File.WriteAllText(Path.Combine(dir, "Classes.json"), JsonSerializer.Serialize(_classes));

        GameDataCache cache = new(_root);
        cache.SwitchSet("set");
        KnownSpellCatalog catalog = new(cache);
        SpellbookState book = new(catalog);
        book.Refresh(classNumber, level);
        return book;
    }

    [Fact]
    public void Rows_MirrorAvailableList_SortedByLevel()
    {
        SpellbookState book = NewBook(classNumber: 12, level: 5);
        using SpellBookViewModel vm = new(book) { ShowAllSpells = true };

        Assert.Equal(3, vm.Rows.Count); // Show-all = the full class list
        Assert.Equal(new[] { "starlight", "high arc", "gated" }, vm.Rows.Select(r => r.Name));
    }

    [Fact]
    public void LevelGate_HidesAboveLevelSpells_UnlessShowAll()
    {
        SpellbookState book = NewBook(classNumber: 12, level: 5);
        using SpellBookViewModel vm = new(book);   // default: level-gated

        // "gated" (ReqLevel 20) is hidden at level 5; req-2 + req-5 spells show.
        Assert.Equal(2, vm.Rows.Count);
        Assert.DoesNotContain(vm.Rows, r => r.Name == "gated");

        vm.ShowAllSpells = true;
        Assert.Equal(3, vm.Rows.Count);
        Assert.Contains(vm.Rows, r => r.Name == "gated");
    }

    [Fact]
    public void ObtainedGlyph_TracksBookState()
    {
        SpellbookState book = NewBook(classNumber: 12, level: 5);
        book.SetObtainedByNames(new[] { "starlight" });
        using SpellBookViewModel vm = new(book);

        SpellBookRowViewModel star = vm.Rows.Single(r => r.Name == "starlight");
        SpellBookRowViewModel high = vm.Rows.Single(r => r.Name == "high arc");
        Assert.True(star.IsObtained);
        Assert.False(high.IsObtained);
    }

    [Fact]
    public void BookChanged_RebuildsRows()
    {
        SpellbookState book = NewBook(classNumber: 12, level: 5);
        using SpellBookViewModel vm = new(book);
        Assert.False(vm.Rows.Single(r => r.Name == "high arc").IsObtained);

        book.MarkObtainedByName("high arc"); // fires Changed

        Assert.True(vm.Rows.Single(r => r.Name == "high arc").IsObtained);
    }

    [Fact]
    public void Search_FiltersByNameAndShort()
    {
        SpellbookState book = NewBook(classNumber: 12, level: 5);
        using SpellBookViewModel vm = new(book) { ShowAllSpells = true };

        vm.SearchText = "high";   // matches name "high arc"
        Assert.Single(vm.Rows);
        Assert.Equal("high arc", vm.Rows[0].Name);

        vm.SearchText = "star";   // matches short "star" / name "starlight"
        Assert.Single(vm.Rows);
        Assert.Equal("starlight", vm.Rows[0].Name);

        vm.SearchText = "";       // cleared → all back
        Assert.Equal(3, vm.Rows.Count);
    }

    [Fact]
    public void ShowObtainedOnly_HidesUnlearned()
    {
        SpellbookState book = NewBook(classNumber: 12, level: 5);
        book.SetObtainedByNames(new[] { "starlight" });
        using SpellBookViewModel vm = new(book);

        vm.ShowObtainedOnly = true;
        Assert.Single(vm.Rows);
        Assert.Equal("starlight", vm.Rows[0].Name);
    }

    [Fact]
    public void Header_UsesClassNameProviderAndLevel()
    {
        SpellbookState book = NewBook(classNumber: 12, level: 7);
        using SpellBookViewModel vm = new(book, () => "Mage");

        Assert.Equal("Mage — Level 7", vm.HeaderText);
    }

    [Fact]
    public void Status_ShowsObtainedOfTotal()
    {
        SpellbookState book = NewBook(classNumber: 12, level: 5);
        book.SetObtainedByNames(new[] { "starlight" });
        using SpellBookViewModel vm = new(book) { ShowAllSpells = true };

        Assert.Equal("1 of 3 learned", vm.StatusText);
    }

    [Fact]
    public void NonMageryClass_HasEmptyBook()
    {
        SpellbookState book = NewBook(classNumber: 1, level: 5); // Warrior
        using SpellBookViewModel vm = new(book, () => "Warrior");

        Assert.Empty(vm.Rows);
        Assert.Equal("Spell Book — no spells for this class", vm.HeaderText);
        Assert.Equal("This class has no spell book.", vm.StatusText);
    }

    // ----- synthetic-row builders (mirror SpellListParserTests) ----------

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
