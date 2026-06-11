using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FujinTerm.Game.Spells;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins <see cref="KnownSpellCatalog"/> against the MMUD Explorer
/// <c>SpellIsUsable</c> rules it ports: magery match, MageryLVL gate,
/// explicit class-token AND, level gate, the Kai-autolearn learnable
/// special, and the empty list for non-magery classes. Synthetic
/// <c>Spells.json</c> + <c>Classes.json</c> are seeded into an isolated
/// <see cref="GameDataCache"/> per test.
/// </summary>
public sealed class KnownSpellCatalogTests : IDisposable
{
    private readonly string _root;

    public KnownSpellCatalogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-known-spell-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // Stock-like class rows: Number / MageryType (enmMagicEnum) / MageryLVL.
    private static readonly object[] _classes =
    [
        ClassRow(1, "Warrior", magery: 0, mageryLvl: 0),
        ClassRow(11, "Warlock", magery: 1, mageryLvl: 2),  // Mage magery, low cap
        ClassRow(12, "Mage", magery: 1, mageryLvl: 3),     // Mage magery, full cap
        ClassRow(13, "Druid", magery: 3, mageryLvl: 3),    // Druid magery
        ClassRow(15, "Mystic", magery: 5, mageryLvl: 1),   // Kai magery
    ];

    // Synthetic spells, each exercising one SpellIsUsable branch.
    private static readonly object[] _spells =
    [
        // 100 star: Mage magery, level-1 cap, learnable, all-classes, real damage abil.
        SpellRow(100, "starlight", "star", magery: 1, mageryLvl: 1, reqLevel: 2,
            learnable: 1, classes: "(*)", minBase: 5, maxBase: 10, abil0: 1),
        // 101 high: needs MageryLVL 3 — gates out Warlock (cap 2).
        SpellRow(101, "high arc", "high", magery: 1, mageryLvl: 3, reqLevel: 1,
            learnable: 1, classes: "(*)", minBase: 1, abil0: 1),
        // 102 excl: explicit class token (12) — Mage only, even though Warlock magery-matches.
        SpellRow(102, "exclusive", "excl", magery: 1, mageryLvl: 1, reqLevel: 1,
            learnable: 1, classes: "(12)", minBase: 1, abil0: 1),
        // 103 lvlg: requires level 20 — gated by the level argument.
        SpellRow(103, "gated", "lvlg", magery: 1, mageryLvl: 1, reqLevel: 20,
            learnable: 1, classes: "(*)", minBase: 1, abil0: 1),
        // 104 nolr: not learnable, no LearnedFrom — filtered.
        SpellRow(104, "unlearnable", "nolr", magery: 1, mageryLvl: 1, reqLevel: 1,
            learnable: 0, learnedFrom: "\0", classes: "(*)", minBase: 1, abil0: 1),
        // 105 kai1: Kai magery, reqLevel>=1, not learnable — usable via Kai autolearn.
        SpellRow(105, "kai blast", "kai1", magery: 5, mageryLvl: 1, reqLevel: 3,
            learnable: 0, learnedFrom: "\0", classes: "(*)", minBase: 1, abil0: 1),
        // 106 kai0: Kai magery but reqLevel<1 — NOT autolearned, filtered.
        SpellRow(106, "kai dud", "kai0", magery: 5, mageryLvl: 1, reqLevel: 0,
            learnable: 0, learnedFrom: "\0", classes: "(*)", minBase: 1, abil0: 1),
    ];

    private KnownSpellCatalog NewCatalog()
    {
        string dir = Path.Combine(_root, "set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), JsonSerializer.Serialize(_spells));
        File.WriteAllText(Path.Combine(dir, "Classes.json"), JsonSerializer.Serialize(_classes));

        GameDataCache cache = new(_root);
        cache.SwitchSet("set");
        return new KnownSpellCatalog(cache);
    }

    private static string[] Shorts(IEnumerable<KnownSpell> spells) => spells.Select(s => s.Short).ToArray();

    [Fact]
    public void Query_Mage_IncludesMageryMatched_ExcludesMismatchAndUnlearnable()
    {
        string[] shorts = Shorts(NewCatalog().Query(classNumber: 12, level: 0));

        // Mage gets every Mage-magery learnable it qualifies for (cap 3).
        Assert.Contains("star", shorts);
        Assert.Contains("high", shorts);
        Assert.Contains("excl", shorts);
        Assert.Contains("lvlg", shorts);
        // Excludes: not-learnable, and both Kai spells (magery mismatch).
        Assert.DoesNotContain("nolr", shorts);
        Assert.DoesNotContain("kai1", shorts);
        Assert.DoesNotContain("kai0", shorts);
    }

    [Fact]
    public void Query_Warlock_MageryLvlGate_DropsHighLevelSpell()
    {
        string[] shorts = Shorts(NewCatalog().Query(classNumber: 11, level: 0));

        Assert.Contains("star", shorts);          // needs MageryLVL 1 — Warlock cap 2 OK
        Assert.DoesNotContain("high", shorts);    // needs MageryLVL 3 — Warlock cap 2 fails
        Assert.DoesNotContain("excl", shorts);    // explicit (12) — Warlock excluded
    }

    [Fact]
    public void Query_ExplicitClassToken_RestrictsToListed()
    {
        KnownSpellCatalog catalog = NewCatalog();
        Assert.Contains("excl", Shorts(catalog.Query(12, 0)));        // Mage listed
        Assert.DoesNotContain("excl", Shorts(catalog.Query(11, 0)));  // Warlock not listed
    }

    [Fact]
    public void Query_LevelGate_HidesSpellBelowReqLevel()
    {
        KnownSpellCatalog catalog = NewCatalog();
        Assert.DoesNotContain("lvlg", Shorts(catalog.Query(12, level: 5)));  // below req 20
        Assert.Contains("lvlg", Shorts(catalog.Query(12, level: 0)));        // 0 = ignore gate
        Assert.Contains("lvlg", Shorts(catalog.Query(12, level: 25)));       // above req 20
    }

    [Fact]
    public void Query_Druid_MageryMismatch_Empty()
        => Assert.Empty(NewCatalog().Query(classNumber: 13, level: 0));

    [Fact]
    public void Query_Warrior_NoMagery_Empty()
        => Assert.Empty(NewCatalog().Query(classNumber: 1, level: 0));

    [Fact]
    public void Query_Mystic_KaiAutolearn_IncludesKaiButNotReqLevelZero()
    {
        string[] shorts = Shorts(NewCatalog().Query(classNumber: 15, level: 0));

        Assert.Contains("kai1", shorts);        // Kai autolearn, reqLevel>=1
        Assert.DoesNotContain("kai0", shorts);  // reqLevel<1 → not autolearned
        Assert.DoesNotContain("star", shorts);  // Mage magery — mismatch for Kai class
    }

    [Fact]
    public void Query_SortedByReqLevelThenName()
    {
        // Mage list: excl(1), high(1), star(2), lvlg(20).
        // ReqLevel asc, ties broken by Name ("exclusive" < "high arc").
        KnownSpell[] spells = NewCatalog().Query(12, 0).ToArray();
        Assert.Equal(new[] { "excl", "high", "star", "lvlg" }, spells.Select(s => s.Short).ToArray());
    }

    [Fact]
    public void GetByShort_ReturnsUsable_NullOtherwise()
    {
        KnownSpellCatalog catalog = NewCatalog();

        Assert.Equal(100, catalog.GetByShort("star", 12)!.Value.Number);
        Assert.Null(catalog.GetByShort("star", 1));     // Warrior can't cast it
        Assert.Null(catalog.GetByShort("kai1", 12));    // Mage ≠ Kai magery
        Assert.Null(catalog.GetByShort("nope", 12));    // unknown shortcode
    }

    [Fact]
    public void ResolveClassNumber_IsCaseInsensitive()
    {
        KnownSpellCatalog catalog = NewCatalog();
        Assert.Equal(15, catalog.ResolveClassNumber("mystic"));
        Assert.Equal(12, catalog.ResolveClassNumber("  Mage "));
        Assert.Null(catalog.ResolveClassNumber("Sorcerer"));
    }

    [Fact]
    public void ResolveClassMagery_EmitsTypeAndLevel()
    {
        KnownSpellCatalog catalog = NewCatalog();

        Assert.Equal(1, catalog.ResolveClassMagery(12, out int mageLvl));
        Assert.Equal(3, mageLvl);
        Assert.Equal(0, catalog.ResolveClassMagery(1, out int warriorLvl)); // None
        Assert.Equal(0, warriorLvl);
    }

    [Fact]
    public void KnownSpell_Formula_FeedsCalculator()
    {
        // star: MinBase 5, MaxBase 10, no scaling, no energy multiplier.
        KnownSpell star = NewCatalog().GetByShort("star", 12)!.Value;
        Assert.Equal(5, SpellCalculator.MinDamage(star.Formula, level: 10));
        Assert.Equal(10, SpellCalculator.MaxDamage(star.Formula, level: 10));
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
        int number, string name, string shortCode, int magery, int mageryLvl, int reqLevel,
        int learnable, string classes, int minBase = 0, int maxBase = 0, int abil0 = 0,
        string learnedFrom = "\0")
    {
        Dictionary<string, object> row = new()
        {
            ["Number"] = number,
            ["Name"] = name,
            ["Short"] = shortCode,
            ["Magery"] = magery,
            ["MageryLVL"] = mageryLvl,
            ["ReqLevel"] = reqLevel,
            ["Learnable"] = learnable,
            ["Learned From"] = learnedFrom,
            ["Classes"] = classes,
            ["MinBase"] = minBase,
            ["MaxBase"] = maxBase,
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
