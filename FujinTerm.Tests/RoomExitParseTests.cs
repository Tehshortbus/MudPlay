using FujinTerm.Game.Map;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Parser coverage for every modifier shape the MajorMUD MDB emits on
/// an exit cell. Sources cross-referenced against real Rooms.json
/// rows and MudProxy's RoomGraphManager.cs parser.
/// </summary>
public sealed class RoomExitParseTests
{
    // ----- Plain (no modifier) --------------------------------------

    [Fact]
    public void Plain_KeyOnly_ParsesNoneHint()
    {
        Assert.True(RoomExit.TryParseWire("1/2", out RoomExit exit));
        Assert.Equal(new RoomKey(1, 2), exit.Target);
        Assert.Equal(RoomExitHint.None, exit.Hint);
        Assert.Null(exit.RawHint);
        Assert.Equal(0, exit.StatRequirement);
        Assert.True(exit.CanBash);                     // default
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0")]
    public void Sentinel_ReturnsFalse(string? wire)
    {
        Assert.False(RoomExit.TryParseWire(wire, out _));
    }

    // ----- Door (no key) --------------------------------------------

    [Fact]
    public void Door_PlainNoRequirement_BashableAndPickable()
    {
        Assert.True(RoomExit.TryParseWire("1/2666 (Door)", out RoomExit exit));
        Assert.Equal(RoomExitHint.Door, exit.Hint);
        Assert.Equal(0, exit.StatRequirement);
        Assert.True(exit.CanBash);
    }

    [Fact]
    public void Door_PicklocksAndStrength_BashableWithReq()
    {
        Assert.True(RoomExit.TryParseWire("12/51 (Door [any picklocks/strength])",
            out RoomExit exit));
        Assert.Equal(RoomExitHint.Door, exit.Hint);
        Assert.True(exit.CanBash);
        // "any" → no numeric requirement; stat-req stays 0
        Assert.Equal(0, exit.StatRequirement);
    }

    [Fact]
    public void Door_PicklocksAndStrength_WithNumericReq()
    {
        Assert.True(RoomExit.TryParseWire("9/177 (Door [11 picklocks/strength])",
            out RoomExit exit));
        Assert.Equal(RoomExitHint.Door, exit.Hint);
        Assert.Equal(11, exit.StatRequirement);
        Assert.True(exit.CanBash);
    }

    [Fact]
    public void Door_PicklocksOnly_NotBashable()
    {
        // Picklocks-only modifier with no /strength suffix — bash impossible.
        Assert.True(RoomExit.TryParseWire("3/14 (Door [50 picklocks])",
            out RoomExit exit));
        Assert.Equal(RoomExitHint.Door, exit.Hint);
        Assert.Equal(50, exit.StatRequirement);
        Assert.False(exit.CanBash);
    }

    [Fact]
    public void Door_ImpossiblyHighReq_ParsesNormally()
    {
        // The 1000-picklocks/strength shape exists on intentionally
        // unbeatable doors — parser doesn't gate, just records.
        Assert.True(RoomExit.TryParseWire("12/2252 (Door [1000 picklocks/strength])",
            out RoomExit exit));
        Assert.Equal(RoomExitHint.Door, exit.Hint);
        Assert.Equal(1000, exit.StatRequirement);
        Assert.True(exit.CanBash);
    }

    // ----- Keyed doors ----------------------------------------------

    [Fact]
    public void Key_Plain_ParsesItemId()
    {
        Assert.True(RoomExit.TryParseWire("1/1224 (Key: 172)", out RoomExit exit));
        Assert.Equal(RoomExitHint.KeyLocked, exit.Hint);
        Assert.Equal(172, exit.KeyItemId);
        Assert.Equal(0, exit.StatRequirement);
    }

    [Fact]
    public void Key_WithPicklockAlternative_PickOnly()
    {
        Assert.True(RoomExit.TryParseWire("1/1224 (Key: 172 [or 100 picklocks])",
            out RoomExit exit));
        Assert.Equal(RoomExitHint.KeyLocked, exit.Hint);
        Assert.Equal(172, exit.KeyItemId);
        Assert.Equal(100, exit.StatRequirement);
        Assert.False(exit.CanBash);                    // picklocks alone
    }

    [Fact]
    public void Key_WithPicklockStrengthAlternative_Bashable()
    {
        Assert.True(RoomExit.TryParseWire("12/2252 (Key: 1980 [or 1000 picklocks/strength])",
            out RoomExit exit));
        Assert.Equal(RoomExitHint.KeyLocked, exit.Hint);
        Assert.Equal(1980, exit.KeyItemId);
        Assert.Equal(1000, exit.StatRequirement);
        Assert.True(exit.CanBash);
    }

    // ----- Text exits ------------------------------------------------

    [Fact]
    public void Text_MultipleAlternatives_ParsesCommandList()
    {
        Assert.True(RoomExit.TryParseWire("1/2335 (Text: borrow skiff, go skiff, row skiff)",
            out RoomExit exit));
        Assert.Equal(RoomExitHint.Text, exit.Hint);
        Assert.NotNull(exit.TextCommands);
        Assert.Equal(3, exit.TextCommands!.Count);
        Assert.Equal("borrow skiff", exit.TextCommands[0]);
        Assert.Equal("go skiff",     exit.TextCommands[1]);
        Assert.Equal("row skiff",    exit.TextCommands[2]);
    }

    [Fact]
    public void Text_SingleCommand_StillALeavesList()
    {
        Assert.True(RoomExit.TryParseWire("2/765 (Text: go path)", out RoomExit exit));
        Assert.Equal(RoomExitHint.Text, exit.Hint);
        Assert.NotNull(exit.TextCommands);
        Assert.Single(exit.TextCommands!);
        Assert.Equal("go path", exit.TextCommands![0]);
    }

    // ----- Item / Ticket / Toll --------------------------------------

    [Fact]
    public void Item_NoCmdContext_ClassifiesAsItem()
    {
        // The parser doesn't know about source-room CMD; Item stays
        // Item here. Promotion to Teleport happens in
        // RoomGraphManager after Room.Cmd is read.
        Assert.True(RoomExit.TryParseWire("7/131 (Item: 474)", out RoomExit exit));
        Assert.Equal(RoomExitHint.Item, exit.Hint);
        Assert.Equal(474, exit.KeyItemId);
    }

    [Fact]
    public void Ticket_ParsesItemId()
    {
        Assert.True(RoomExit.TryParseWire("5/100 (Ticket: 555)", out RoomExit exit));
        Assert.Equal(RoomExitHint.Ticket, exit.Hint);
        Assert.Equal(555, exit.KeyItemId);
    }

    [Theory]
    [InlineData("1/1382 (Toll: 5)",      5)]
    [InlineData("17/3254 (Toll: 10000)", 10000)]
    [InlineData("17/2655 (Toll: 0)",     0)]
    public void Toll_ParsesGoldCost(string wire, int expected)
    {
        Assert.True(RoomExit.TryParseWire(wire, out RoomExit exit));
        Assert.Equal(RoomExitHint.Toll, exit.Hint);
        Assert.Equal(expected, exit.TollGold);
    }

    // ----- Hidden variants -------------------------------------------

    [Fact]
    public void Hidden_Plain_ClassifiesAsSearchableHidden()
    {
        Assert.True(RoomExit.TryParseWire("5/55 (Hidden)", out RoomExit exit));
        Assert.Equal(RoomExitHint.SearchableHidden, exit.Hint);
    }

    [Theory]
    [InlineData("5/55 (Hidden/Passable)")]
    [InlineData("5/55 (Hidden/Passage)")]
    public void Hidden_PassableVariant_TreatedAsNone(string wire)
    {
        Assert.True(RoomExit.TryParseWire(wire, out RoomExit exit));
        // Walker traverses these with a plain cardinal — no search needed.
        Assert.Equal(RoomExitHint.None, exit.Hint);
    }

    [Fact]
    public void Hidden_NeedsActions_ClassifiesAsMultiAction()
    {
        Assert.True(RoomExit.TryParseWire("5/55 (Hidden, Needs 2 Actions, any order)",
            out RoomExit exit));
        Assert.Equal(RoomExitHint.MultiActionHidden, exit.Hint);
    }

    // ----- Trap (existing behaviour preserved) -----------------------

    [Theory]
    [InlineData("5/55 (Trap, 30 damage)")]
    [InlineData("5/55 (Trap)")]
    [InlineData("5/55 (Spell Trap: 905)")]
    public void Trap_VariantsAllClassifyAsTrap(string wire)
    {
        Assert.True(RoomExit.TryParseWire(wire, out RoomExit exit));
        Assert.Equal(RoomExitHint.Trap, exit.Hint);
    }

    // ----- Restrictions (deferred — stay None + RawHint --------------

    [Theory]
    [InlineData("5/55 (Level: 10-20)")]   // hyphen form — not the live "to" encoding, stays ungated
    [InlineData("5/55 (Class: 1 OK, 2 No)")]
    [InlineData("5/55 (Race: 3 OK)")]
    [InlineData("5/55 (Alignment: Good)")]
    public void Restriction_LeftAsNone_RawHintRetained(string wire)
    {
        Assert.True(RoomExit.TryParseWire(wire, out RoomExit exit));
        Assert.Equal(RoomExitHint.None, exit.Hint);
        Assert.False(string.IsNullOrEmpty(exit.RawHint));
    }

    // ----- Level gate (Form A — "(Level: MIN to MAX)") ---------------

    [Theory]
    [InlineData("5/55 (Level: 40 to 0)",   40, 0)]    // floor only (0 max = no cap)
    [InlineData("5/55 (Level: 10 to 999)", 10, 0)]    // 999 max = no cap
    [InlineData("5/55 (Level: 0 to 3)",     0, 3)]    // cap only (0 min = no floor)
    [InlineData("5/55 (Level: 10 to 25)",  10, 25)]   // both bounds
    public void LevelGate_ParsesWindow_StaysNoneHint(string wire, int min, int max)
    {
        Assert.True(RoomExit.TryParseWire(wire, out RoomExit exit));
        Assert.Equal(RoomExitHint.None, exit.Hint);   // still a plain cardinal step
        Assert.True(exit.HasLevelGate);
        Assert.Equal(min, exit.MinLevel);
        Assert.Equal(max, exit.MaxLevel);
    }

    [Theory]
    [InlineData(40, 0,  "Level 40+")]
    [InlineData(0,  3,  "Level ≤3")]
    [InlineData(10, 25, "Level 10–25")]
    [InlineData(0,  0,  "")]
    public void FormatLevelGate_RendersFriendlyLabel(int min, int max, string expected)
    {
        Assert.Equal(expected, RoomExit.FormatLevelGate(min, max));
    }
}
