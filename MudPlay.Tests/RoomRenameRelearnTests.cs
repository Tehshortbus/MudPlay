using System.IO;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A room the data leaves unnamed is matched on its exits whether or not it bears
// a learned name: a gang's house can be renamed, each room by itself, so the
// name the map has for such a room is no proof against the walk. Only with a
// move in flight that predicts the room, and never for a room the data names.
//
// The house is one of the Stock set's own (15/874 to 15/881, entered from
// 1/1096): its room numbers and exits are the data's, so two adjacent rooms have
// the same exits (874 and 877) and one exit has no way back (876 south to 875).
// The names are made up.
public sealed class RoomRenameRelearnTests : IDisposable
{
    private static readonly RoomKey Street = new(1, 1096);
    private static readonly RoomKey Post = new(1, 1097);     // prints {E}; the graph has a hidden exit south too
    private static readonly RoomKey Entrance = new(15, 874); // N 877, S street, E 875
    private static readonly RoomKey Shop = new(15, 875);     // W 874
    private static readonly RoomKey Landing = new(15, 877);  // N 879, S 874, E 876

    private static readonly RoomKey[] House =
    {
        new(15, 874), new(15, 875), new(15, 876), new(15, 877),
        new(15, 878), new(15, 879), new(15, 880), new(15, 881),
    };

    private const string Z = "\"NE\": \"0\", \"NW\": \"0\", \"SE\": \"0\", \"SW\": \"0\", \"U\": \"0\", \"D\": \"0\"";

    private static readonly string RoomsJson = $$"""
        [
          { "Map Number": 1, "Room Number": 1096, "Name": "Lantern Row", "N": "15/874", "S": "0", "E": "0", "W": "1/1097", {{Z}} },
          { "Map Number": 1, "Room Number": 1097, "Name": "Guard Post", "N": "0", "S": "1/1098 (Hidden/Searchable)", "E": "1/1096", "W": "0", {{Z}} },
          { "Map Number": 1, "Room Number": 1098, "Name": "Cell", "N": "1/1097", "S": "0", "E": "0", "W": "0", {{Z}} },
          { "Map Number": 15, "Room Number": 874, "Name": null, "N": "15/877", "S": "1/1096", "E": "15/875", "W": "0", {{Z}} },
          { "Map Number": 15, "Room Number": 875, "Name": null, "N": "0", "S": "0", "E": "0", "W": "15/874", {{Z}} },
          { "Map Number": 15, "Room Number": 876, "Name": null, "N": "15/878", "S": "15/875", "E": "0", "W": "15/877", {{Z}} },
          { "Map Number": 15, "Room Number": 877, "Name": null, "N": "15/879", "S": "15/874", "E": "15/876", "W": "0", {{Z}} },
          { "Map Number": 15, "Room Number": 878, "Name": null, "N": "0", "S": "15/876", "E": "0", "W": "15/879", {{Z}} },
          { "Map Number": 15, "Room Number": 879, "Name": null, "N": "15/985", "S": "15/877", "E": "15/878", "W": "15/880", {{Z}} },
          { "Map Number": 15, "Room Number": 880, "Name": null, "N": "0", "S": "15/881", "E": "15/879", "W": "0", {{Z}} },
          { "Map Number": 15, "Room Number": 881, "Name": null, "N": "15/880", "S": "0", "E": "0", "W": "0", {{Z}} },
          { "Map Number": 15, "Room Number": 985, "Name": "Back Lane", "N": "0", "S": "15/879", "E": "0", "W": "0", {{Z}} },
          { "Map Number": 15, "Room Number": 958, "Name": null, "N": "15/959", "S": "0", "E": "0", "W": "0", {{Z}} },
          { "Map Number": 15, "Room Number": 959, "Name": null, "N": "15/960", "S": "15/958", "E": "0", "W": "0", {{Z}} },
          { "Map Number": 15, "Room Number": 960, "Name": null, "N": "15/961 (Key: 1033 [or 301 picklocks])", "S": "15/959", "E": "0", "W": "0", {{Z}} },
          { "Map Number": 15, "Room Number": 961, "Name": null, "N": "0", "S": "15/960 (Key: 1033 [or 301 picklocks])", "E": "0", "W": "0", {{Z}} },
          { "Map Number": 2, "Room Number": 500, "Name": "Kitchen", "N": "2/501", "S": "2/501", "E": "2/501", "W": "0", {{Z}} },
          { "Map Number": 2, "Room Number": 501, "Name": "Scullery", "N": "0", "S": "2/500", "E": "0", "W": "0", {{Z}} },
          { "Map Number": 3, "Room Number": 1, "Name": "Yard", "N": "3/3", "S": "0", "E": "3/2", "W": "0", {{Z}} },
          { "Map Number": 3, "Room Number": 2, "Name": "Yard", "N": "3/5", "S": "0", "E": "0", "W": "3/1 (Hidden/Searchable)", {{Z}} },
          { "Map Number": 3, "Room Number": 3, "Name": null, "N": "3/4", "S": "3/1", "E": "3/5", "W": "0", {{Z}} },
          { "Map Number": 3, "Room Number": 4, "Name": "Tower", "N": "0", "S": "3/3", "E": "0", "W": "0", {{Z}} },
          { "Map Number": 3, "Room Number": 5, "Name": "Barn", "N": "0", "S": "3/2", "E": "0", "W": "0", {{Z}} }
        ]
        """;

    // Made up: a yard beside another of the same name, whose way back is hidden,
    // a gate room the data leaves unnamed north of the first, with a way east to
    // the barn, and a tower beyond.
    private static readonly RoomKey Yard = new(3, 1);
    private static readonly RoomKey GateRoom = new(3, 3);

    // The end of another of the set's houses (15/958 to 15/961; 958's way west is
    // left out): two rooms with the same exits, north and south, and beyond them
    // a room behind a locked door with the one exit back.
    private static readonly RoomKey Stair = new(15, 959);
    private static readonly RoomKey StoreRoom = new(15, 960);
    private static readonly RoomKey Vault = new(15, 961);

    private static readonly string[] Token = { "n", "s", "e", "w", "ne", "nw", "se", "sw", "u", "d" };

    private readonly string _root;

    public RoomRenameRelearnTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-roomrename-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), RoomsJson);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
    }

    private static HashSet<Direction> X(params Direction[] exits) => new(exits);

    // The room map of a session, the house bearing the names kept before (none:
    // the house was never seen).
    private RoomGraphManager Map(Func<RoomKey, string?>? stored = null)
    {
        GameDataCache cache = new(_root);
        RoomGraphManager graph = new(cache);
        cache.ActiveSetChanged += graph.OnActiveSetChanged;
        cache.SwitchSet("alpha");
        if (stored is not null)
        {
            Dictionary<RoomKey, string> names = new();
            foreach (RoomKey room in House)
                if (stored(room) is { } name) names[room] = name;
            graph.ApplyStoredNames(names);
        }
        return graph;
    }

    private static (RoomTracker Tracker, List<NameLearnedEvent> Learned) Track(RoomGraphManager graph, RoomKey at)
    {
        RoomTracker tracker = new(graph);
        List<NameLearnedEvent> learned = new();
        tracker.NameLearned += learned.Add;
        tracker.SetLocated(at);
        return (tracker, learned);
    }

    private static string OwnName(RoomKey room) => $"Old Room {room.Room}";
    private static string OwnNewName(RoomKey room) => $"New Room {room.Room}";

    // The exits a room's display shows: every exit of the fixture's but the guard
    // post's hidden one.
    private static HashSet<Direction> Shown(Room room)
    {
        HashSet<Direction> shown = new();
        foreach ((Direction direction, RoomExit exit) in room.Exits)
            if (exit.RawHint?.StartsWith("Hidden", StringComparison.OrdinalIgnoreCase) != true) shown.Add(direction);
        return shown;
    }

    // Walk the whole house from the street, into every room and back out of it,
    // the game echoing each move and printing each room under printed's name.
    // Returns what went wrong, step by step, and how many names were learned.
    private static (List<string> Wrong, int Steps, List<NameLearnedEvent> Learned) WalkTheHouse(
        RoomGraphManager graph, Func<RoomKey, string> printed)
    {
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Street);
        List<string> wrong = new();
        int steps = 0;
        HashSet<RoomKey> house = new(House), seen = new();

        void Step(Direction direction, RoomKey to)
        {
            tracker.NoteMoveSent(direction);
            tracker.NoteInboundMoveEcho(Token[(int)direction]);
            tracker.NoteRoomObserved(new RoomObservation(printed(to), Shown(graph.GetRoom(to)!)));
            steps++;
            if (tracker.State.Confidence != RoomConfidence.Confirmed || tracker.State.CurrentRoom?.Key != to)
                wrong.Add($"step {steps} {direction} into {to}: {tracker.State.Confidence} at {tracker.State.CurrentRoom?.Key}");
        }

        void Visit(RoomKey here)
        {
            seen.Add(here);
            foreach ((Direction direction, RoomExit exit) in graph.GetRoom(here)!.Exits.ToList())
            {
                if (!house.Contains(exit.Target) || seen.Contains(exit.Target)) continue;
                Direction? back = null;
                foreach ((Direction way, RoomExit other) in graph.GetRoom(exit.Target)!.Exits)
                    if (other.Target == here) { back = way; break; }
                if (back is null) continue;
                Step(direction, exit.Target);
                Visit(exit.Target);
                Step(back.Value, here);
            }
        }

        Step(Direction.N, Entrance);
        Visit(Entrance);
        Assert.Equal(House.Length, seen.Count);
        return (wrong, steps, learned);
    }

    // ----- the whole house, renamed ---------------------------------------

    // The hard case: every room of the house prints one name, so a room just
    // re-learned and the next one in look alike, two of them exits and all.
    [Fact]
    public void AHouseWhoseRoomsShareAName_Renamed_IsWalkedRoomByRoom_AndEveryRoomTakesTheNewName()
    {
        RoomGraphManager graph = Map(stored: _ => "Old Lodge Room");

        (List<string> wrong, int steps, List<NameLearnedEvent> learned) = WalkTheHouse(graph, _ => "New Lodge Room");

        Assert.Empty(wrong);
        Assert.Equal(15, steps);
        Assert.All(House, room => Assert.Equal("New Lodge Room", graph.GetRoom(room)!.Name));
        Assert.Equal(House.OrderBy(r => r.Room), learned.Select(e => e.Key).OrderBy(r => r.Room));
        Assert.Empty(graph.FindByName("Old Lodge Room"));
    }

    // The same walk when the house was never seen: main's nameless rule, unchanged.
    [Fact]
    public void TheSameHouse_NeverNamed_IsWalkedAndLearnedAsBefore()
    {
        RoomGraphManager graph = Map();

        (List<string> wrong, int steps, List<NameLearnedEvent> learned) = WalkTheHouse(graph, _ => "New Lodge Room");

        Assert.Empty(wrong);
        Assert.Equal(15, steps);
        Assert.All(House, room => Assert.Equal("New Lodge Room", graph.GetRoom(room)!.Name));
        Assert.Equal(House.Length, learned.Count);
    }

    // And when nothing was renamed: the stored names are the printed ones.
    [Fact]
    public void TheSameHouse_NotRenamed_IsWalkedOnItsStoredNames_AndNothingIsLearned()
    {
        RoomGraphManager graph = Map(stored: _ => "Old Lodge Room");

        (List<string> wrong, _, List<NameLearnedEvent> learned) = WalkTheHouse(graph, _ => "Old Lodge Room");

        Assert.Empty(wrong);
        Assert.Empty(learned);
    }

    // The usual house: each room has a name of its own.
    [Fact]
    public void AHouseWhoseRoomsHaveTheirOwnNames_AllRenamed_IsWalkedAndRelearned()
    {
        RoomGraphManager graph = Map(stored: OwnName);

        (List<string> wrong, _, List<NameLearnedEvent> learned) = WalkTheHouse(graph, OwnNewName);

        Assert.Empty(wrong);
        Assert.All(House, room => Assert.Equal(OwnNewName(room), graph.GetRoom(room)!.Name));
        Assert.Equal(House.Length, learned.Count);
    }

    [Fact]
    public void AHouseWhoseRoomsHaveTheirOwnNames_NeverNamed_OrNotRenamed_IsWalkedAsBefore()
    {
        RoomGraphManager never = Map();
        (List<string> wrong, _, List<NameLearnedEvent> learned) = WalkTheHouse(never, OwnName);
        Assert.Empty(wrong);
        Assert.Equal(House.Length, learned.Count);

        RoomGraphManager kept = Map(stored: OwnName);
        (wrong, _, learned) = WalkTheHouse(kept, OwnName);
        Assert.Empty(wrong);
        Assert.Empty(learned);
    }

    // A rename can be of one room alone.
    [Fact]
    public void OneRoomOfAHouseRenamed_IsTheOnlyOneRelearned()
    {
        RoomGraphManager graph = Map(stored: OwnName);
        string Printed(RoomKey room) => room == Landing ? "The Long Gallery" : OwnName(room);

        (List<string> wrong, _, List<NameLearnedEvent> learned) = WalkTheHouse(graph, Printed);

        Assert.Empty(wrong);
        Assert.Equal(new[] { new NameLearnedEvent(Landing, "The Long Gallery") }, learned);
        Assert.All(House, room => Assert.Equal(Printed(room), graph.GetRoom(room)!.Name));
        Assert.Equal(new[] { Landing }, graph.FindCandidates("The Long Gallery", X(Direction.N, Direction.S, Direction.E)));
    }

    // ----- what is and isn't the renamed room -------------------------------

    // The exits are the only evidence, as for a room with no name: a display
    // with an exit the room doesn't have is not the room.
    [Fact]
    public void ADisplayWhoseExitsAreNotTheRooms_RelearnsNothing()
    {
        RoomGraphManager graph = Map(stored: OwnName);
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Street);

        tracker.NoteMoveSent(Direction.N);
        tracker.NoteRoomObserved(new RoomObservation("The Long Gallery", X(Direction.S, Direction.W)));

        Assert.Empty(learned);
        Assert.Equal(OwnName(Entrance), graph.GetRoom(Entrance)!.Name);
        Assert.NotEqual(RoomConfidence.Confirmed, tracker.State.Confidence);
    }

    // A room the data names is held to that name.
    [Fact]
    public void ARoomTheDataNames_PrintingAnotherName_IsNotRelearned()
    {
        RoomGraphManager graph = Map(stored: OwnName);
        Assert.False(graph.IsUnnamedInData(Street));
        Assert.True(graph.IsUnnamedInData(Entrance));
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Entrance);

        tracker.NoteMoveSent(Direction.S);
        tracker.NoteRoomObserved(new RoomObservation("Lamp Row", X(Direction.N, Direction.W)));

        Assert.Empty(learned);
        Assert.Equal("Lantern Row", graph.GetRoom(Street)!.Name);
        Assert.NotEqual(RoomConfidence.Confirmed, tracker.State.Confidence);
    }

    // The display is of the guard post, beside the street the move leaves, seen
    // with its hidden exit left out, which the candidate search allows for; its
    // exits fit the house room the move was heading for too. With no echo to say
    // the game took the move, the post's own display is as near as the house
    // room's: the house room keeps its name, or stays nameless, and the tracker
    // is anchored at the post.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ANamedNeighboursDisplayWithAnExitLeftOut_IsNotTakenForTheHouseRoomAhead(bool houseSeenBefore)
    {
        RoomGraphManager graph = houseSeenBefore ? Map(stored: OwnName) : Map();
        Assert.Equal(new[] { Post }, graph.FindCandidates("Guard Post", X(Direction.E)));
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Street);

        tracker.NoteMoveSent(Direction.N);
        tracker.NoteRoomObserved(new RoomObservation("Guard Post", X(Direction.E)));

        Assert.Empty(learned);
        Assert.Equal(houseSeenBefore ? OwnName(Entrance) : string.Empty, graph.GetRoom(Entrance)!.Name);
        Assert.Equal((RoomConfidence.Confirmed, Post), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
    }

    // With the game's echo of the move ahead of it, the same display is the
    // arrival: the walk outweighs a room beside it, and the house room takes the
    // name.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADisplayAfterTheMovesEcho_IsTheArrival_ThoughANamedNeighbourCouldHaveShownIt(bool houseSeenBefore)
    {
        RoomGraphManager graph = houseSeenBefore ? Map(stored: OwnName) : Map();
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Street);

        tracker.NoteMoveSent(Direction.N);
        tracker.NoteInboundMoveEcho("n");
        tracker.NoteRoomObserved(new RoomObservation("Guard Post", X(Direction.E)));

        Assert.Equal(new[] { new NameLearnedEvent(Entrance, "Guard Post") }, learned);
        Assert.Equal((RoomConfidence.Confirmed, Entrance), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
    }

    // A room with no name is learned on its exits alone, whatever it prints. Its
    // name is the sysop's own text and is often one the data has for a room
    // elsewhere; here the shop prints the street's name, and the street's exits
    // cover the ones shown. The street is beside the room being left, but its
    // display always shows its way north, so it could not have shown this one.
    // Walked into, echoed or not, or stood beside with no move in flight, the
    // shop is adopted and takes the name, and the tracker is not sent to the
    // street. (Holding the name against the room confirmed the tracker in the
    // wrong room on every visit to a house with such a name.)
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void ARoomWithNoName_PrintingANameTheDataHasElsewhere_IsStillLearned(bool moveInFlight, bool echoed)
    {
        RoomGraphManager graph = Map();
        Assert.Single(graph.FindByNameCoveringExits("Lantern Row", X(Direction.W)));
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Entrance);

        if (moveInFlight)
        {
            tracker.NoteMoveSent(Direction.E);
            if (echoed) tracker.NoteInboundMoveEcho("e");
        }
        tracker.NoteRoomObserved(new RoomObservation("Lantern Row", X(Direction.W)));

        Assert.Equal(new[] { new NameLearnedEvent(Shop, "Lantern Row") }, learned);
        Assert.Equal("Lantern Row", graph.GetRoom(Shop)!.Name);
        Assert.Equal((RoomConfidence.Confirmed, Shop), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
    }

    // The same named room's display with no move in flight, standing beside a
    // house whose rooms bear stored names or in it, or beside a house never seen:
    // nothing is learned. Beside the post, its display is as near as the house
    // room's, with no move to tell them apart.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ANamedRoomsDisplayWithAnExitLeftOut_WithNoMoveInFlight_NamesNoHouseRoom(bool beside, bool houseSeenBefore)
    {
        RoomGraphManager graph = houseSeenBefore ? Map(stored: OwnName) : Map();
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, beside ? Street : Entrance);

        tracker.NoteRoomObserved(new RoomObservation("Guard Post", X(Direction.E)));

        Assert.Empty(learned);
        Assert.Equal(houseSeenBefore ? OwnName(Entrance) : string.Empty, graph.GetRoom(Entrance)!.Name);
        Assert.Equal((RoomConfidence.Confirmed, Post), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
    }

    // A room is only re-learned by walking into it. Moved into a renamed room by
    // something the tracker was not told of, it does not rename the room it
    // thought it stood in, nor any other: it is unsure where it is.
    [Fact]
    public void WithNoMoveInFlight_ARenamedRoomsDisplay_RenamesNothing()
    {
        RoomGraphManager graph = Map(stored: OwnName);
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Entrance);
        tracker.NoteRoomObserved(new RoomObservation(OwnName(Entrance), Shown(graph.GetRoom(Entrance)!)));

        // Now in the landing, which the board has renamed.
        tracker.NoteRoomObserved(new RoomObservation("The Long Gallery", Shown(graph.GetRoom(Landing)!)));

        Assert.Empty(learned);
        Assert.All(House, room => Assert.Equal(OwnName(room), graph.GetRoom(room)!.Name));
        Assert.Equal((RoomConfidence.Suspect, Entrance), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
    }

    // Coming back into the game: the last room on file and the steps taken since
    // put the character in the landing, which prints another name now. The
    // anchor is not renamed. Standing in the renamed room itself, likewise: it
    // keeps its old name until it is next walked into.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ComingBackIntoTheGameInARenamedRoom_RenamesNothing(bool anchoredARoomBehind)
    {
        RoomGraphManager graph = Map(stored: OwnName);
        RoomTracker tracker = new(graph);
        List<NameLearnedEvent> learned = new();
        tracker.NameLearned += learned.Add;
        RoomKey anchor = anchoredARoomBehind ? Entrance : Landing;
        tracker.Hydrate(new CharacterProfile
        {
            LastKnownRoom = new RoomRef(anchor.Map, anchor.Room),
            RecentSteps = anchoredARoomBehind ? new List<DirectionDto> { new(Direction.N) } : new List<DirectionDto>(),
        });

        tracker.NoteRoomObserved(new RoomObservation("The Long Gallery", Shown(graph.GetRoom(Landing)!)));

        Assert.Empty(learned);
        Assert.All(House, room => Assert.Equal(OwnName(room), graph.GetRoom(room)!.Name));
        Assert.Equal((RoomConfidence.Suspect, anchor), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));

        // The first step out and back in re-learns it.
        if (anchoredARoomBehind) return;
        tracker.SetLocated(Entrance);
        tracker.NoteMoveSent(Direction.N);
        tracker.NoteRoomObserved(new RoomObservation("The Long Gallery", Shown(graph.GetRoom(Landing)!)));
        Assert.Equal(new[] { new NameLearnedEvent(Landing, "The Long Gallery") }, learned);
    }

    // A room printed under two names in turn, with nobody moving: nothing is
    // learned, so nothing is asked over and over.
    [Fact]
    public void ARoomPrintedUnderTwoNamesInTurn_WithNoMoveInFlight_IsNeverRelearned()
    {
        RoomGraphManager graph = Map(stored: OwnName);
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Landing);

        for (int i = 0; i < 3; i++)
        {
            tracker.NoteRoomObserved(new RoomObservation("The Long Gallery", Shown(graph.GetRoom(Landing)!)));
            tracker.NoteRoomObserved(new RoomObservation(OwnName(Landing), Shown(graph.GetRoom(Landing)!)));
        }

        Assert.Empty(learned);
        Assert.Equal(OwnName(Landing), graph.GetRoom(Landing)!.Name);
    }

    // ----- the room being left, redisplayed ---------------------------------

    // The store room (15/880, exits south and east) redisplays, someone having
    // walked in, while the step east into the hall (15/879) is still unanswered.
    // Both its exits are exits of the hall too, and the hall must not take its
    // name. The arrival, when it comes under the hall's new name, is taken.
    [Fact]
    public void ARedisplayOfTheRoomBeingLeft_BeforeTheMoveIsEchoed_IsNotTakenForTheRoomAhead()
    {
        RoomKey storeRoom = new(15, 880), hall = new(15, 879);
        RoomGraphManager graph = Map(stored: OwnName);
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, storeRoom);
        tracker.NoteRoomObserved(new RoomObservation(OwnName(storeRoom), X(Direction.S, Direction.E)));

        tracker.NoteMoveSent(Direction.E);
        tracker.NoteRoomObserved(new RoomObservation(OwnName(storeRoom), X(Direction.S, Direction.E)));

        Assert.Empty(learned);
        Assert.Equal(OwnName(hall), graph.GetRoom(hall)!.Name);
        Assert.Equal((RoomConfidence.Pending, storeRoom), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));

        tracker.NoteInboundMoveEcho("e");
        tracker.NoteRoomObserved(new RoomObservation("The Great Hall", Shown(graph.GetRoom(hall)!)));

        Assert.Equal((RoomConfidence.Confirmed, hall), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
        Assert.Equal(new[] { new NameLearnedEvent(hall, "The Great Hall") }, learned);
    }

    // ----- a look typed right after the move --------------------------------

    // `n` then `l e` at once: the first display is the arrival, in a room that
    // prints another name than the map has, and it must not be dropped as the
    // look's. The second is the look's and moves nothing.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AMoveThenALook_IntoARenamedRoom_TakesTheArrivalAndDropsTheLook(bool houseSeenBefore)
    {
        RoomGraphManager graph = houseSeenBefore ? Map(stored: OwnName) : Map();
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Street);

        tracker.NoteMoveSent(Direction.N);
        tracker.NoteLookSent();
        tracker.NoteRoomObserved(new RoomObservation("The Front Hall", Shown(graph.GetRoom(Entrance)!)));

        Assert.Equal((RoomConfidence.Confirmed, Entrance), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
        Assert.Equal(new[] { new NameLearnedEvent(Entrance, "The Front Hall") }, learned);

        // The look east: the shop, as the map has it or renamed too.
        tracker.NoteRoomObserved(new RoomObservation("The Counting Room", Shown(graph.GetRoom(Shop)!)));

        Assert.Equal((RoomConfidence.Confirmed, Entrance), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
        Assert.Single(learned);
        Assert.Equal(houseSeenBefore ? OwnName(Shop) : string.Empty, graph.GetRoom(Shop)!.Name);
    }

    // A `look <dir>` into the guard post, typed just before the step into a house
    // never seen, is answered first. Its display fits the house room ahead, and
    // must not name it: it is dropped as the look's, and the arrival names the
    // room. Echoed or not.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ALookIntoANamedNeighbour_AnsweredBeforeTheMove_DoesNotNameTheRoomAhead(bool echoes)
    {
        RoomGraphManager graph = Map();
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Street);
        if (echoes) tracker.NoteInboundMoveEcho("stat");

        tracker.NoteLookSent();
        tracker.NoteMoveSent(Direction.N);
        tracker.NoteRoomObserved(new RoomObservation("Guard Post", X(Direction.E)));

        Assert.Empty(learned);
        Assert.Equal((RoomConfidence.Pending, Street), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));

        if (echoes) tracker.NoteInboundMoveEcho("n");
        tracker.NoteRoomObserved(new RoomObservation("The Front Hall", Shown(graph.GetRoom(Entrance)!)));

        Assert.Equal(new[] { new NameLearnedEvent(Entrance, "The Front Hall") }, learned);
        Assert.Equal((RoomConfidence.Confirmed, Entrance), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
    }

    // ----- a room renamed to a name the data has ----------------------------

    // The landing renamed "Kitchen", a name the data gives a room on another map
    // whose exits are the landing's. That room is nowhere near the walk: the room
    // one step along the move is the one shown, and it takes the name.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARoomRenamedToANameTheDataHasFarAway_IsRelearned(bool echoes)
    {
        RoomGraphManager graph = Map(stored: OwnName);
        Assert.Single(graph.FindCandidates("Kitchen", X(Direction.N, Direction.S, Direction.E)));
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Entrance);
        tracker.NoteRoomObserved(new RoomObservation(OwnName(Entrance), Shown(graph.GetRoom(Entrance)!)));

        tracker.NoteMoveSent(Direction.N);
        if (echoes) tracker.NoteInboundMoveEcho("n");
        tracker.NoteRoomObserved(new RoomObservation("Kitchen", Shown(graph.GetRoom(Landing)!)));

        Assert.Equal(new[] { new NameLearnedEvent(Landing, "Kitchen") }, learned);
        Assert.Equal((RoomConfidence.Confirmed, Landing), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
    }

    // ----- the room being left, not shown since we came to it ---------------

    // Put in the landing by hand, never shown it: the board has renamed it. The
    // step north into the hall is in flight when the landing redisplays under its
    // new name, before the game echoes the step. Its exits are all the hall's
    // too, but it is the landing's display, and the hall must not take its name.
    [Fact]
    public void ARelookOfTheRoomBeingLeft_UnderANameItDoesNotBearYet_IsNotTakenForTheRoomAhead()
    {
        RoomKey hall = new(15, 879);
        RoomGraphManager graph = Map(stored: OwnName);
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Landing);
        tracker.NoteInboundMoveEcho("stat");

        tracker.NoteMoveSent(Direction.N);
        tracker.NoteRoomObserved(new RoomObservation("The Long Gallery", Shown(graph.GetRoom(Landing)!)));

        Assert.Empty(learned);
        Assert.Equal(OwnName(hall), graph.GetRoom(hall)!.Name);
        Assert.Equal((RoomConfidence.Pending, Landing), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));

        tracker.NoteInboundMoveEcho("n");
        tracker.NoteRoomObserved(new RoomObservation("The Great Hall", Shown(graph.GetRoom(hall)!)));

        Assert.Equal(new[] { new NameLearnedEvent(hall, "The Great Hall") }, learned);
        Assert.Equal((RoomConfidence.Confirmed, hall), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
        Assert.Equal(OwnName(Landing), graph.GetRoom(Landing)!.Name);
    }

    // The same, stepping east into the shop, whose one exit the entrance's
    // display doesn't fit: the entrance's redisplay under its new name leaves the
    // tracker where it is, waiting on the step, instead of unsure of the house.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARelookOfTheRoomBeingLeft_UnderANewName_ThatFitsOnlyThatRoom_KeepsThePlace(bool echoes)
    {
        RoomGraphManager graph = Map(stored: OwnName);
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Entrance);
        if (echoes) tracker.NoteInboundMoveEcho("stat");

        tracker.NoteMoveSent(Direction.E);
        tracker.NoteRoomObserved(new RoomObservation("The Front Hall", Shown(graph.GetRoom(Entrance)!)));

        Assert.Equal((RoomConfidence.Pending, Entrance), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));

        if (echoes) tracker.NoteInboundMoveEcho("e");
        tracker.NoteRoomObserved(new RoomObservation("The Counting Room", Shown(graph.GetRoom(Shop)!)));

        Assert.Equal(new[] { new NameLearnedEvent(Shop, "The Counting Room") }, learned);
        Assert.Equal((RoomConfidence.Confirmed, Shop), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
    }

    // Once the room being left has been shown under the name it bears, a display
    // under another name that fits only it is not its own: unsure, as before.
    [Fact]
    public void OnceTheRoomBeingLeftHasBeenShown_ADisplayUnderAnotherNameFittingOnlyIt_LeavesTheTrackerUnsure()
    {
        RoomGraphManager graph = Map(stored: OwnName);
        (RoomTracker tracker, _) = Track(graph, Street);
        tracker.NoteMoveSent(Direction.N);
        tracker.NoteRoomObserved(new RoomObservation(OwnName(Entrance), Shown(graph.GetRoom(Entrance)!)));
        Assert.Equal((RoomConfidence.Confirmed, Entrance), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));

        tracker.NoteMoveSent(Direction.E);
        tracker.NoteRoomObserved(new RoomObservation("Somewhere Else", X(Direction.N, Direction.S)));

        Assert.Equal(RoomConfidence.Suspect, tracker.State.Confidence);
    }

    // ----- no echo, two rooms alike -----------------------------------------

    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    // The stair and the store room share a name and their exits, north and
    // south; the vault beyond prints a name of its own. On a statline that
    // doesn't echo moves, the store room's display comes too soon after the step
    // to tell from a re-look of the stair, and is held. The step on into the
    // vault shows the vault.
    private (RoomGraphManager Graph, RoomTracker Tracker, List<NameLearnedEvent> Learned) AtTheStair()
    {
        RoomGraphManager graph = Map();
        graph.ApplyStoredNames(new Dictionary<RoomKey, string>
        {
            [Stair] = "Lodge Room", [StoreRoom] = "Old Store", [Vault] = "Vault",
        });
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Stair);
        tracker.NoteRoomObserved(new RoomObservation("Lodge Room", X(Direction.N, Direction.S)), T0);

        tracker.NoteMoveSent(Direction.N, T0.AddSeconds(1));
        tracker.NoteRoomObserved(new RoomObservation("Lodge Room", X(Direction.N, Direction.S)), T0.AddSeconds(1.1));
        Assert.Equal((RoomConfidence.Pending, Stair), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
        Assert.NotNull(tracker.HeldDisplay);
        return (graph, tracker, learned);
    }

    // The vault's display fits the store room ahead too, and used to give it the
    // vault's name. It fits the room the second step leads to, so the held
    // display was the store room's: the store room takes that, and the vault is
    // where we are.
    [Fact]
    public void NoEcho_TheNextStepsRoomShowing_TakesTheHeldDisplayForTheRoomAhead()
    {
        (RoomGraphManager graph, RoomTracker tracker, List<NameLearnedEvent> learned) = AtTheStair();

        tracker.NoteMoveSent(Direction.N, T0.AddSeconds(3));
        tracker.NoteRoomObserved(new RoomObservation("Vault", X(Direction.S)), T0.AddSeconds(3.1));

        Assert.Equal(new[] { new NameLearnedEvent(StoreRoom, "Lodge Room") }, learned);
        Assert.Equal((RoomConfidence.Confirmed, Vault), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
        Assert.Null(tracker.HeldDisplay);
    }

    // The store room, taken from the held display while the vault's display is being
    // read, carries the held display's exits to a listener of that step, not the
    // vault's.
    [Fact]
    public void NoEcho_TheRoomTakenFromTheHeldDisplay_IsGivenThatDisplaysExits()
    {
        (RoomGraphManager _, RoomTracker tracker, List<NameLearnedEvent> _) = AtTheStair();
        List<IReadOnlySet<Direction>?> storeRoomExits = new();
        tracker.StateChanged += t =>
        {
            if (t.NewRoom?.Key == StoreRoom) storeRoomExits.Add(tracker.State.ObservedExitDirections);
        };

        tracker.NoteMoveSent(Direction.N, T0.AddSeconds(3));
        tracker.NoteRoomObserved(new RoomObservation("Vault", X(Direction.S)), T0.AddSeconds(3.1));

        IReadOnlySet<Direction>? shown = Assert.Single(storeRoomExits);
        Assert.Equal(X(Direction.N, Direction.S), shown);
        Assert.Equal(X(Direction.S), tracker.State.ObservedExitDirections);
    }

    // No second step: the store room shown again, late enough to be no re-look,
    // is the arrival, as it always was.
    [Fact]
    public void NoEcho_WithNoSecondStep_TheNextDisplayIsTheArrival()
    {
        (RoomGraphManager graph, RoomTracker tracker, List<NameLearnedEvent> learned) = AtTheStair();

        tracker.NoteRoomObserved(new RoomObservation("Lodge Room", X(Direction.N, Direction.S)), T0.AddSeconds(2));

        Assert.Equal(new[] { new NameLearnedEvent(StoreRoom, "Lodge Room") }, learned);
        Assert.Equal((RoomConfidence.Confirmed, StoreRoom), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
        Assert.Equal("Vault", graph.GetRoom(Vault)!.Name);
    }

    // A second step, and a display that fits neither room it leads to: the held
    // display is not taken, and the store room keeps its name.
    [Fact]
    public void NoEcho_ADisplayThatIsNotTheNextStepsRoom_LeavesTheHeldDisplayUntaken()
    {
        (RoomGraphManager graph, RoomTracker tracker, List<NameLearnedEvent> learned) = AtTheStair();

        tracker.NoteMoveSent(Direction.N, T0.AddSeconds(3));
        tracker.NoteRoomObserved(new RoomObservation("Somewhere Else", X(Direction.E)), T0.AddSeconds(3.1));

        Assert.Empty(learned);
        Assert.Equal("Old Store", graph.GetRoom(StoreRoom)!.Name);
        Assert.NotEqual(StoreRoom, tracker.State.CurrentRoom?.Key);
    }

    // Two steps north from the entrance, typed together on a statline with no
    // echo, each room printing the name it bears. The entrance re-looked at once
    // is held; the landing then shows under its own name, and is its arrival, not
    // a sign that the held re-look was. Nothing is learned, and the hall is where
    // we end.
    [Fact]
    public void NoEcho_AReLookHeld_ThenTheRoomAheadUnderItsOwnName_IsNotTakenForTheArrival()
    {
        RoomKey hall = new(15, 879);
        RoomGraphManager graph = Map(stored: OwnName);
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Entrance);
        tracker.NoteRoomObserved(new RoomObservation(OwnName(Entrance), Shown(graph.GetRoom(Entrance)!)), T0);

        tracker.NoteMoveSent(Direction.N, T0.AddSeconds(1));
        tracker.NoteMoveSent(Direction.N, T0.AddSeconds(1.05));
        tracker.NoteRoomObserved(new RoomObservation(OwnName(Entrance), Shown(graph.GetRoom(Entrance)!)), T0.AddSeconds(1.1));
        tracker.NoteRoomObserved(new RoomObservation(OwnName(Landing), Shown(graph.GetRoom(Landing)!)), T0.AddSeconds(1.6));
        tracker.NoteRoomObserved(new RoomObservation(OwnName(hall), Shown(graph.GetRoom(hall)!)), T0.AddSeconds(2.1));

        Assert.Empty(learned);
        Assert.Equal((RoomConfidence.Confirmed, hall), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
    }

    // The same, the board having renamed the landing and the hall: the landing's
    // display under its new name is its own arrival, not the hall's, so the held
    // re-look of the entrance is not taken for the landing. Each room is learned
    // from its own display, and the hall is where we end.
    [Fact]
    public void NoEcho_ReLookHeld_ThenTheRoomAheadRenamed()
    {
        RoomKey hall = new(15, 879);
        RoomGraphManager graph = Map(stored: OwnName);
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Entrance);
        tracker.NoteRoomObserved(new RoomObservation(OwnName(Entrance), Shown(graph.GetRoom(Entrance)!)), T0);

        tracker.NoteMoveSent(Direction.N, T0.AddSeconds(1));
        tracker.NoteMoveSent(Direction.N, T0.AddSeconds(1.05));
        tracker.NoteRoomObserved(new RoomObservation(OwnName(Entrance), Shown(graph.GetRoom(Entrance)!)), T0.AddSeconds(1.1));
        tracker.NoteRoomObserved(new RoomObservation(OwnNewName(Landing), Shown(graph.GetRoom(Landing)!)), T0.AddSeconds(1.6));
        tracker.NoteRoomObserved(new RoomObservation(OwnNewName(hall), Shown(graph.GetRoom(hall)!)), T0.AddSeconds(2.1));

        Assert.Equal(new[]
        {
            new NameLearnedEvent(Landing, OwnNewName(Landing)),
            new NameLearnedEvent(hall, OwnNewName(hall)),
        }, learned);
        Assert.Equal((RoomConfidence.Confirmed, hall), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
    }

    // The same two steps on a statline that echoes: a re-look of the entrance
    // before the echo is never held for later, so the landing's echoed arrival
    // names the landing.
    [Fact]
    public void WithAnEcho_AReLookBeforeTheEcho_IsNotHeldForLater()
    {
        RoomKey hall = new(15, 879);
        RoomGraphManager graph = Map(stored: OwnName);
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Street);
        tracker.NoteMoveSent(Direction.N, T0);
        tracker.NoteInboundMoveEcho("n", T0.AddSeconds(0.05));
        tracker.NoteRoomObserved(new RoomObservation(OwnNewName(Entrance), Shown(graph.GetRoom(Entrance)!)), T0.AddSeconds(0.1));
        learned.Clear();

        tracker.NoteMoveSent(Direction.N, T0.AddSeconds(1));
        tracker.NoteMoveSent(Direction.N, T0.AddSeconds(1.05));
        tracker.NoteRoomObserved(new RoomObservation(OwnNewName(Entrance), Shown(graph.GetRoom(Entrance)!)), T0.AddSeconds(1.1));
        Assert.Null(tracker.HeldDisplay);
        tracker.NoteInboundMoveEcho("n", T0.AddSeconds(1.5));
        tracker.NoteRoomObserved(new RoomObservation(OwnNewName(Landing), Shown(graph.GetRoom(Landing)!)), T0.AddSeconds(1.6));

        Assert.Equal(new[] { new NameLearnedEvent(Landing, OwnNewName(Landing)) }, learned);
        Assert.Equal(Landing, tracker.State.CurrentRoom!.Key);
        Assert.Equal(OwnName(hall), graph.GetRoom(hall)!.Name);
    }

    // With an echo, the entrance re-looked under the name it bears and was shown
    // with, before the step north into a landing never seen, is the entrance's
    // display: the landing is not named from it.
    [Fact]
    public void WithAnEcho_AReLookOfTheRoomBeingLeft_DoesNotNameANamelessRoomAhead()
    {
        RoomGraphManager graph = Map();
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Street);
        tracker.NoteMoveSent(Direction.N, T0);
        tracker.NoteInboundMoveEcho("n", T0.AddSeconds(0.05));
        tracker.NoteRoomObserved(new RoomObservation("The Front Hall", Shown(graph.GetRoom(Entrance)!)), T0.AddSeconds(0.1));
        learned.Clear();

        tracker.NoteMoveSent(Direction.N, T0.AddSeconds(1));
        tracker.NoteRoomObserved(new RoomObservation("The Front Hall", Shown(graph.GetRoom(Entrance)!)), T0.AddSeconds(1.1));

        Assert.Empty(learned);
        Assert.Equal((RoomConfidence.Pending, Entrance), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
    }

    // A follow drag is only shown on arrival: the house room the drag reaches
    // takes the name it prints, though the guard post beside the street could
    // have shown the same display.
    [Fact]
    public void AFollowDrag_IsTheArrival_ThoughANamedNeighbourCouldHaveShownIt()
    {
        RoomGraphManager graph = Map(stored: OwnName);
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Street);

        tracker.NoteFollowMove(Direction.N);
        tracker.NoteRoomObserved(new RoomObservation("Guard Post", X(Direction.E)));

        Assert.Equal(new[] { new NameLearnedEvent(Entrance, "Guard Post") }, learned);
        Assert.Equal((RoomConfidence.Confirmed, Entrance), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
    }

    // Put in the entrance by hand, never shown it. A display under another name
    // that fits only the entrance, after the step's echo or on a follow drag, is
    // no re-look: the step went somewhere the map didn't expect, and the tracker
    // is unsure, as before.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADisplayFittingOnlyTheUnshownRoomBeingLeft_AfterTheEchoOrOnADrag_LeavesTheTrackerUnsure(bool drag)
    {
        RoomGraphManager graph = Map(stored: OwnName);
        (RoomTracker tracker, _) = Track(graph, Entrance);

        if (drag) tracker.NoteFollowMove(Direction.E);
        else
        {
            tracker.NoteInboundMoveEcho("stat");
            tracker.NoteMoveSent(Direction.E);
            tracker.NoteInboundMoveEcho("e");
        }
        tracker.NoteRoomObserved(new RoomObservation("The Front Hall", Shown(graph.GetRoom(Entrance)!)));

        Assert.Equal(RoomConfidence.Suspect, tracker.State.Confidence);
    }

    // No echo. From the yard, a step north to the gate room; the yard's display
    // with only its way north comes back too soon, and the other yard beside it,
    // whose way back is hidden, could have shown it too. It is held. The step on
    // to the tower shows the tower: the held display is not taken for the gate
    // room, since the yard beside could have been the one shown.
    [Fact]
    public void NoEcho_AHeldDisplayANamedNeighbourCouldHaveShown_DoesNotNameTheRoomAhead()
    {
        RoomGraphManager graph = Map();
        graph.ApplyStoredNames(new Dictionary<RoomKey, string> { [GateRoom] = "Old Gate" });
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, Yard);
        tracker.NoteRoomObserved(new RoomObservation("Yard", X(Direction.N, Direction.E)), T0);

        tracker.NoteMoveSent(Direction.N, T0.AddSeconds(1));
        tracker.NoteRoomObserved(new RoomObservation("Yard", X(Direction.N)), T0.AddSeconds(1.1));
        Assert.NotNull(tracker.HeldDisplay);
        tracker.NoteMoveSent(Direction.N, T0.AddSeconds(3));
        tracker.NoteRoomObserved(new RoomObservation("Tower", X(Direction.S)), T0.AddSeconds(3.1));

        Assert.DoesNotContain(new NameLearnedEvent(GateRoom, "Yard"), learned);
    }

    // Put in the yard, which the data names, without its display. The step north
    // into the gate room is in flight when the yard redisplays: its exits are all
    // the gate room's. On a statline that echoes, before the echo it is the
    // yard's display; with none, the gate room, bearing a name, is not named
    // from a display the yard could have shown. The arrival names it.
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AReLookOfANamedRoomBeingLeft_NotShownSince_DoesNotNameTheRoomAhead(bool echoes, bool gateSeenBefore)
    {
        RoomGraphManager graph = Map();
        if (gateSeenBefore) graph.ApplyStoredNames(new Dictionary<RoomKey, string> { [GateRoom] = "Old Gate" });
        (RoomTracker tracker, List<NameLearnedEvent> learned) = PutInTheYardByHand(graph);
        if (echoes) tracker.NoteInboundMoveEcho("stat", T0);

        tracker.NoteMoveSent(Direction.N, T0.AddSeconds(1));
        tracker.NoteRoomObserved(new RoomObservation("Yard", X(Direction.N, Direction.E)), T0.AddSeconds(1.1));

        Assert.Empty(learned);
        Assert.Equal((RoomConfidence.Pending, Yard), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));

        if (echoes) tracker.NoteInboundMoveEcho("n", T0.AddSeconds(1.5));
        tracker.NoteRoomObserved(new RoomObservation("Gatehouse", X(Direction.N, Direction.S, Direction.E)), T0.AddSeconds(1.6));

        Assert.Equal(new[] { new NameLearnedEvent(GateRoom, "Gatehouse") }, learned);
        Assert.Equal((RoomConfidence.Confirmed, GateRoom), (tracker.State.Confidence, tracker.State.CurrentRoom!.Key));
    }

    // The last room shown was the tower, and then we were put in the yard by hand.
    private static (RoomTracker, List<NameLearnedEvent>) PutInTheYardByHand(RoomGraphManager graph)
    {
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, new RoomKey(3, 4));
        tracker.NoteRoomObserved(new RoomObservation("Tower", X(Direction.S)), T0.AddSeconds(-2));
        tracker.SetLocated(Yard, T0.AddSeconds(-1));
        return (tracker, learned);
    }

    // No echo: the yard's re-look, held, is not taken for the gate room when the
    // step after it shows the tower. The yard could have shown it.
    [Fact]
    public void NoEcho_AHeldReLookOfANamedRoomBeingLeft_IsNotTakenForTheRoomAhead()
    {
        RoomGraphManager graph = Map();
        graph.ApplyStoredNames(new Dictionary<RoomKey, string> { [GateRoom] = "Old Gate" });
        (RoomTracker tracker, List<NameLearnedEvent> learned) = PutInTheYardByHand(graph);

        tracker.NoteMoveSent(Direction.N, T0.AddSeconds(1));
        tracker.NoteRoomObserved(new RoomObservation("Yard", X(Direction.N, Direction.E)), T0.AddSeconds(1.1));
        Assert.NotNull(tracker.HeldDisplay);
        tracker.NoteMoveSent(Direction.N, T0.AddSeconds(3));
        tracker.NoteRoomObserved(new RoomObservation("Tower", X(Direction.S)), T0.AddSeconds(3.1));

        Assert.DoesNotContain(new NameLearnedEvent(GateRoom, "Yard"), learned);
    }

    // ----- a held display and a teleporting exit ----------------------------

    // The stair-and-store shape again, its rooms made up, with one of its two
    // steps through an exit whose spell sends the walker on to a landing.
    private RoomGraphManager CastCorridor(bool castOnFirstStep)
    {
        string first = castOnFirstStep ? "20/2 (Cast: pre-0, post-858)" : "20/2";
        string second = castOnFirstStep ? "20/3" : "20/3 (Cast: pre-0, post-858)";
        string set = Path.Combine(_root, "beta");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "Rooms.json"), $$"""
            [
              { "Map Number": 20, "Room Number": 1, "Name": null, "N": "{{first}}", "S": "20/9", "E": "0", "W": "0", {{Z}} },
              { "Map Number": 20, "Room Number": 2, "Name": null, "N": "{{second}}", "S": "20/1", "E": "0", "W": "0", {{Z}} },
              { "Map Number": 20, "Room Number": 3, "Name": null, "N": "0", "S": "20/2", "E": "0", "W": "0", {{Z}} },
              { "Map Number": 20, "Room Number": 4, "Name": "Landing", "N": "0", "S": "0", "E": "0", "W": "20/9", {{Z}} },
              { "Map Number": 20, "Room Number": 9, "Name": "Porch", "N": "20/1", "S": "0", "E": "20/4", "W": "0", {{Z}} }
            ]
            """);
        File.WriteAllText(Path.Combine(set, "TBInfo.json"),
            """[ { "Number": 2900, "LinkTo": 0, "Action": "teleport 4 20\n" } ]""");
        File.WriteAllText(Path.Combine(set, "Spells.json"),
            """[ { "Number": 858, "Name": "porch teleport", "Short": "", "Abil-0": 148, "AbilVal-0": 2900 } ]""");
        GameDataCache cache = new(_root);
        cache.SwitchSet("beta");
        TBInfoStore tbinfo = new(cache);
        tbinfo.OnActiveSetChanged("beta");
        RoomGraphManager graph = new(cache, log: null, tbinfo, new KnownSpellCatalog(cache));
        graph.OnActiveSetChanged("beta");
        graph.ApplyStoredNames(new Dictionary<RoomKey, string>
        {
            [new RoomKey(20, 1)] = "Lodge Room", [new RoomKey(20, 2)] = "Old Store", [new RoomKey(20, 3)] = "Vault",
        });
        return graph;
    }

    // The held display is not taken across a teleporting exit on either step:
    // the cast's own reading of the display settles where it leads.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoEcho_AHeldDisplay_IsNotTakenAcrossATeleportingExit(bool castOnFirstStep)
    {
        RoomGraphManager graph = CastCorridor(castOnFirstStep);
        RoomKey stair = new(20, 1), store = new(20, 2);
        Assert.NotNull(graph.GetRoom(castOnFirstStep ? stair : store)!.Exits[Direction.N].CastLandings);
        (RoomTracker tracker, List<NameLearnedEvent> learned) = Track(graph, stair);
        tracker.NoteRoomObserved(new RoomObservation("Lodge Room", X(Direction.N, Direction.S)), T0);

        tracker.NoteMoveSent(Direction.N, T0.AddSeconds(1));
        tracker.NoteRoomObserved(new RoomObservation("Lodge Room", X(Direction.N, Direction.S)), T0.AddSeconds(1.1));
        tracker.NoteMoveSent(Direction.N, T0.AddSeconds(3));
        tracker.NoteRoomObserved(new RoomObservation("Vault", X(Direction.S)), T0.AddSeconds(3.1));

        Assert.DoesNotContain(new NameLearnedEvent(store, "Lodge Room"), learned);
    }
}
