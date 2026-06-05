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

    // ----- door-aware encoding --------------------------------------

    [Fact]
    public void EncodeExits_NormalExit_WeightsOne()
    {
        // N alone, no door — encoding should match the naive form.
        Room room = MakeRoom("Test", new[]
        {
            (Direction.N, RoomExitHint.None),
        });
        string encoded = MegaMudHash.EncodeExits(room);
        Assert.Equal(MegaMudHash.EncodeExits(new HashSet<Direction> { Direction.N }), encoded);
    }

    [Fact]
    public void EncodeExits_DoorExit_DoublesWeightVsNormal()
    {
        // Same exit topology but the N is a Door — encoding MUST differ
        // from the naive "all normal" form.
        Room doorRoom = MakeRoom("Test", new[]
        {
            (Direction.N, RoomExitHint.Door),
        });
        string doorEncoded = MegaMudHash.EncodeExits(doorRoom);
        string naive = MegaMudHash.EncodeExits(new HashSet<Direction> { Direction.N });
        Assert.NotEqual(naive, doorEncoded);
    }

    [Fact]
    public void EncodeExits_HiddenExit_IsExcludedEntirely()
    {
        // A hidden N + a normal S should encode the same as just S.
        Room hiddenN = MakeRoom("Test", new[]
        {
            (Direction.N, RoomExitHint.SearchableHidden),
            (Direction.S, RoomExitHint.None),
        });
        string withHidden = MegaMudHash.EncodeExits(hiddenN);
        string sOnly = MegaMudHash.EncodeExits(new HashSet<Direction> { Direction.S });
        Assert.Equal(sOnly, withHidden);
    }

    [Theory]
    [InlineData(RoomExitHint.Door)]
    [InlineData(RoomExitHint.KeyLocked)]
    [InlineData(RoomExitHint.Toll)]
    public void IsDoorLike_RecognisesDoorishHints(RoomExitHint hint)
        => Assert.True(MegaMudHash.IsDoorLike(hint));

    [Theory]
    [InlineData(RoomExitHint.None)]
    [InlineData(RoomExitHint.Trap)]
    [InlineData(RoomExitHint.Item)]
    [InlineData(RoomExitHint.Text)]
    public void IsDoorLike_NotDoorishHints(RoomExitHint hint)
        => Assert.False(MegaMudHash.IsDoorLike(hint));

    // ----- helper ----------------------------------------------------

    private static Room MakeRoom(string name, IEnumerable<(Direction Dir, RoomExitHint Hint)> exits)
    {
        var dict = new Dictionary<Direction, RoomExit>();
        uint mask = 0;
        foreach ((Direction d, RoomExitHint h) in exits)
        {
            dict[d] = new RoomExit(new RoomKey(99, 1), h, RawHint: null);
            mask |= 1u << (int)d;
        }
        return new Room
        {
            Key = new RoomKey(0, 0),
            Name = name,
            Light = 0, Shop = 0, Spell = 0, Delay = 0, Cmd = 0,
            RawLairTag = null,
            Exits = dict,
            ExitMask = mask,
        };
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
