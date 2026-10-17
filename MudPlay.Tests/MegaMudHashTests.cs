using System.Collections.Generic;
using MudPlay.Game.Map;
using MudPlay.Game.Map.MpFile;
using Xunit;

namespace MudPlay.Tests;

public sealed class MegaMudHashTests
{
    // ----- name hash -------------------------------------------------

    [Theory]
    [InlineData("", "FFF")]      // empty → JS sentinel
    [InlineData(null, "FFF")]    // null → JS sentinel
    public void ComputeNameHash_EmptyOrNull_ReturnsFFF(string? name, string expected)
        => Assert.Equal(expected, MegaMudHash.ComputeNameHash(name));

    // sum(position × char code), low 12 bits as three hex digits: "Stone Street"
    // sums to 7833 = 0x1E99.
    [Theory]
    [InlineData("Stone Street", "E99")]
    [InlineData("Start", "66A")]
    [InlineData("Town Square", "9D4")]
    public void ComputeNameHash_IsTheLow12BitsOfThePositionWeightedSum(string name, string expected)
        => Assert.Equal(expected, MegaMudHash.ComputeNameHash(name));

    // ----- exits code ------------------------------------------------

    // Each direction lands in its own digit, N/S rightmost and U/D leftmost, the
    // second of each pair weighing 4 to the first's 1.
    [Theory]
    [InlineData(Direction.N, "00001")]
    [InlineData(Direction.S, "00004")]
    [InlineData(Direction.E, "00010")]
    [InlineData(Direction.W, "00040")]
    [InlineData(Direction.NE, "00100")]
    [InlineData(Direction.NW, "00400")]
    [InlineData(Direction.SE, "01000")]
    [InlineData(Direction.SW, "04000")]
    [InlineData(Direction.U, "10000")]
    [InlineData(Direction.D, "40000")]
    public void EncodeExits_EachDirection_HasItsOwnDigit(Direction dir, string expected)
        => Assert.Equal(expected, MegaMudHash.EncodeExits(MakeRoom("Test", new[] { (dir, RoomExitHint.None) })));

    [Fact]
    public void EncodeExits_EveryExit_FillsEveryDigit()
    {
        Room room = MakeRoom("Test", new[]
        {
            (Direction.N, RoomExitHint.None), (Direction.S, RoomExitHint.None),
            (Direction.E, RoomExitHint.None), (Direction.W, RoomExitHint.None),
            (Direction.NE, RoomExitHint.None), (Direction.NW, RoomExitHint.None),
            (Direction.SE, RoomExitHint.None), (Direction.SW, RoomExitHint.None),
            (Direction.U, RoomExitHint.None), (Direction.D, RoomExitHint.None),
        });
        Assert.Equal("55555", MegaMudHash.EncodeExits(room));
    }

    // ----- door-aware encoding --------------------------------------

    [Fact]
    public void EncodeExits_NormalExit_WeightsOne()
    {
        Room room = MakeRoom("Test", new[]
        {
            (Direction.N, RoomExitHint.None),
        });
        Assert.Equal("00001", MegaMudHash.EncodeExits(room));
    }

    [Fact]
    public void EncodeExits_DoorExit_DoublesWeightVsNormal()
    {
        // Same exit topology but the N is a Door: its weight doubles.
        Room doorRoom = MakeRoom("Test", new[]
        {
            (Direction.N, RoomExitHint.Door),
        });
        Assert.Equal("00002", MegaMudHash.EncodeExits(doorRoom));
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
        Assert.Equal("00004", MegaMudHash.EncodeExits(hiddenN));
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
