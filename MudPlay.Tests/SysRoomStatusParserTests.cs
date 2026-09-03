using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

public sealed class SysRoomStatusParserTests
{
    private static (SysRoomStatusParser parser, List<SysRoomStatus> captured) NewParser()
    {
        SysRoomStatusParser parser = new();
        List<SysRoomStatus> captured = new();
        parser.StatusParsed += captured.Add;
        return (parser, captured);
    }

    private static void Arm(SysRoomStatusParser parser, string command = "sys st")
        => parser.ObserveOutbound(Encoding.Latin1.GetBytes(command + "\r\n"));

    // Verbatim capture from a live gang-house room, including the 80-column
    // wrapping that splits item 470 and item 430 mid-token.
    private static readonly string[] LiveCapture =
    {
        "Room 2187  Map: 1",
        "This room as Area: Max: 0  Current: 0",
        "Min: 0 Max: 0 Group: Lair by Number: 0",
        "Room Max: 5  Current: 0  Last Killed: 00:00:00 Delay: 0",
        "No controlling room.",
        "Patrollable",
        "Ganghouse",
        "Monsters: None",
        "Items: 521(0) 743(0) 882(0) 690(0) 464(0) 1484(0) 37(0) 890(0) 1443(0) 891(0) 47",
        "0(0) 466(0) 1461(0) 899(0) 420(0) 465(0)",
        "Hidden items: 1845(0) 14(0) 894(0) 223(0) 879(0) 870(0) 897(1) 876(1) 402(0) 430",
        "(0) 264(0) 905(0) 419(0) 422(0) 896(0)",
    };

    private static SysRoomStatus ParseLive(SysRoomStatusParser parser, List<SysRoomStatus> captured)
    {
        Arm(parser);
        foreach (string line in LiveCapture) parser.FeedTestLine(line);
        parser.FeedTestLine("[HP=899/MA=573]:", isPromptLine: true);
        return Assert.Single(captured);
    }

    [Fact]
    public void ParsesRoomIdentityFromTheLiveCapture()
    {
        (SysRoomStatusParser parser, List<SysRoomStatus> captured) = NewParser();

        SysRoomStatus s = ParseLive(parser, captured);

        Assert.Equal(new RoomKey(1, 2187), s.Room);
    }

    [Fact]
    public void RejoinsItemWrappedMidToken()
    {
        // "47" + "0(0)" is item 470, not items 47 and 0. This is the single
        // highest-risk detail in the format.
        (SysRoomStatusParser parser, List<SysRoomStatus> captured) = NewParser();

        SysRoomStatus s = ParseLive(parser, captured);

        Assert.Equal(16, s.Items.Count);
        Assert.Contains(s.Items, i => i.ItemId == 470);
        Assert.DoesNotContain(s.Items, i => i.ItemId == 47);
        Assert.DoesNotContain(s.Items, i => i.ItemId == 0);
    }

    [Fact]
    public void RejoinsHiddenItemWrappedBetweenIdAndValue()
    {
        // "430" + "(0)" splits between the id and its parenthesised value.
        (SysRoomStatusParser parser, List<SysRoomStatus> captured) = NewParser();

        SysRoomStatus s = ParseLive(parser, captured);

        Assert.Equal(15, s.HiddenItems.Count);
        Assert.Contains(s.HiddenItems, i => i.ItemId == 430);
    }

    [Fact]
    public void ReadsParenthesisedValueAsQuantityMinusOne()
    {
        (SysRoomStatusParser parser, List<SysRoomStatus> captured) = NewParser();

        SysRoomStatus s = ParseLive(parser, captured);

        SysRoomItem pearl = s.HiddenItems.Single(i => i.ItemId == 897);
        Assert.Equal(1, pearl.RawValue);
        Assert.Equal(2, pearl.Count);

        SysRoomItem corselet = s.Items.Single(i => i.ItemId == 521);
        Assert.Equal(0, corselet.RawValue);
        Assert.Equal(1, corselet.Count);
    }

    [Fact]
    public void MonstersNoneReadsAsNoMonsters()
    {
        (SysRoomStatusParser parser, List<SysRoomStatus> captured) = NewParser();

        SysRoomStatus s = ParseLive(parser, captured);

        Assert.Equal("None", s.MonstersRaw);
        Assert.False(s.HasMonsters);
    }

    [Fact]
    public void PopulatedMonstersLineIsCarriedVerbatim()
    {
        // The populated wording has never been captured, so the parser keeps the
        // remainder as-is rather than guessing at a token format.
        (SysRoomStatusParser parser, List<SysRoomStatus> captured) = NewParser();

        Arm(parser);
        parser.FeedTestLine("Room 12  Map: 3");
        parser.FeedTestLine("Monsters: 314(0) 315(0)");
        parser.FeedTestLine("[HP=100]:", isPromptLine: true);

        SysRoomStatus s = Assert.Single(captured);
        Assert.Equal("314(0) 315(0)", s.MonstersRaw);
        Assert.True(s.HasMonsters);
    }

    [Fact]
    public void UnarmedInputProducesNothing()
    {
        // The outbound gate is a security control: this parser writes location,
        // so a chat line echoing a room header must never relocate the player.
        (SysRoomStatusParser parser, List<SysRoomStatus> captured) = NewParser();

        foreach (string line in LiveCapture) parser.FeedTestLine(line);
        parser.FeedTestLine("[HP=899/MA=573]:", isPromptLine: true);

        Assert.Empty(captured);
    }

    [Theory]
    [InlineData("sys st")]
    [InlineData("sys status")]
    [InlineData("sysop st")]
    [InlineData("sysop status")]
    [InlineData("SYS ST")]
    [InlineData("sys st room 2187")]
    public void ArmsOnEveryAcceptedAbbreviation(string command)
    {
        (SysRoomStatusParser parser, List<SysRoomStatus> captured) = NewParser();

        Arm(parser, command);
        parser.FeedTestLine("Room 12  Map: 3");
        parser.FeedTestLine("[HP=100]:", isPromptLine: true);

        Assert.Single(captured);
    }

    [Theory]
    [InlineData("stat")]
    [InlineData("s")]
    [InlineData("say sys st")]
    [InlineData("sys")]
    [InlineData("sys list users")]
    public void DoesNotArmOnUnrelatedCommands(string command)
    {
        (SysRoomStatusParser parser, List<SysRoomStatus> captured) = NewParser();

        Arm(parser, command);
        parser.FeedTestLine("Room 12  Map: 3");
        parser.FeedTestLine("[HP=100]:", isPromptLine: true);

        Assert.Empty(captured);
    }

    [Fact]
    public void ChatLineMidBlockDoesNotPolluteTheItemList()
    {
        (SysRoomStatusParser parser, List<SysRoomStatus> captured) = NewParser();

        Arm(parser);
        parser.FeedTestLine("Room 12  Map: 3");
        parser.FeedTestLine("Items: 100(0) 101(0)");
        parser.FeedTestLine("Bob gossips: got 999(9) of them!");
        parser.FeedTestLine("Hidden items: 200(0)");
        parser.FeedTestLine("[HP=100]:", isPromptLine: true);

        SysRoomStatus s = Assert.Single(captured);
        Assert.Equal(new[] { 100, 101 }, s.Items.Select(i => i.ItemId));
        Assert.Equal(new[] { 200 }, s.HiddenItems.Select(i => i.ItemId));
    }

    [Fact]
    public void MissingOptionalLinesStillParse()
    {
        // Patrollable / Ganghouse / controlling-room / lair lines are all
        // conditional; a room printing none of them still yields a record.
        (SysRoomStatusParser parser, List<SysRoomStatus> captured) = NewParser();

        Arm(parser);
        parser.FeedTestLine("Room 4001  Map: 7");
        parser.FeedTestLine("Monsters: None");
        parser.FeedTestLine("[HP=100]:", isPromptLine: true);

        SysRoomStatus s = Assert.Single(captured);
        Assert.Equal(new RoomKey(7, 4001), s.Room);
        Assert.Empty(s.Items);
        Assert.Empty(s.HiddenItems);
    }

    [Fact]
    public void EmptyItemListsParseAsEmpty()
    {
        (SysRoomStatusParser parser, List<SysRoomStatus> captured) = NewParser();

        Arm(parser);
        parser.FeedTestLine("Room 4001  Map: 7");
        parser.FeedTestLine("Items: None");
        parser.FeedTestLine("Hidden items: None");
        parser.FeedTestLine("[HP=100]:", isPromptLine: true);

        SysRoomStatus s = Assert.Single(captured);
        Assert.Empty(s.Items);
        Assert.Empty(s.HiddenItems);
    }

    [Fact]
    public void BlockWithoutHeaderEmitsNothing()
    {
        // A denied `sys st` prints no room header. The arm window closes empty
        // rather than emitting a half-record.
        (SysRoomStatusParser parser, List<SysRoomStatus> captured) = NewParser();

        Arm(parser);
        parser.FeedTestLine("Command not recognized.");
        parser.FeedTestLine("[HP=100]:", isPromptLine: true);

        Assert.Empty(captured);
    }

    [Fact]
    public void SecondBlockInOneWindowEmitsBothRecords()
    {
        // `sys st room <n>` batches: a fresh header flushes the block in progress.
        (SysRoomStatusParser parser, List<SysRoomStatus> captured) = NewParser();

        Arm(parser);
        parser.FeedTestLine("Room 12  Map: 3");
        parser.FeedTestLine("Items: 100(0)");
        parser.FeedTestLine("Room 13  Map: 3");
        parser.FeedTestLine("Items: 101(0)");
        parser.FeedTestLine("[HP=100]:", isPromptLine: true);

        Assert.Equal(2, captured.Count);
        Assert.Equal(new RoomKey(3, 12), captured[0].Room);
        Assert.Equal(new RoomKey(3, 13), captured[1].Room);
    }

    [Fact]
    public void ExpiredWindowStopsScanning()
    {
        (SysRoomStatusParser parser, List<SysRoomStatus> captured) = NewParser();
        DateTime now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
        parser.NowProvider = () => now;

        Arm(parser);
        now = now.AddSeconds(30);
        parser.FeedTestLine("Room 12  Map: 3");
        parser.FeedTestLine("[HP=100]:", isPromptLine: true);

        Assert.Empty(captured);
    }
}
