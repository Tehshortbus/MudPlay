using FujinTerm.Game;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 10.8 — <see cref="AutoTrainSequenceBuilder"/> turns a current→target stat
/// delta into the Enter-driven payloads that drive the <c>train stats</c> form:
/// 11 entries (skip Family Name, the six stat boxes, skip the three appearance
/// boxes, save+exit), with target digits only where a stat is actually raised.
/// </summary>
public sealed class AutoTrainSequenceBuilderTests
{
    // order: STR, INT, WIL, AGL, HEA, CHM
    private static readonly int[] Current = { 40, 50, 30, 50, 30, 40 };

    [Fact]
    public void Build_RaisesOneStat_TypesItOnly()
    {
        // Raise STR 40 → 50; everything else unchanged.
        int[] target = { 50, 50, 30, 50, 30, 40 };
        var seq = AutoTrainSequenceBuilder.Build(Current, target);

        Assert.Equal(11, seq.Count);
        Assert.Equal("", seq[0]);     // skip Family Name
        Assert.Equal("50", seq[1]);   // STR set
        Assert.Equal("", seq[2]);     // INT
        Assert.Equal("", seq[3]);     // WIL
        Assert.Equal("", seq[4]);     // AGL
        Assert.Equal("", seq[5]);     // HEA
        Assert.Equal("", seq[6]);     // CHM
        Assert.Equal("", seq[7]);     // Hair Length
        Assert.Equal("", seq[8]);     // Hair Colour
        Assert.Equal("", seq[9]);     // Eye Colour
        Assert.Equal("", seq[10]);    // Exit → SAVE
    }

    [Fact]
    public void Build_RaisesMultipleStats_TypesEach()
    {
        // Raise STR 40→55 and HEA 30→45.
        int[] target = { 55, 50, 30, 50, 45, 40 };
        var seq = AutoTrainSequenceBuilder.Build(Current, target);

        Assert.Equal("55", seq[1]);   // STR
        Assert.Equal("45", seq[5]);   // HEA (5th stat → index 5)
        Assert.Equal("", seq[2]);
        Assert.Equal("", seq[6]);
    }

    [Fact]
    public void Build_NoRaise_AllEnterOnly()
    {
        // Targets equal (or below — can't untrain) current → nothing typed.
        int[] target = { 40, 50, 30, 50, 30, 35 };   // CHM below current
        var seq = AutoTrainSequenceBuilder.Build(Current, target);

        Assert.All(seq, p => Assert.Equal("", p));
        Assert.False(AutoTrainSequenceBuilder.HasRaise(Current, target));
    }

    [Fact]
    public void HasRaise_TrueWhenAnyTargetExceedsCurrent()
    {
        Assert.True(AutoTrainSequenceBuilder.HasRaise(Current, new[] { 40, 50, 31, 50, 30, 40 }));
        Assert.False(AutoTrainSequenceBuilder.HasRaise(Current, Current));
    }
}
