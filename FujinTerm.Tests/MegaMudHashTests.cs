using System.Collections.Generic;
using FujinTerm.Game.Map;
using FujinTerm.Game.Map.MpFile;
using Xunit;

namespace FujinTerm.Tests;

public sealed class MegaMudHashTests
{
    // ----- name hash -------------------------------------------------

    [Theory]
    [InlineData("", "FFF")]      // empty → JS sentinel
    [InlineData(null, "FFF")]    // null → JS sentinel
    public void ComputeNameHash_EmptyOrNull_ReturnsFFF(string? name, string expected)
        => Assert.Equal(expected, MegaMudHash.ComputeNameHash(name));

    [Fact]
    public void ComputeNameHash_Deterministic_SameInputSameHash()
    {
        // Anchor: the JS reference algorithm is sum(pos * charCode) → low 12 bits → 3-hex.
        // Compute the expected by mirroring the JS:
        string name = "Stone Street";
        int n = 0;
        for (int i = 0; i < name.Length; i++) n = unchecked(n + (i + 1) * name[i]);
        uint u = unchecked((uint)n);
        string hex = u.ToString("X");
        string expected = (hex.Length >= 3 ? hex[^3..] : hex.PadLeft(3, '0')).ToUpperInvariant();

        Assert.Equal(expected, MegaMudHash.ComputeNameHash(name));
    }

    // ----- exits encode/decode roundtrip -----------------------------

    public static IEnumerable<object[]> ExitRoundtripCases()
    {
        yield return new object[] { new HashSet<Direction>() };
        yield return new object[] { new HashSet<Direction> { Direction.N } };
        yield return new object[] { new HashSet<Direction> { Direction.N, Direction.S } };
        yield return new object[] { new HashSet<Direction> { Direction.E, Direction.W } };
        yield return new object[] { new HashSet<Direction> { Direction.NE, Direction.NW, Direction.SE, Direction.SW } };
        yield return new object[] { new HashSet<Direction> { Direction.U, Direction.D } };
        yield return new object[] { new HashSet<Direction>
            { Direction.N, Direction.S, Direction.E, Direction.W, Direction.NE, Direction.NW, Direction.SE, Direction.SW, Direction.U, Direction.D } };
        yield return new object[] { new HashSet<Direction> { Direction.S, Direction.NW } };
    }

    [Theory]
    [MemberData(nameof(ExitRoundtripCases))]
    public void EncodeDecode_Roundtrip(HashSet<Direction> exits)
    {
        string code = MegaMudHash.EncodeExits(exits);
        Assert.Equal(5, code.Length);
        IReadOnlySet<Direction>? decoded = MegaMudHash.DecodeExits(code);
        Assert.NotNull(decoded);
        Assert.Equal((IReadOnlySet<Direction>)exits, decoded);
    }

    // ----- split helper ---------------------------------------------

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    [InlineData("abc", null, null)]                          // wrong length
    [InlineData("70C00504X", null, null)]                    // non-hex tail
    [InlineData("70C00504", "70C", "00504")]                 // happy path
    [InlineData("70c00504", "70C", "00504")]                 // lower-cased → upper out
    public void Split_KnownShapes(string? input, string? expectHash, string? expectExits)
    {
        (string? h, string? e) = MegaMudHash.Split(input);
        Assert.Equal(expectHash, h);
        Assert.Equal(expectExits, e);
    }
}
