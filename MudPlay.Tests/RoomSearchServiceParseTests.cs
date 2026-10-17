using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The static parsers on RoomSearchService that the @goto / @loop / @lair
// handlers and the Navigation rail search box share.
public sealed class RoomSearchServiceParseTests
{
    // ----- ExtractAcronym ------------------------------------------

    [Theory]
    [InlineData("Frozen Cavern, Cave Opening", "FCCO")]
    [InlineData("Bank of Godfrey",              "BOG")]
    [InlineData("Small Alleyway",               "SA")]
    [InlineData("intersection of stone st. and main",
                "IOSSAM")]
    [InlineData("Single",                       "S")]
    [InlineData("",                             "")]
    [InlineData("   ",                          "")]
    [InlineData("123 numerics ignored",         "NI")]
    public void ExtractAcronym_BuildsFromFirstLetters(string input, string expected)
        => Assert.Equal(expected, RoomSearchService.ExtractAcronym(input));

    // ----- TryParseCoordinate --------------------------------------

    [Theory]
    [InlineData("1/297",   1, 297)]
    [InlineData("1,297",   1, 297)]
    [InlineData("1 297",   1, 297)]
    [InlineData("12/3456", 12, 3456)]
    public void TryParseCoordinate_AcceptsTwoPartSeparators(string s, int map, int room)
    {
        (int? m, int? r) = RoomSearchService.TryParseCoordinate(s);
        Assert.Equal(map, m);
        Assert.Equal(room, r);
    }

    [Fact]
    public void TryParseCoordinate_BareNumber_RoomOnly()
    {
        (int? m, int? r) = RoomSearchService.TryParseCoordinate("297");
        Assert.Null(m);
        Assert.Equal(297, r);
    }

    [Theory]
    [InlineData("not numeric")]
    [InlineData("")]
    [InlineData("1/2/3")]
    public void TryParseCoordinate_Garbage_ReturnsNullNull(string s)
    {
        (int? m, int? r) = RoomSearchService.TryParseCoordinate(s);
        Assert.Null(m);
        Assert.Null(r);
    }

    // ----- TryParseCoordList ----------------------------------------

    [Fact]
    public void TryParseCoordList_ValidList_ParsesAll()
    {
        List<RoomKey>? keys = RoomSearchService.TryParseCoordList("1/224, 1/218, 1/245, 1/324");
        Assert.NotNull(keys);
        Assert.Equal(4, keys!.Count);
        Assert.Equal(new RoomKey(1, 224), keys[0]);
        Assert.Equal(new RoomKey(1, 245), keys[2]);
    }

    [Fact]
    public void TryParseCoordList_SemicolonSeparator_AlsoWorks()
    {
        List<RoomKey>? keys = RoomSearchService.TryParseCoordList("1/1; 2/2");
        Assert.NotNull(keys);
        Assert.Equal(2, keys!.Count);
    }

    [Theory]
    [InlineData("loop name with words")]
    [InlineData("1/1, not a coord, 1/3")]
    [InlineData("")]
    public void TryParseCoordList_AnyBadToken_ReturnsNull(string s)
    {
        Assert.Null(RoomSearchService.TryParseCoordList(s));
    }

    [Fact]
    public void TryParseCoordList_BareRoomNumbers_ReturnsNull()
    {
        // Bare "1, 2, 3" with no map/room split is ambiguous — the
        // handler treats it as a non-coord list so the caller falls
        // through to name match.
        Assert.Null(RoomSearchService.TryParseCoordList("1, 2, 3"));
    }

    // ----- NameMatchesTokens (@loop / @goto fuzzy resolution) -------
    // Shared 1-of-1 matcher: every typed word must appear in the candidate name
    // (case-insensitive, order-independent). Used to single out a saved loop, a
    // GOTO favourite label, or a boss name.

    [Theory]
    // The motivating case: "godfrey bank" resolves to "Bank of Godfrey Loop".
    [InlineData("Bank of Godfrey Loop", "godfrey bank", true)]
    [InlineData("Bank of Godfrey Loop", "bank godfrey", true)]   // order-independent
    [InlineData("Bank of Godfrey Loop", "GODFREY", true)]        // case-insensitive, single word
    [InlineData("Bank of Godfrey Loop", "god bank", true)]       // partial-word substrings count
    [InlineData("Bank of Godfrey Loop", "godfrey, bank", true)]  // separators in the needle are tokenized
    [InlineData("Bank of Silvermere",   "godfrey bank", false)]  // missing a token → no match
    [InlineData("Bank of Godfrey Loop", "godfrey castle", false)]
    public void NameMatchesTokens_TokenSubset(string name, string needle, bool expected)
        => Assert.Equal(expected, RoomSearchService.NameMatchesTokens(name, needle));

    [Theory]
    [InlineData(null, "bank")]
    [InlineData("Bank of Godfrey Loop", null)]
    [InlineData("Bank of Godfrey Loop", "")]
    [InlineData("Bank of Godfrey Loop", "   ")]
    [InlineData("", "bank")]
    public void NameMatchesTokens_BlankInputs_NeverMatch(string? name, string? needle)
        => Assert.False(RoomSearchService.NameMatchesTokens(name, needle));
}
