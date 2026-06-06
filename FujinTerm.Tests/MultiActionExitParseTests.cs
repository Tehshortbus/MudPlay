using FujinTerm.Game.Map;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Parser-shape coverage for <see cref="MultiActionExitData"/> — the
/// modifier text and the Action#N cell shape.
/// </summary>
public sealed class MultiActionExitParseTests
{
    // ----- modifier -----------------------------------------------------

    [Theory]
    [InlineData("1/2 (Hidden/Needs 1 Actions, any order)",      1, false)]
    [InlineData("1/2 (Hidden/Needs 2 Actions, specific order)", 2, true)]
    [InlineData("1/2 (Hidden/Needs 5 Actions, any order)",      5, false)]
    public void ParseModifier_ExtractsCountAndOrder(string wire, int expectedCount, bool expectedSpecific)
    {
        (int count, bool specific) = MultiActionExitData.ParseModifier(wire);
        Assert.Equal(expectedCount, count);
        Assert.Equal(expectedSpecific, specific);
    }

    [Theory]
    [InlineData("1/2 (Hidden/Needs 1278 Actions, any order)", 1)]    // anomaly cap
    [InlineData("nothing useful here", 1)]                            // no match → default 1
    public void ParseModifier_AbsurdValuesFallBackToDefault(string raw, int expectedCount)
    {
        (int count, _) = MultiActionExitData.ParseModifier(raw);
        Assert.Equal(expectedCount, count);
    }

    // ----- action cell --------------------------------------------------

    [Fact]
    public void ParseActionCell_SameRoom_BasicShape()
    {
        var cell = MultiActionExitData.ParseActionCell(
            "Action#1 [on the N exit of this room]: say faith, speak faith, answer faith");
        Assert.NotNull(cell);
        Assert.Equal(1, cell!.StepNumber);
        Assert.Equal(Direction.N, cell.ExitDirection);
        Assert.Null(cell.RemoteSourceRoom);
        Assert.Equal(3, cell.Commands.Count);
        Assert.Equal("say faith",    cell.Commands[0]);
        Assert.Equal("speak faith",  cell.Commands[1]);
        Assert.Equal("answer faith", cell.Commands[2]);
    }

    [Fact]
    public void ParseActionCell_RemoteRoom_CarriesRoomKey()
    {
        var cell = MultiActionExitData.ParseActionCell(
            "Action#1 [on the S exit of room 17/248]: push button, push button, push button");
        Assert.NotNull(cell);
        Assert.Equal(1, cell!.StepNumber);
        Assert.Equal(Direction.S, cell.ExitDirection);
        Assert.NotNull(cell.RemoteSourceRoom);
        Assert.Equal(17, cell.RemoteSourceRoom!.Value.Map);
        Assert.Equal(248, cell.RemoteSourceRoom!.Value.Room);
    }

    [Fact]
    public void ParseActionCell_TrailingParenModifier_StrippedFromCommand()
    {
        // "(Item: 1289)" suffix on the last command is metadata, not part
        // of the verb to send.
        var cell = MultiActionExitData.ParseActionCell(
            "Action#1 [on the D exit of this room]: dig sand, move sand, dig claw (Item: 1289)");
        Assert.NotNull(cell);
        Assert.Equal(3, cell!.Commands.Count);
        Assert.Equal("dig claw", cell.Commands[2]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not an action cell")]
    [InlineData("Action# bad [on the N exit of this room]: cmd")]
    public void ParseActionCell_RejectsBadInput(string? raw)
    {
        Assert.Null(MultiActionExitData.ParseActionCell(raw!));
    }

    [Theory]
    [InlineData("Action#1 [on the N exit of this room]: pull",  Direction.N)]
    [InlineData("Action#1 [on the south exit of this room]: x", Direction.S)]
    [InlineData("Action#2 [on the NE exit of this room]: y",    Direction.NE)]
    [InlineData("Action#3 [on the up exit of this room]: z",    Direction.U)]
    public void ParseActionCell_DirectionAliases(string raw, Direction expected)
    {
        var cell = MultiActionExitData.ParseActionCell(raw);
        Assert.NotNull(cell);
        Assert.Equal(expected, cell!.ExitDirection);
    }
}
