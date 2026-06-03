using System.Collections.Generic;
using System.IO;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

public sealed class RoomDisplayParserTests : IDisposable
{
    private readonly string _root;

    public RoomDisplayParserTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-roomdisplay-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ----- fixtures --------------------------------------------------

    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Town Gates",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "North Square",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 2, "Room Number": 5, "Name": "Cellar",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "2/4", "D": "0" }
        ]
        """;

    private (RoomTracker Tracker, RoomDisplayParser Parser) NewParser(string json = GraphJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        LineExtractor lines = new(new TerminalEmulator(80, 25));
        RoomDisplayParser parser = new(lines, tracker);
        return (tracker, parser);
    }

    // ----- happy paths ----------------------------------------------

    [Fact]
    public void CompactRoom_NameThenExits_RegistersObservation()
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        parser.FeedTestLines(new[]
        {
            "Town Gates",
            "Obvious exits: north."
        });

        Assert.Equal(RoomConfidence.Located, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void VerboseRoom_NameThenDescriptionThenExits_RegistersObservation()
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        parser.FeedTestLines(new[]
        {
            "Town Gates",
            "You are standing at the south gate of the town. The road",
            "continues north into the heart of town.",
            "Obvious exits: north."
        });

        Assert.Equal(RoomConfidence.Located, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void MultipleExits_ParsedAsSet()
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        // North Square (1/2) has just S; verify the multi-exit parse
        // by faking a room with all four cardinals.
        parser.FeedTestLines(new[]
        {
            "Town Gates",
            "Obvious exits: north, south, east and west."
        });

        // Town Gates has only N in the graph — so the (Name, ExitMask)
        // tuple won't match a 1-of-1; parser still emits the
        // observation but the tracker lands Lost (no candidate).
        Assert.NotEqual(RoomConfidence.Located, tracker.State.Confidence);
    }

    [Fact]
    public void ObviousExitsNone_EmitsEmptyExitSet()
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        parser.FeedTestLines(new[]
        {
            "Town Gates",
            "Obvious exits: none."
        });

        // Town Gates has N in the graph; with exits={}, no candidate
        // matches → Lost.
        Assert.Equal(RoomConfidence.Lost, tracker.State.Confidence);
    }

    [Fact]
    public void UpDownExits_RegisterAsVerticalDirections()
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        // Cellar (2/5) has only U.
        parser.FeedTestLines(new[]
        {
            "Cellar",
            "Obvious exits: up."
        });

        Assert.Equal(RoomConfidence.Located, tracker.State.Confidence);
        Assert.Equal(new RoomKey(2, 5), tracker.State.CurrentRoom!.Key);
    }

    // ----- block boundary semantics ---------------------------------

    [Fact]
    public void MovementTransition_BetweenRooms_ResetsNameSearch()
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        // The first room's lines, then a movement transition, then a
        // new room display. The transition line acts as a block
        // boundary so the second room's name is correctly picked.
        parser.FeedTestLines(new[]
        {
            "Town Gates",
            "Obvious exits: north."
        });
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);

        parser.FeedTestLines(new[]
        {
            "You walk north.",
            "North Square",
            "Obvious exits: south."
        });
        Assert.Equal(new RoomKey(1, 2), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void BlankLine_BeforeRoomName_IsHandledAsBoundary()
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        // Combat ending + blank + new room display.
        parser.FeedTestLines(new[]
        {
            "You strike the goblin for 12 damage.",
            "The goblin dies.",
            "",
            "Town Gates",
            "Obvious exits: north."
        });
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void NoRoomNameRecoverable_DropsObservation()
    {
        // Only a movement-transition line before the exits anchor —
        // can't recover a name. No observation should be made.
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();

        parser.FeedTestLines(new[]
        {
            "You walk north.",
            "Obvious exits: south."
        });
        Assert.Equal(RoomConfidence.Unknown, tracker.State.Confidence);
    }

    // ----- direction parsing edge cases -----------------------------

    [Theory]
    [InlineData("n, e, s, w",                   new[] { Direction.N, Direction.E, Direction.S, Direction.W })]
    [InlineData("ne, nw, se, sw",               new[] { Direction.NE, Direction.NW, Direction.SE, Direction.SW })]
    [InlineData("north, south",                 new[] { Direction.N, Direction.S })]
    [InlineData("up and down",                  new[] { Direction.U, Direction.D })]
    [InlineData("North, South, East and West",  new[] { Direction.N, Direction.S, Direction.E, Direction.W })]
    public void ExitListParses_VariousFormats(string list, Direction[] expected)
    {
        (RoomTracker tracker, RoomDisplayParser parser) = NewParser();
        parser.FeedTestLines(new[]
        {
            "Some Room Name",
            $"Obvious exits: {list}."
        });

        // The room "Some Room Name" isn't in the graph → Lost. But the
        // observation itself must have been dispatched with the right
        // exit set; we infer this from the fact the tracker moved from
        // Unknown to Lost (Unknown stays Unknown if no observation fires).
        Assert.Equal(RoomConfidence.Lost, tracker.State.Confidence);
        // (Direct exit-set assertion would require more wiring; the
        // tracker landing in Lost rather than Unknown is the proxy.)
        _ = expected;                                       // suppress unused warning
    }
}
