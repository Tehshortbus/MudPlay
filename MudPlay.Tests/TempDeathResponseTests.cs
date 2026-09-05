using MudPlay.Game.Combat;
using Xunit;

namespace MudPlay.Tests;

public sealed class TempDeathResponseTests
{
    [Theory]
    [InlineData("lich temp", true)]
    [InlineData("necromancer temp", true)]
    [InlineData("temp", true)]
    [InlineData("Dark Temp", true)]            // case-insensitive
    [InlineData("acid tempest", false)]        // whole-word: 'tempest' is not 'temp'
    [InlineData("attempt", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTempSpell_MatchesWholeWordOnly(string? name, bool expected)
        => Assert.Equal(expected, TempDeathResponse.IsTempSpell(name));

    [Fact]
    public void ExpandToWireBytes_TwoCarriageReturns()
    {
        byte[]? b = TempDeathResponse.ExpandToWireBytes("^M^M");
        Assert.NotNull(b);
        Assert.Equal(new byte[] { (byte)'\r', (byte)'\r' }, b);   // exactly two CRs
    }

    [Fact]
    public void ExpandToWireBytes_MixedTextAndCr()
    {
        byte[]? b = TempDeathResponse.ExpandToWireBytes("look^M");
        Assert.Equal(System.Text.Encoding.Latin1.GetBytes("look\r"), b);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void ExpandToWireBytes_EmptyIsNull(string? s)
        => Assert.Null(TempDeathResponse.ExpandToWireBytes(s));
}
