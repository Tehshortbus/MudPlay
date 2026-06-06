using FujinTerm.Game.Map;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 7.18 — LairTagParser unit coverage for both MDB shapes:
/// the NMR 1.83+ <c>[GROUP-INDEX][MAX-REGEN]Group(lair): MAP/ROOM</c>
/// tag and the pre-1.83 comma-separated monster-id list.
/// </summary>
public sealed class LairTagParserTests
{
    // ----- NMR 1.83+ shape -----------------------------------------

    [Fact]
    public void TryParse_GroupTag_ExtractsAllFields()
    {
        LairTagInfo? info = LairTagParser.TryParse(
            "[120-005-001][2]Group(lair): 5/417");

        Assert.NotNull(info);
        Assert.Equal("120-005-001", info!.GroupIndex);
        Assert.Equal(2, info.MaxRegen);
        Assert.Equal(new RoomKey(5, 417), info.ReferenceRoom);
        Assert.Empty(info.MonsterIds);
    }

    [Fact]
    public void TryParse_GroupTag_AcceptsLeadingAndTrailingWhitespace()
    {
        LairTagInfo? info = LairTagParser.TryParse(
            "   [01-02-03][1]Group(lair): 12/34   ");

        Assert.NotNull(info);
        Assert.Equal("01-02-03", info!.GroupIndex);
        Assert.Equal(new RoomKey(12, 34), info.ReferenceRoom);
    }

    [Fact]
    public void TryParse_GroupTag_PlainNumericGroupIndex()
    {
        // Some realms tag with a plain integer key — still legal under
        // the [GROUP-INDEX] section because the regex accepts [\d-]+.
        LairTagInfo? info = LairTagParser.TryParse(
            "[42][3]Group(lair): 1/2");

        Assert.NotNull(info);
        Assert.Equal("42", info!.GroupIndex);
        Assert.Equal(3, info.MaxRegen);
        Assert.Equal(new RoomKey(1, 2), info.ReferenceRoom);
    }

    // ----- Pre-1.83 shape ------------------------------------------

    [Fact]
    public void TryParse_MonsterList_ExtractsIdsSortedByInputOrder()
    {
        LairTagInfo? info = LairTagParser.TryParse("12, 34, 56");

        Assert.NotNull(info);
        Assert.Null(info!.GroupIndex);
        Assert.Equal(0, info.MaxRegen);
        Assert.Null(info.ReferenceRoom);
        Assert.Equal(new[] { 12, 34, 56 }, info.MonsterIds);
    }

    [Fact]
    public void TryParse_MonsterList_SingleId()
    {
        LairTagInfo? info = LairTagParser.TryParse("99");
        Assert.NotNull(info);
        Assert.Equal(new[] { 99 }, info!.MonsterIds);
    }

    [Fact]
    public void TryParse_MonsterList_TrimsWhitespaceAroundCommas()
    {
        LairTagInfo? info = LairTagParser.TryParse("  1 ,  2  ,3 ");
        Assert.NotNull(info);
        Assert.Equal(new[] { 1, 2, 3 }, info!.MonsterIds);
    }

    // ----- Null / empty / garbage -----------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    public void TryParse_NullOrWhitespace_ReturnsNull(string? input)
    {
        Assert.Null(LairTagParser.TryParse(input));
    }

    [Fact]
    public void TryParse_Garbage_ReturnsNull()
    {
        Assert.Null(LairTagParser.TryParse("not a tag"));
    }

    [Fact]
    public void TryParse_TagWithoutGroupSection_ReturnsNull()
    {
        // Missing the [GROUP-INDEX] section entirely — server-side
        // bug or partial export. Don't guess; return null.
        Assert.Null(LairTagParser.TryParse("Group(lair): 5/417"));
    }
}
