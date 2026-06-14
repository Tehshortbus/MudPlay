using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FujinTerm.Game.Spells;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins <see cref="SpellListParser"/> — the state machine that folds
/// <c>spells</c> / <c>pow</c> output into <see cref="SpellbookState"/>'s
/// obtained set. Format + matching are a faithful port of MMUD Explorer's
/// <c>PasteSpells</c>; the harness reuses the synthetic Mage class + spell
/// rows from <see cref="SpellbookStateTests"/>.
/// </summary>
public sealed class SpellListParserTests : IDisposable
{
    private readonly string _root;

    public SpellListParserTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-spelllist-" + Path.GetRandomFileName());
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

    private SpellbookState NewBook()
    {
        string dir = Path.Combine(_root, "set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), JsonSerializer.Serialize(_spells));
        File.WriteAllText(Path.Combine(dir, "Classes.json"), JsonSerializer.Serialize(_classes));

        GameDataCache cache = new(_root);
        cache.SwitchSet("set");
        KnownSpellCatalog catalog = new(cache);
        SpellbookState book = new(catalog);
        book.Refresh(classNumber: 12, level: 5); // Mage
        return book;
    }

    // ----- happy path ---------------------------------------------------

    [Fact]
    public void IntroHeader_ThenRows_ThenPrompt_CommitsObtained()
    {
        SpellbookState book = NewBook();
        using SpellListParser parser = new(book);

        parser.FeedTestLines(new[]
        {
            "You have the following spells:",
            "Level Mana Short Spell Name",
            "  2    5  star starlight",
            "  5   10  high high arc",
        });
        Assert.Equal(0, book.ObtainedCount);   // not committed until block ends
        parser.FeedTestLine("Mage> ", isPromptLine: true);

        Assert.True(book.IsObtained(100));
        Assert.True(book.IsObtained(101));
        Assert.False(book.IsObtained(103));
        Assert.Equal(2, book.ObtainedCount);
    }

    [Fact]
    public void ColumnHeaderAlone_OpensBlock()
    {
        SpellbookState book = NewBook();
        using SpellListParser parser = new(book);

        // No intro line — just the column header (some servers skip it).
        parser.FeedTestLines(new[]
        {
            "Level Mana Short Spell Name",
            "  2    5  star starlight",
        });
        parser.FeedTestLine("Mage> ", isPromptLine: true);

        Assert.True(book.IsObtained(100));
        Assert.Equal(1, book.ObtainedCount);
    }

    [Fact]
    public void KaiHeader_PowersList_Parses()
    {
        SpellbookState book = NewBook();
        using SpellListParser parser = new(book);

        parser.FeedTestLines(new[]
        {
            "You have the following powers:",
            "Level Kai  Short Spell Name",
            "  2    0  star starlight",   // Kai column may be 0
        });
        parser.FeedTestLine("Mage> ", isPromptLine: true);

        Assert.True(book.IsObtained(100));
    }

    [Fact]
    public void BlankLineAfterRows_EndsBlock()
    {
        SpellbookState book = NewBook();
        using SpellListParser parser = new(book);

        parser.FeedTestLines(new[]
        {
            "You have the following spells:",
            "Level Mana Short Spell Name",
            "  2    5  star starlight",
            "",                            // blank after rows → commit
        });

        Assert.True(book.IsObtained(100));
        Assert.Equal(1, book.ObtainedCount);
    }

    [Fact]
    public void NonRowLineAfterRows_EndsBlock_AndIsReFed()
    {
        SpellbookState book = NewBook();
        using SpellListParser parser = new(book);

        parser.FeedTestLines(new[]
        {
            "You have the following spells:",
            "Level Mana Short Spell Name",
            "  2    5  star starlight",
            "You have no spells.",         // footer/next output — ends block, then re-fed as empty-list
        });

        // The empty-list line re-feeds through the now-Idle parser and
        // clears the obtained set authoritatively.
        Assert.Equal(0, book.ObtainedCount);
    }

    // ----- empty list ---------------------------------------------------

    [Fact]
    public void NoSpellsLine_ClearsObtained()
    {
        SpellbookState book = NewBook();
        book.SetObtainedByNames(new[] { "starlight" });
        Assert.Equal(1, book.ObtainedCount);

        using SpellListParser parser = new(book);
        parser.FeedTestLine("You have no spells.");

        Assert.Equal(0, book.ObtainedCount);
    }

    [Fact]
    public void NoPowersLine_ClearsObtained()
    {
        SpellbookState book = NewBook();
        book.SetObtainedByNames(new[] { "starlight" });

        using SpellListParser parser = new(book);
        parser.FeedTestLine("You have no powers.");

        Assert.Equal(0, book.ObtainedCount);
    }

    // ----- snapshot replace ---------------------------------------------

    [Fact]
    public void SecondBlock_ReplacesObtainedSnapshot()
    {
        SpellbookState book = NewBook();
        using SpellListParser parser = new(book);

        parser.FeedTestLines(new[]
        {
            "You have the following spells:",
            "Level Mana Short Spell Name",
            "  2    5  star starlight",
            "  5   10  high high arc",
        });
        parser.FeedTestLine("Mage> ", isPromptLine: true);
        Assert.Equal(2, book.ObtainedCount);

        // A later `spells` run that no longer lists high arc removes it.
        parser.FeedTestLines(new[]
        {
            "You have the following spells:",
            "Level Mana Short Spell Name",
            "  2    5  star starlight",
        });
        parser.FeedTestLine("Mage> ", isPromptLine: true);

        Assert.True(book.IsObtained(100));
        Assert.False(book.IsObtained(101));
        Assert.Equal(1, book.ObtainedCount);
    }

    // ----- non-row noise ------------------------------------------------

    [Fact]
    public void ChatBeforeHeader_Ignored()
    {
        SpellbookState book = NewBook();
        using SpellListParser parser = new(book);

        // Lines before any header must not be mistaken for rows.
        parser.FeedTestLines(new[]
        {
            "Bob gossips: 5 10 lol nice spell name",
            "You have the following spells:",
            "Level Mana Short Spell Name",
            "  2    5  star starlight",
        });
        parser.FeedTestLine("Mage> ", isPromptLine: true);

        Assert.Equal(1, book.ObtainedCount);
        Assert.True(book.IsObtained(100));
    }

    [Fact]
    public void MultiWordSpellName_ResolvesByFullName()
    {
        SpellbookState book = NewBook();
        using SpellListParser parser = new(book);

        parser.FeedTestLines(new[]
        {
            "You have the following spells:",
            "Level Mana Short Spell Name",
            "  5   10  high high arc",     // name spans two tokens
        });
        parser.FeedTestLine("Mage> ", isPromptLine: true);

        Assert.True(book.IsObtained(101));
    }

    // ----- synthetic-row builders (mirror SpellbookStateTests) ----------

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
