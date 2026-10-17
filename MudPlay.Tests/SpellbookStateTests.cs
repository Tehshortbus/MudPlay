using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MudPlay.Game.Spells;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins SpellbookState — the available/obtained model that the Spell Book window
// binds to. Reuses the same synthetic Mage class + spell rows shape as
// KnownSpellCatalogTests.
public sealed class SpellbookStateTests : IDisposable
{
    private readonly string _root;

    public SpellbookStateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-spellbook-" + Path.GetRandomFileName());
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

    private (KnownSpellCatalog catalog, SpellbookState book) New(object[]? spells = null)
    {
        string dir = Path.Combine(_root, "set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), JsonSerializer.Serialize(spells ?? _spells));
        File.WriteAllText(Path.Combine(dir, "Classes.json"), JsonSerializer.Serialize(_classes));

        GameDataCache cache = new(_root);
        cache.SwitchSet("set");
        KnownSpellCatalog catalog = new(cache);
        return (catalog, new SpellbookState(catalog));
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

    // ----- alignment (CharAlign) -----------------------------------------

    private static readonly object[] _alignedSpells =
    [
        SpellRow(300, "smite", "smit", magery: 1, mageryLvl: 1, reqLevel: 1, abil0: 97),  // GoodOnly
        SpellRow(301, "curse", "curs", magery: 1, mageryLvl: 1, reqLevel: 1, abil0: 98),  // EvilOnly
        SpellRow(302, "plain bolt", "bolt", magery: 1, mageryLvl: 1, reqLevel: 1),        // ungated
    ];

    [Fact]
    public void Refresh_KnownAlignment_ExcludesNonMatchingUnobtainedSpell()
    {
        SpellbookState book = New(_alignedSpells).book;
        book.Refresh(classNumber: 12, level: 1, charAlign: 1); // Good

        string[] names = Names(book.Available);
        Assert.Contains("smite", names);       // GoodOnly — matches
        Assert.Contains("plain bolt", names);  // ungated — always shows
        Assert.DoesNotContain("curse", names); // EvilOnly — not yet obtained, excluded
    }

    [Fact]
    public void Refresh_AlreadyObtainedSpell_SurvivesAlignmentDriftAwayFromIt()
    {
        // Learned "curse" (EvilOnly) while Evil-aligned — the normal path: it's
        // both eligible and obtained, so it shows regardless of the union logic.
        SpellbookState book = New(_alignedSpells).book;
        book.Refresh(classNumber: 12, level: 1, charAlign: 3); // Evil
        book.MarkObtainedByName("curse");
        Assert.Contains("curse", Names(book.Available));

        // The character's alignment has since drifted to Good — MajorMUD doesn't
        // retroactively un-teach a learned spell, so "curse" must still show even
        // though it no longer matches the character's CURRENT alignment. A
        // not-yet-obtained cross-alignment spell (there is none here past "curse")
        // would correctly stay excluded — see the sibling test above.
        book.Refresh(classNumber: 12, level: 1, charAlign: 1); // now Good
        string[] names = Names(book.Available);
        Assert.Contains("curse", names);   // obtained → survives the drift
        Assert.Contains("smite", names);   // GoodOnly — now eligible too
        Assert.True(book.IsObtained(book.Available.Single(s => s.Name == "curse").Number));
    }

    // Report paradigm-20261003-215442: our alignment reading only moves on a `who`
    // or `pro`, so it can say Neutral while the character is evil and has just learned
    // an evil-only spell. The game's own spell list outranks the reading.
    [Fact]
    public void SpellList_NamingASpellOurAlignmentReadingExcludes_MarksItLearned()
    {
        SpellbookState book = New(_alignedSpells).book;
        book.Refresh(classNumber: 12, level: 1, charAlign: 2); // read as Neutral
        Assert.DoesNotContain("curse", Names(book.Available));

        book.SetObtainedByNames(new[] { "plain bolt", "curse" });

        KnownSpell curse = book.Available.Single(s => s.Name == "curse");
        Assert.True(book.IsObtained(curse.Number));
        Assert.Equal(2, book.ObtainedCount);
        Assert.DoesNotContain("smite", Names(book.Available)); // unlearned, still excluded
    }

    [Fact]
    public void ClassSpells_HoldsTheWholeClassList_WhileAvailableStaysAlignmentGated()
    {
        SpellbookState book = New(_alignedSpells).book;
        book.Refresh(classNumber: 12, level: 1, charAlign: 1); // read as Good

        Assert.Equal(new[] { "curse", "plain bolt", "smite" }, Names(book.ClassSpells));
        Assert.DoesNotContain("curse", Names(book.Available));
    }

    [Fact]
    public void LearnLine_NamingASpellOurAlignmentReadingExcludes_MarksItLearned()
    {
        SpellbookState book = New(_alignedSpells).book;
        book.Refresh(classNumber: 12, level: 1, charAlign: 1); // read as Good

        KnownSpell? learned = book.MarkObtainedByName("curse");

        Assert.NotNull(learned);
        Assert.True(book.IsObtained(learned!.Value.Number));
        Assert.Contains("curse", Names(book.Available));
    }

    // ----- one cast code, several spells (the priest's word spells) --------

    private static readonly object[] _wordSpells =
    [
        SpellRow(400, "balanced word", "word", magery: 1, mageryLvl: 1, reqLevel: 1),
        SpellRow(401, "exalted word", "word", magery: 1, mageryLvl: 1, reqLevel: 1, abil0: 111), // not evil
        SpellRow(402, "tainted word", "word", magery: 1, mageryLvl: 1, reqLevel: 1, abil0: 110), // not good
        SpellRow(403, "plain bolt", "bolt", magery: 1, mageryLvl: 1, reqLevel: 1),
    ];

    // The alignment quest decides which word spell a priest learns, and the other two
    // can then never be learned (user, 2026-10-03). A neutral reading allows all three,
    // so before this the cast code resolved to the first of them, learned or not.
    [Fact]
    public void LearnedSpell_OwnsItsCastCode_AndRulesOutItsSiblings()
    {
        SpellbookState book = New(_wordSpells).book;
        book.Refresh(classNumber: 12, level: 1, charAlign: 2); // read as Neutral
        Assert.Equal(4, book.Available.Count);
        Assert.Equal("balanced word", book.FindByCastCode("word")!.Value.Name);

        book.SetObtainedByNames(new[] { "tainted word", "plain bolt" });

        Assert.Equal(new[] { "plain bolt", "tainted word" }, Names(book.Available));
        Assert.Equal("tainted word", book.FindByCastCode("word")!.Value.Name);
        Assert.Equal(4, book.ClassSpells.Count);   // the Spell Book window still lists all three
    }

    [Fact]
    public void LearnedSpell_RulesOutASiblingOurAlignmentNowAllows()
    {
        SpellbookState book = New(_wordSpells).book;
        book.Refresh(classNumber: 12, level: 1, charAlign: 3); // Evil
        book.MarkObtainedByName("tainted word");

        book.Refresh(classNumber: 12, level: 1, charAlign: 1); // drifted to Good

        // Tainted stays (learned); exalted is allowed by alignment but can't be learned now.
        Assert.Equal(new[] { "plain bolt", "tainted word" }, Names(book.Available));
    }

    [Fact]
    public void ClearingTheLearnedSet_BringsTheSiblingsBack()
    {
        SpellbookState book = New(_wordSpells).book;
        book.Refresh(classNumber: 12, level: 1, charAlign: 2);
        book.SetObtainedByNames(new[] { "exalted word" });
        Assert.Equal(2, book.Available.Count);

        book.ClearObtained();   // a reroll

        Assert.Equal(4, book.Available.Count);
    }

    [Fact]
    public void Refresh_CharAlignZero_NeverFiltersRegardlessOfObtainedState()
    {
        // Unknown alignment (no `who` observed yet) must never exclude anything —
        // the union-back-in logic is a no-op when CharAlign is 0.
        SpellbookState book = New(_alignedSpells).book;
        book.Refresh(classNumber: 12, level: 1, charAlign: 0);
        Assert.Equal(3, book.Available.Count);
    }

    [Fact]
    public void Reseed_AfterDataSetRenumber_KeepsObtainedByName()
    {
        // The set-swap bug: obtained spells were keyed by Spells.Number, so
        // swapping to a set that renumbers the rows (and the class) emptied the
        // book until a full profile reload. The obtained set is now name-backed
        // and Reseed re-resolves it against the new set — so the same spells stay
        // obtained under their new numbers.
        string root = Path.Combine(_root, "renum");
        Directory.CreateDirectory(root);
        WriteRenumberSet(root, "A", mageClass: 12, starNum: 100, highNum: 101);
        WriteRenumberSet(root, "B", mageClass: 8, starNum: 500, highNum: 501);

        GameDataCache cache = new(root);
        cache.SwitchSet("A");
        KnownSpellCatalog catalog = new(cache);
        SpellbookState book = new(catalog);

        book.Refresh(catalog.ResolveClassNumber("Mage") ?? 0, 5);
        book.SetObtainedByNames(new[] { "starlight", "high arc" });
        Assert.True(book.IsObtained(100));
        Assert.True(book.IsObtained(101));

        // Swap the active set to B (renumbered) and reseed, as the ActiveSetChanged
        // handler does — resolving the class number from the persisted class NAME.
        cache.SwitchSet("B");
        book.Reseed(catalog.ResolveClassNumber("Mage") ?? 0, 5);

        Assert.False(book.IsObtained(100));  // old set's numbers are gone
        Assert.False(book.IsObtained(101));
        Assert.True(book.IsObtained(500));   // re-resolved by name to B's numbers
        Assert.True(book.IsObtained(501));
        Assert.Equal(2, book.ObtainedCount);
        Assert.Equal(new[] { "starlight", "high arc" }, book.ObtainedNames);
    }

    // Report paradigm-20260820-055007 (learned spells lost on upgrade): profile load
    // can run before the game-data set is active, so Available is empty and no
    // persisted name resolves to a number yet. SetObtainedByNames would drop every
    // name here — then the immediate post-migration Save writes an empty set and the
    // learned list is gone. SeedObtainedNames keeps the names as the source of truth;
    // the numbers re-derive on the next Refresh/Reseed once Available exists.
    [Fact]
    public void SeedObtainedNames_BeforeAvailableResolves_RetainsNames_RefreshResolvesNumbers()
    {
        SpellbookState book = New().book;
        Assert.Empty(book.Available);                       // set not active yet

        book.SeedObtainedNames(new[] { "starlight", "high arc" });

        // Names retained even though nothing resolved to a number (Available empty).
        Assert.Equal(2, book.ObtainedNames.Count);
        Assert.Contains("starlight", book.ObtainedNames);
        Assert.Contains("high arc", book.ObtainedNames);
        Assert.Equal(0, book.ObtainedCount);                // numbers unresolved
        Assert.False(book.IsObtained(100));

        // Once the set is active, the numbers re-derive from the retained names.
        book.Refresh(classNumber: 12, level: 5);            // Mage
        Assert.True(book.IsObtained(100));
        Assert.True(book.IsObtained(101));
        Assert.Equal(2, book.ObtainedCount);
        Assert.Equal(new[] { "starlight", "high arc" }, book.ObtainedNames);
    }

    [Fact]
    public void SetObtainedByNames_BeforeAvailableResolves_DropsNames()
    {
        // The contrast that pins WHY profile load must Seed, not Set: the
        // resolve-required setter drops everything while Available is empty.
        SpellbookState book = New().book;
        Assert.Empty(book.Available);

        book.SetObtainedByNames(new[] { "starlight", "high arc" });
        Assert.Empty(book.ObtainedNames);
    }

    private void WriteRenumberSet(string root, string setName, int mageClass, int starNum, int highNum)
    {
        string dir = Path.Combine(root, setName);
        Directory.CreateDirectory(dir);
        object[] classes = [ClassRow(mageClass, "Mage", magery: 1, mageryLvl: 3)];
        object[] spells =
        [
            SpellRow(starNum, "starlight", "star", magery: 1, mageryLvl: 1, reqLevel: 2),
            SpellRow(highNum, "high arc", "high", magery: 1, mageryLvl: 3, reqLevel: 5),
        ];
        File.WriteAllText(Path.Combine(dir, "Spells.json"), JsonSerializer.Serialize(spells));
        File.WriteAllText(Path.Combine(dir, "Classes.json"), JsonSerializer.Serialize(classes));
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

    // ----- AvailablePicks.Learned (the picker's unlearned guard) ---------

    [Fact]
    public void AvailablePicks_NothingObtainedYet_AllLearned()
    {
        SpellbookState book = New().book;
        book.Refresh(12, 1);   // Mage; obtained set unknown (never parsed)

        // Nothing flagged before the spell list is known — no false strike-through.
        Assert.All(book.AvailablePicks, p => Assert.True(p.Learned));
    }

    [Fact]
    public void AvailablePicks_KnownObtainedSet_FlagsUnlearned()
    {
        SpellbookState book = New().book;
        book.Refresh(12, 1);
        book.SetObtainedByNames(new[] { "starlight" });

        Assert.True(book.AvailablePicks.Single(p => p.Short == "star").Learned);
        Assert.False(book.AvailablePicks.Single(p => p.Short == "high").Learned);
    }

    [Fact]
    public void AvailablePicks_LearnLine_FlipsToLearnedLive()
    {
        SpellbookState book = New().book;
        book.Refresh(12, 1);
        book.SetObtainedByNames(new[] { "starlight" });   // establishes a known set
        Assert.False(book.AvailablePicks.Single(p => p.Short == "high").Learned);

        // The "You add high arc to your spellbook!" path.
        book.MarkObtainedByName("high arc");
        Assert.True(book.AvailablePicks.Single(p => p.Short == "high").Learned);
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
    public void ObtainedNames_RoundTripsThroughSeedObtainedNames()
    {
        // The persistence path: ObtainedNames captured at save, fed back through
        // SeedObtainedNames at load, must reconstruct the exact obtained set.
        SpellbookState book = New().book;
        book.Refresh(12, 5);
        book.SetObtainedByNames(new[] { "starlight", "high arc" });

        IReadOnlyList<string> saved = book.ObtainedNames;
        Assert.Equal(new[] { "starlight", "high arc" }, saved);

        book.ClearObtained();
        Assert.Equal(0, book.ObtainedCount);

        book.SeedObtainedNames(saved);   // hydrate from the persisted names, as load does
        book.Refresh(12, 5);
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
        int number, string name, string shortCode, int magery, int mageryLvl, int reqLevel, int abil0 = 1)
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
            row[$"Abil-{x}"] = x == 0 ? abil0 : 0;
            row[$"AbilVal-{x}"] = 0;
        }
        return row;
    }
}
