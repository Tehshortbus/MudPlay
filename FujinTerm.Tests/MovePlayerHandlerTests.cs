using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pure-logic coverage of the static parsers exposed on
/// <see cref="RoomSearchService"/> — used by the @goto / @loop /
/// @lair handlers + the Navigation rail search box. The integration
/// tier (the full @goto dispatch) lands in a separate suite once
/// the engine seam is mocked.
/// </summary>
public sealed class MovePlayerHandlerTests
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
}
