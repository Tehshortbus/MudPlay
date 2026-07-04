using System.IO;
using FujinTerm.Game.Combat;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

// Pins MonsterResistIndex — the elemental damage-type resist lookup that feeds
// the pre-emptive resist guard. Two concerns: the static AttType→resist-code
// map (ElementalResistCode) and the signed per-monster resist read (only the
// five elemental Abil codes count; the value passes through signed; 0 is
// dropped; non-elemental abilities are ignored).
public sealed class MonsterResistIndexTests : IDisposable
{
    private readonly string _root;

    public MonsterResistIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-resist-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // #11 skeleton — Resist-Cold (3) 100% and a NonLiving (109) marker that must
    // be ignored (not an elemental resist). #184 dragon — Resist-Fire (5) 50%
    // (partial, not a skip). #99 chill wisp — Resist-Cold (3) -50 (vulnerability,
    // a negative value that must pass through). #7 rat — a 0-value Resist-Water
    // slot that must be dropped. #8 slug — no resist abilities at all.
    private const string MonstersJson = """
        [
          { "Number": 11,  "Name": "skeleton",   "Abil-0": 109, "AbilVal-0": 0,   "Abil-1": 3,   "AbilVal-1": 100 },
          { "Number": 184, "Name": "dragon",     "Abil-0": 5,   "AbilVal-0": 50 },
          { "Number": 99,  "Name": "chill wisp", "Abil-0": 3,   "AbilVal-0": -50 },
          { "Number": 7,   "Name": "rat",        "Abil-0": 147, "AbilVal-0": 0 },
          { "Number": 8,   "Name": "slug" }
        ]
        """;

    private MonsterResistIndex NewIndex(string set = "alpha", string json = MonstersJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, set));
        File.WriteAllText(Path.Combine(_root, set, "Monsters.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet(set);
        return new MonsterResistIndex(cache);
    }

    [Theory]
    [InlineData(0, 3)]    // Cold
    [InlineData(1, 5)]    // Fire
    [InlineData(2, 65)]   // Stone
    [InlineData(3, 66)]   // Lightning
    [InlineData(5, 147)]  // Water
    public void ElementalResistCode_MapsEachElementalAttackType(int attackType, int code)
    {
        Assert.Equal(code, MonsterResistIndex.ElementalResistCode(attackType));
    }

    [Theory]
    [InlineData(4)]   // Normal — Magic Resist, not deterministic
    [InlineData(6)]   // Poison — not resistible
    [InlineData(7)]   // unknown / out of range
    [InlineData(-1)]  // unknown cast-code sentinel
    public void ElementalResistCode_NonElementalAttackType_IsMinusOne(int attackType)
    {
        Assert.Equal(-1, MonsterResistIndex.ElementalResistCode(attackType));
    }

    [Fact]
    public void ResistPercent_ElementalResist_ReturnsValue()
    {
        MonsterResistIndex s = NewIndex();
        Assert.Equal(100, s.ResistPercent(11, 3));    // skeleton Resist-Cold 100
        Assert.Equal(50, s.ResistPercent(184, 5));    // dragon Resist-Fire 50
    }

    [Fact]
    public void ResistPercent_NegativeValue_PassesThroughSigned()
    {
        // A negative resist is a vulnerability (extra damage) — the raw signed
        // value must survive so the guard leaves the spell eligible.
        MonsterResistIndex s = NewIndex();
        Assert.Equal(-50, s.ResistPercent(99, 3));
    }

    [Fact]
    public void ResistPercent_NonElementalAbility_NotIndexed()
    {
        // The skeleton's NonLiving (109) marker is not an elemental resist and
        // must never surface as a resist value.
        MonsterResistIndex s = NewIndex();
        Assert.Equal(0, s.ResistPercent(11, 109));
    }

    [Fact]
    public void ResistPercent_ZeroValueSlot_Dropped()
    {
        // A Resist-Water slot valued 0 is no resistance — it must read back 0
        // (the absent default), same as a monster that never had the ability.
        MonsterResistIndex s = NewIndex();
        Assert.Equal(0, s.ResistPercent(7, 147));
    }

    [Fact]
    public void ResistPercent_MonsterWithoutResists_IsZero()
    {
        MonsterResistIndex s = NewIndex();
        Assert.Equal(0, s.ResistPercent(8, 3));
    }

    [Fact]
    public void ResistPercent_UnknownMonster_IsZero()
    {
        MonsterResistIndex s = NewIndex();
        Assert.Equal(0, s.ResistPercent(99999, 3));
    }

    [Fact]
    public void ResistPercent_WrongElement_IsZero()
    {
        // The skeleton resists cold, not fire.
        MonsterResistIndex s = NewIndex();
        Assert.Equal(0, s.ResistPercent(11, 5));
    }
}
