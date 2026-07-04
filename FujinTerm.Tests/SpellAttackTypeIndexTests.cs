using System.IO;
using FujinTerm.Game.Combat;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

// Pins SpellAttackTypeIndex — the cast-code → AttType (damage element) lookup
// the resist guard reads. Keyed by Short (the cast-code the combat slots store),
// case-insensitive, fail-open (-1) on an unknown or missing cast-code.
public sealed class SpellAttackTypeIndexTests : IDisposable
{
    private readonly string _root;

    public SpellAttackTypeIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-atttype-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // lbol lightning bolt (AttType 3), mmis magic missile (4 Normal), pois a
    // poison spell (6), noatt a row with no AttType column (skipped), and a
    // null-Short row (skipped — no cast-code to key on).
    private const string SpellsJson = """
        [
          { "Number": 8,  "Name": "lightning bolt", "Short": "lbol", "AttType": 3 },
          { "Number": 1,  "Name": "magic missile",  "Short": "mmis", "AttType": 4 },
          { "Number": 90, "Name": "venom",          "Short": "pois", "AttType": 6 },
          { "Number": 50, "Name": "no att type",    "Short": "noatt" },
          { "Number": 60, "Name": "nameless",       "Short": null,   "AttType": 1 }
        ]
        """;

    private SpellAttackTypeIndex NewIndex(string set = "alpha", string json = SpellsJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, set));
        File.WriteAllText(Path.Combine(_root, set, "Spells.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet(set);
        return new SpellAttackTypeIndex(cache);
    }

    [Fact]
    public void AttackType_KnownCastCode_ReturnsAttType()
    {
        SpellAttackTypeIndex s = NewIndex();
        Assert.Equal(3, s.AttackType("lbol"));
        Assert.Equal(4, s.AttackType("mmis"));
        Assert.Equal(6, s.AttackType("pois"));
    }

    [Fact]
    public void AttackType_IsCaseInsensitive()
    {
        SpellAttackTypeIndex s = NewIndex();
        Assert.Equal(3, s.AttackType("LBOL"));
    }

    [Fact]
    public void AttackType_UnknownCastCode_IsMinusOne()
    {
        SpellAttackTypeIndex s = NewIndex();
        Assert.Equal(-1, s.AttackType("nope"));
    }

    [Fact]
    public void AttackType_MissingAttTypeColumn_IsMinusOne()
    {
        // A row without an AttType column is never indexed → fail-open -1.
        SpellAttackTypeIndex s = NewIndex();
        Assert.Equal(-1, s.AttackType("noatt"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AttackType_NullOrBlank_IsMinusOne(string? castCode)
    {
        SpellAttackTypeIndex s = NewIndex();
        Assert.Equal(-1, s.AttackType(castCode));
    }
}
