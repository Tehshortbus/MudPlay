using MudPlay.Models.GameData;
using Xunit;

namespace MudPlay.Tests;

// The {null}/{void}/{empty} "no such line" sentinels: recognized case-insensitively and
// whitespace-tolerantly, treated as absent for recognition (IsBlankOrAbsent) yet distinct
// from a real blank so the Incomplete Messages worklist can count them as filled.
public sealed class MessageRecordSentinelTests
{
    [Theory]
    [InlineData("{null}")]
    [InlineData("{void}")]
    [InlineData("{empty}")]
    [InlineData("{NULL}")]
    [InlineData("  {Void}  ")]
    public void IsAbsentSentinel_True(string s) =>
        Assert.True(MessageRecord.IsAbsentSentinel(s));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("You fumble in confusion!")]
    [InlineData("{nullish}")]
    [InlineData("null")]
    [InlineData("void message")]
    public void IsAbsentSentinel_False(string? s) =>
        Assert.False(MessageRecord.IsAbsentSentinel(s));

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("{void}", true)]
    [InlineData("{empty}", true)]
    [InlineData("You cast bless!", false)]
    public void IsBlankOrAbsent(string s, bool expected) =>
        Assert.Equal(expected, MessageRecord.IsBlankOrAbsent(s));
}
