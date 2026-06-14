using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FujinTerm.Game.Spells;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins <see cref="TrainLearnParser"/> — the collector that marks powers
/// obtained the moment training lists them ("You learn the following Kai
/// abilities:"). Incremental (never snapshots), so it mirrors the learn-scroll
/// path. Harness reuses the synthetic class + spell rows from
/// <see cref="SpellListParserTests"/>.
/// </summary>
public sealed class TrainLearnParserTests : IDisposable
{
    private readonly string _root;

    public TrainLearnParserTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-trainlearn-" + Path.GetRandomFileName());
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
        ClassRow(12, "Mystic", magery: 1, mageryLvl: 1),
    ];

    private static readonly object[] _spells =
    [
        SpellRow(200, "way of the swan", "swan", magery: 1, mageryLvl: 1, reqLevel: 2),
        SpellRow(201, "way of the crane", "cran", magery: 1, mageryLvl: 1, reqLevel: 3),
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
        book.Refresh(classNumber: 12, level: 2); // Mystic
        return book;
    }

    // ----- captured train output ---------------------------------------

    [Fact]
    public void KaiHeader_ThenName_ThenPrompt_MarksObtained()
    {
        SpellbookState book = NewBook();
        using TrainLearnParser parser = new(book);

        // Verbatim from the captured mystic `train`.
        parser.FeedTestLine("You learn the following Kai abilities:");
        parser.FeedTestLine("way of the swan");
        Assert.True(book.IsObtained(200)); // additive — marked as read, no commit wait
        parser.FeedTestLine("[HP=34/KAI=1]:", isPromptLine: true);

        Assert.True(book.IsObtained(200));
        Assert.Equal(1, book.ObtainedCount);
    }

    [Fact]
    public void MultipleAbilities_AllMarked()
    {
        SpellbookState book = NewBook();
        using TrainLearnParser parser = new(book);

        parser.FeedTestLine("You learn the following Kai abilities:");
        parser.FeedTestLine("way of the swan");
        parser.FeedTestLine("way of the crane");
        parser.FeedTestLine("[HP=34/KAI=1]:", isPromptLine: true);

        Assert.True(book.IsObtained(200));
        Assert.True(book.IsObtained(201));
        Assert.Equal(2, book.ObtainedCount);
    }

    // ----- additive, not snapshot --------------------------------------

    [Fact]
    public void DoesNotClearExistingObtained()
    {
        SpellbookState book = NewBook();
        book.SetObtainedByNames(new[] { "way of the crane" });
        Assert.Equal(1, book.ObtainedCount);

        using TrainLearnParser parser = new(book);
        parser.FeedTestLine("You learn the following Kai abilities:");
        parser.FeedTestLine("way of the swan");
        parser.FeedTestLine("[HP=34/KAI=1]:", isPromptLine: true);

        // Both the pre-existing and the newly learned power survive.
        Assert.True(book.IsObtained(200));
        Assert.True(book.IsObtained(201));
        Assert.Equal(2, book.ObtainedCount);
    }

    // ----- block boundaries --------------------------------------------

    [Fact]
    public void NamesOutsideHeader_Ignored()
    {
        SpellbookState book = NewBook();
        using TrainLearnParser parser = new(book);

        // A bare spell name with no header preceding it is not a learn.
        parser.FeedTestLine("way of the swan");
        Assert.Equal(0, book.ObtainedCount);
    }

    [Fact]
    public void PromptClosesBlock_LaterNameIgnored()
    {
        SpellbookState book = NewBook();
        using TrainLearnParser parser = new(book);

        parser.FeedTestLine("You learn the following Kai abilities:");
        parser.FeedTestLine("way of the swan");
        parser.FeedTestLine("[HP=34/KAI=1]:", isPromptLine: true);
        // A later bare name (e.g. a `pow` row's spell-name token line) must
        // not be folded in once the block closed.
        parser.FeedTestLine("way of the crane");

        Assert.True(book.IsObtained(200));
        Assert.False(book.IsObtained(201));
        Assert.Equal(1, book.ObtainedCount);
    }

    [Fact]
    public void BlankLineClosesBlock()
    {
        SpellbookState book = NewBook();
        using TrainLearnParser parser = new(book);

        parser.FeedTestLine("You learn the following Kai abilities:");
        parser.FeedTestLine("way of the swan");
        parser.FeedTestLine("");
        parser.FeedTestLine("way of the crane"); // after the block closed

        Assert.True(book.IsObtained(200));
        Assert.False(book.IsObtained(201));
        Assert.Equal(1, book.ObtainedCount);
    }

    // ----- synthetic-row builders (mirror SpellListParserTests) --------

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
