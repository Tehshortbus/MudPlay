using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FujinTerm.Game.Calculators;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="ClassCapabilities"/> resolves smash / stealth capability straight
/// from game-data tables. Smash scans <c>TBInfo</c> <c>Action</c> chains for
/// a <c>giveability 32 1</c> step paired with a <c>class N</c> restriction in the
/// same chain; stealth is a direct <c>Abil-0..9</c> scan (race 102, class 103).
/// These tests seed an isolated set and assert each lookup.
/// </summary>
public sealed class ClassCapabilitiesTests : IDisposable
{
    private readonly string _root;

    public ClassCapabilitiesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-classcap-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // Seeds named tables (each an array of row dictionaries) into a set and activates it.
    private GameDataCache CacheWith(params (string Table, Dictionary<string, object>[] Rows)[] tables)
    {
        string dir = Path.Combine(_root, "test-set");
        Directory.CreateDirectory(dir);
        foreach ((string table, Dictionary<string, object>[] rows) in tables)
            File.WriteAllText(Path.Combine(dir, table + ".json"), JsonSerializer.Serialize(rows));
        GameDataCache cache = new(_root);
        cache.SwitchSet("test-set");
        return cache;
    }

    private static Dictionary<string, object> Row(params (string Key, object Value)[] fields)
    {
        Dictionary<string, object> d = new();
        foreach ((string key, object value) in fields) d[key] = value;
        return d;
    }

    [Fact]
    public void Smash_ClassRestrictedChain_ReturnsThoseClasses()
    {
        GameDataCache cache = CacheWith(
            ("Classes", new[]
            {
                Row(("Number", 1), ("Name", "Warrior")),
                Row(("Number", 2), ("Name", "Mage")),
            }),
            ("TBInfo", new[]
            {
                Row(("Action", "class 1:giveability 32 1")),
            }));

        HashSet<string>? smash = ClassCapabilities.GetSmashCapableClasses(cache);

        Assert.NotNull(smash);
        Assert.Contains("Warrior", smash);
        Assert.DoesNotContain("Mage", smash);
    }

    [Fact]
    public void Smash_NoClassRestrictedChain_ReturnsNull()
    {
        // A giveability chain with no class restriction is a gated reward block,
        // not "everyone can smash" — null means "assume every class can".
        GameDataCache cache = CacheWith(
            ("Classes", new[] { Row(("Number", 1), ("Name", "Warrior")) }),
            ("TBInfo", new[] { Row(("Action", "giveability 32 1")) }));

        Assert.Null(ClassCapabilities.GetSmashCapableClasses(cache));
    }

    [Fact]
    public void Smash_DifferentAbilityChain_Ignored()
    {
        // giveability for a non-smash ability (id != 32) must not register.
        GameDataCache cache = CacheWith(
            ("Classes", new[] { Row(("Number", 1), ("Name", "Warrior")) }),
            ("TBInfo", new[] { Row(("Action", "class 1:giveability 40 1")) }));

        Assert.Null(ClassCapabilities.GetSmashCapableClasses(cache));
    }

    [Fact]
    public void ClassHasStealth_DetectsAbility103()
    {
        GameDataCache cache = CacheWith(
            ("Classes", new[]
            {
                Row(("Number", 1), ("Name", "Thief"), ("Abil-0", 103)),
                Row(("Number", 2), ("Name", "Mage")),
            }));

        Assert.True(ClassCapabilities.ClassHasStealth(cache.FindRowByName("Classes", "Thief")));
        Assert.False(ClassCapabilities.ClassHasStealth(cache.FindRowByName("Classes", "Mage")));
    }

    [Fact]
    public void RaceHasStealth_DetectsAbility102()
    {
        GameDataCache cache = CacheWith(
            ("Races", new[]
            {
                Row(("Number", 1), ("Name", "Halfling"), ("Abil-3", 102)),
                Row(("Number", 2), ("Name", "Human")),
            }));

        Assert.True(ClassCapabilities.RaceHasStealth(cache.FindRowByName("Races", "Halfling")));
        Assert.False(ClassCapabilities.RaceHasStealth(cache.FindRowByName("Races", "Human")));
    }

    [Fact]
    public void Stealth_NullRow_IsFalse()
    {
        Assert.False(ClassCapabilities.ClassHasStealth(null));
        Assert.False(ClassCapabilities.RaceHasStealth(null));
    }

    [Fact]
    public void MartialArtsStrikes_DetectsAbilities29_30_35()
    {
        // Mystic carries Punch (29), Kick (30), Jumpkick (35) in its Abil slots;
        // a non-Mystic class carries none, so its strike rows stay hidden even if
        // the character trained the Martial Arts skill via items/races.
        GameDataCache cache = CacheWith(
            ("Classes", new[]
            {
                Row(("Number", 15), ("Name", "Mystic"), ("Abil-2", 29), ("Abil-3", 30), ("Abil-4", 35)),
                Row(("Number", 1), ("Name", "Warrior")),
            }));

        JsonElement? mystic = cache.FindRowByName("Classes", "Mystic");
        Assert.True(ClassCapabilities.ClassHasPunch(mystic));
        Assert.True(ClassCapabilities.ClassHasKick(mystic));
        Assert.True(ClassCapabilities.ClassHasJumpKick(mystic));

        JsonElement? warrior = cache.FindRowByName("Classes", "Warrior");
        Assert.False(ClassCapabilities.ClassHasPunch(warrior));
        Assert.False(ClassCapabilities.ClassHasKick(warrior));
        Assert.False(ClassCapabilities.ClassHasJumpKick(warrior));
    }

    [Fact]
    public void MartialArtsStrikes_NullRow_IsFalse()
    {
        Assert.False(ClassCapabilities.ClassHasPunch(null));
        Assert.False(ClassCapabilities.ClassHasKick(null));
        Assert.False(ClassCapabilities.ClassHasJumpKick(null));
    }
}
