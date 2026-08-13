using System.IO;
using MudPlay.Game.Combat;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins SpellShortIndex — the Spells.Number <-> Short cast-code bridge the
// per-monster override spell slots and the Monster editor's "Override Attack"
// cast-code resolution (report paradigm-20260813-070249) both read.
public sealed class SpellShortIndexTests : IDisposable
{
    private readonly string _root;

    public SpellShortIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-spellshort-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // Two spells share the "turn" cast-code (a real Paradigm shape — several
    // undead-turning spells all answer to "turn"), matching the report.
    private const string SpellsJson = """
        [
          { "Number": 18,   "Name": "turn undead", "Short": "turn" },
          { "Number": 5531, "Name": "turn",         "Short": "turn" },
          { "Number": 8,    "Name": "lightning bolt", "Short": "lbol" },
          { "Number": 50,   "Name": "no short" }
        ]
        """;

    private SpellShortIndex NewIndex(string set = "alpha", string json = SpellsJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, set));
        File.WriteAllText(Path.Combine(_root, set, "Spells.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet(set);
        return new SpellShortIndex(cache);
    }

    [Fact]
    public void ShortByNumber_KnownNumber_ReturnsCode()
    {
        SpellShortIndex s = NewIndex();
        Assert.Equal("turn", s.ShortByNumber(18));
        Assert.Equal("lbol", s.ShortByNumber(8));
    }

    [Fact]
    public void ShortByNumber_UnknownOrNonPositive_ReturnsNull()
    {
        SpellShortIndex s = NewIndex();
        Assert.Null(s.ShortByNumber(999));
        Assert.Null(s.ShortByNumber(0));
        Assert.Null(s.ShortByNumber(-1));
    }

    [Fact]
    public void NumberByShort_KnownCode_ReturnsANumberThatRoundTrips()
    {
        // Two spells share "turn" — whichever Number wins, converting it back
        // via ShortByNumber must land on the SAME cast-code, so which one wins
        // is immaterial to what actually goes out on the wire.
        SpellShortIndex s = NewIndex();
        int? number = s.NumberByShort("turn");

        Assert.NotNull(number);
        Assert.Equal("turn", s.ShortByNumber(number!.Value));
    }

    [Fact]
    public void NumberByShort_IsCaseInsensitiveAndTrims()
    {
        SpellShortIndex s = NewIndex();
        Assert.Equal(s.NumberByShort("turn"), s.NumberByShort("  TURN  "));
    }

    [Fact]
    public void NumberByShort_UnknownCode_ReturnsNull()
    {
        SpellShortIndex s = NewIndex();
        Assert.Null(s.NumberByShort("nope"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NumberByShort_BlankText_ReturnsNull(string? text)
    {
        SpellShortIndex s = NewIndex();
        Assert.Null(s.NumberByShort(text!));
    }
}
