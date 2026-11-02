using System.IO;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Learning where the dead of a room arrive from a death the client sees: the room
// the map was sure of when the death came, and the room the first display after
// it showed, with no move sent in between. Run on the real RoomTracker over a
// small graph: an island across the water with its own shrine, two grottoes the
// map can't tell apart, Darkwood Forest and the Temple, an arena, Kingsport and
// the Burial Vault.
public sealed class ArrivalRoomLearnerTests : IDisposable
{
    private const string Rooms = """
        [
          { "Map Number": 14, "Room Number": 1813, "Name": "Terra Ista, Landing",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "14/1814", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 14, "Room Number": 1814, "Name": "Terra Ista, Path",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "14/1815", "S": "14/1813", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 14, "Room Number": 1815, "Name": "Terra Ista, Clearing",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "14/1814", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 14, "Room Number": 1900, "Name": "Isle Shrine",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "14/1814", "D": "0" },
          { "Map Number": 14, "Room Number": 1901, "Name": "Dark Grotto",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "14/1814", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 14, "Room Number": 1902, "Name": "Dark Grotto",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "14/1813", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 109, "Name": "Darkwood Forest",
            "Light": -150, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/116", "E": "0", "W": "0",
            "NE": "1/110", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2189, "Name": "Temple, Halls of the Dead",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "1/527", "D": "0" },
          { "Map Number": 16, "Room Number": 500, "Name": "Training Grounds",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "16/501", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 14, "Room Number": 5000, "Name": "Kingsport, Market",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "14/5001", "W": "14/5002 (Text: go alley, enter alley)",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 17, "Room Number": 3250, "Name": "Vaulted Hall",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "17/3247", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 17, "Room Number": 3247, "Name": "Grand Chamber",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "17/3250", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 14, "Room Number": 1077, "Name": "Burial Vault",
            "Light": -50, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "14/1072", "D": "0" }
        ]
        """;

    private static readonly RoomKey Landing = new(14, 1813);
    private static readonly RoomKey Shrine = new(14, 1900);
    private static readonly RoomKey Darkwood = new(1, 109);
    private static readonly RoomKey Temple = new(1, 2189);
    private static readonly RoomKey Kingsport = new(14, 5000);
    private static readonly RoomKey Vault = new(14, 1077);

    private static readonly RoomObservation ShrineShown = new("Isle Shrine", new HashSet<Direction> { Direction.U });
    private static readonly RoomObservation TempleShown = new("Temple, Halls of the Dead", new HashSet<Direction> { Direction.U });
    private static readonly RoomObservation VaultShown = new("Burial Vault", new HashSet<Direction> { Direction.U });
    private static readonly RoomObservation GrottoShown = new("Dark Grotto", new HashSet<Direction> { Direction.E });

    private static readonly RoomObservation LandingShown = new("Terra Ista, Landing", new HashSet<Direction> { Direction.N });

    private readonly string _root;
    private readonly RoomGraphManager _graph;
    private readonly RoomTracker _tracker;
    private readonly LearnedArrivalRoomStore _store;
    private readonly LearnedArrivalLookup _lookup;
    private readonly ArrivalRoomLearner _learner;
    private readonly List<LogEntry> _log = new();
    // What the game data says of the list; a board marked for edited rooms is
    // unverified whatever its set, as the app has it.
    private HangupArrivalListTrust _trust = HangupArrivalListTrust.Paradigm;
    private HangupArrivalListTrust Trust =>
        _trust == HangupArrivalListTrust.VanillaStock && _board.Marked ? HangupArrivalListTrust.Unverified : _trust;
    private DateTimeOffset _now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    // The board's count of deaths the standard list doesn't allow, kept in the
    // board's file and read afresh from it, as the client reads the active board.
    private readonly string _boardName = "!mudplay-test-arrivals-" + Path.GetRandomFileName();
    private readonly StandardListContradictions _board;
    private int BoardContradictions => _board.Standing().Count;

    private int Warnings => _log.Count(e => e.Severity == LogSeverity.Warn);

    public ArrivalRoomLearnerTests()
    {
        ScratchRoot.Require();
        _root = Path.Combine(Path.GetTempPath(), "mudplay-arrivallearner-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), Rooms);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = _graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        _tracker = new RoomTracker(graph);

        LogService log = new();
        log.EntryAdded += _log.Add;
        _store = new LearnedArrivalRoomStore(log, set => Path.Combine(_root, set, "learned-arrival-rooms.json"));
        _store.OnActiveSetChanged("alpha");
        _lookup = new LearnedArrivalLookup(_store,
            room => WalkReach.KnotOnItsMap(graph, room), (from, room) => WalkReach.OnItsMap(graph, from, room));
        BbsProfileStore bbsStore = new();
        bbsStore.Save(new MudPlay.Models.Settings.BbsProfile { Name = _boardName, Host = "stock.example", Port = 23 });
        _board = new StandardListContradictions(
            () => bbsStore.Get(_boardName) is { } bbs && bbs.RealmFor(null) is { } realm ? (bbs, realm) : null,
            () => "alpha", bbsStore.Save, log);
        _learner = new ArrivalRoomLearner(_tracker, _store, _lookup, () => Trust, graph.CouldBeTakenFor,
            inAlbion: room => room == Kingsport || room == Vault,
            isExitCommandSomewhere: graph.IsExitCommandSomewhere,
            boardContradicted: _board.Note,
            now: () => _now, log: log);
        // As AppServices wires it.
        _tracker.PlayerDeathObserved += _learner.OnPlayerDied;
        _tracker.StateChanged += _learner.OnRoomStateChanged;
    }

    private void Type(string line) => _learner.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes(line + "\r"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
        try { new BbsProfileStore().Delete(_boardName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort cleanup of a scratch board */ }
    }

    // The map is sure of the room, and the death lines are read there.
    private void DieIn(RoomKey room, bool arena = false)
    {
        _tracker.SetLocated(room);
        _tracker.NoteDeath(livesRemaining: 6, tookNothing: arena);
    }

    [Fact]
    public void AWitnessedDeath_TeachesWhereThatRoomsDeadArrive()
    {
        DieIn(Landing);
        Assert.Null(_store.ArrivalsFor(Landing));

        _tracker.NoteRoomObserved(ShrineShown);

        Assert.Equal(new[] { Shrine }, _store.ArrivalsFor(Landing));
        Assert.Contains(_log, e => e.Severity == LogSeverity.Info
            && e.Message == "learned: the dead of 14/1813 arrive in 14/1900");
        // That room only: the path beside it has taught nothing.
        Assert.Null(_store.ArrivalsFor(new RoomKey(14, 1814)));
        Assert.Equal(1, _store.Count);
    }

    // Only the first display after the death counts, and only with no move sent
    // before it: after either, the character may stand somewhere else.
    [Fact]
    public void AMoveSentBeforeTheRoomWasDrawn_TeachesNothing()
    {
        DieIn(Landing);
        _learner.NoteMoveSent();

        _tracker.NoteRoomObserved(ShrineShown);

        Assert.Equal(0, _store.Count);
    }

    // Two grottoes look the same, so the map can't say which the dead arrived in. One
    // stands in for both: whatever reads the lesson takes either for the other.
    [Fact]
    public void ARoomTheMapCantTellFromItsTwin_TeachesTheTwin_AndALaterDisplayIsNotTriedInstead()
    {
        DieIn(Landing);

        _tracker.NoteRoomObserved(GrottoShown);
        Assert.NotEqual(RoomConfidence.Confirmed, _tracker.State.Confidence);
        RoomKey learned = Assert.Single(_store.ArrivalsFor(Landing)!);
        Assert.Contains(learned, new[] { new RoomKey(14, 1901), new RoomKey(14, 1902) });

        // The next display is of a room it can place, and is not the first.
        _tracker.NoteRoomObserved(ShrineShown);
        Assert.Equal(RoomConfidence.Confirmed, _tracker.State.Confidence);
        Assert.Equal(1, _store.Count);
        Assert.Equal(new[] { learned }, _store.ArrivalsFor(Landing));
    }

    // An arena death is the arena's own rule: where it puts the character says
    // nothing of a death that costs a life.
    [Fact]
    public void AnArenaDeath_TeachesNothing()
    {
        _trust = HangupArrivalListTrust.Unverified;
        DieIn(new RoomKey(16, 500), arena: true);

        _tracker.NoteRoomObserved(ShrineShown);

        Assert.Equal(0, _store.Count);
    }

    // A move was in flight when the death came: the room the map held is only
    // where the character was last known to be.
    [Fact]
    public void ADeathInARoomTheMapWasNotSureOf_TeachesNothing()
    {
        _tracker.SetLocated(Landing);
        _tracker.NoteMoveSent(Direction.N);
        Assert.NotEqual(RoomConfidence.Confirmed, _tracker.State.Confidence);
        _tracker.NoteDeath(livesRemaining: 6);
        Assert.False(_tracker.LastDeathRoomConfirmed);

        _tracker.NoteRoomObserved(ShrineShown);

        Assert.Equal(0, _store.Count);
    }

    // Out of the game before the room was drawn: the room the next entry shows is
    // not known to be the one the death left the character in.
    [Fact]
    public void LeavingTheGameBeforeTheRoomWasDrawn_TeachesNothing()
    {
        DieIn(Landing);
        _learner.Forget();

        _tracker.NoteRoomObserved(ShrineShown);

        Assert.Equal(0, _store.Count);
    }

    // ----- The display of the room died in --------------------------------
    //
    // The death lines print no room, so the first display afterwards is whatever
    // was asked for; one asked for while the death was being carried out is of the
    // room died in. That is not where the dead arrive.

    [Fact]
    public void ADisplayOfTheRoomDiedIn_IsNotTheArrival_AndTheNextDisplayIsWaitedFor()
    {
        DieIn(Landing);

        _tracker.NoteRoomObserved(LandingShown);
        Assert.Equal(Landing, _tracker.State.CurrentRoom?.Key);
        Assert.Equal(0, _store.Count);
        Assert.DoesNotContain(_log, e => e.Message.StartsWith("learned:"));

        _tracker.NoteRoomObserved(ShrineShown);
        Assert.Equal(new[] { Shrine }, _store.ArrivalsFor(Landing));
    }

    // The wait is short: a display that comes long after the death is not taken
    // for the room it left the character in.
    [Fact]
    public void ADisplayLongAfterTheDeath_TeachesNothing()
    {
        DieIn(Landing);
        _tracker.NoteRoomObserved(LandingShown);

        _now += ArrivalRoomLearner.Window + TimeSpan.FromSeconds(1);
        _tracker.NoteRoomObserved(ShrineShown);

        Assert.Equal(0, _store.Count);
    }

    [Fact]
    public void ADisplayJustInsideTheWait_StillTeaches()
    {
        DieIn(Landing);
        _now += ArrivalRoomLearner.Window;
        _tracker.NoteRoomObserved(ShrineShown);

        Assert.Equal(1, _store.Count);
    }

    // A room command moves the character and is neither a direction nor a `go`
    // the movement observer knows of here: with no room known after a death, a
    // line that is an exit command of some room ends the wait. Other lines don't.
    [Theory]
    [InlineData("go alley", false)]
    [InlineData("ENTER ALLEY", false)]
    [InlineData("stat", true)]
    [InlineData("sys god someone add life", true)]
    [InlineData("", true)]
    public void ARoomCommandSentBeforeTheRoomWasDrawn_TeachesNothing(string line, bool learned)
    {
        DieIn(Landing);
        Type(line);

        _tracker.NoteRoomObserved(ShrineShown);

        Assert.Equal(learned ? 1 : 0, _store.Count);
    }

    // A display that settles a move the map knew of is where that move led, not
    // where the death put the character: the room died in was drawn (the death
    // hadn't moved it yet, or never did), the party leader dragged it one room
    // north, and the path is drawn. Nothing is learned, then or from what follows.
    [Fact]
    public void ADisplayThatSettlesADrag_TeachesNothing()
    {
        DieIn(Landing);
        _tracker.NoteRoomObserved(LandingShown);
        _tracker.NoteFollowMove(Direction.N);
        Assert.Equal(RoomConfidence.Pending, _tracker.State.Confidence);

        _tracker.NoteRoomObserved(new RoomObservation("Terra Ista, Path", new HashSet<Direction> { Direction.N, Direction.S }));

        Assert.Equal(new RoomKey(14, 1814), _tracker.State.CurrentRoom?.Key);
        Assert.Equal(RoomConfidence.Confirmed, _tracker.State.Confidence);
        Assert.Equal(0, _store.Count);
        _tracker.NoteRoomObserved(ShrineShown);
        Assert.Equal(0, _store.Count);
    }

    // The same told by the transition alone: from Pending nothing is learned,
    // from PendingRespawn or from the room died in it is.
    [Theory]
    [InlineData(RoomConfidence.Pending, true, false)]
    [InlineData(RoomConfidence.Suspect, true, false)]
    [InlineData(RoomConfidence.Confirmed, false, false)]   // sure of some other room
    [InlineData(RoomConfidence.Confirmed, true, true)]
    [InlineData(RoomConfidence.PendingRespawn, false, true)]
    public void OnlyADisplayTheMapWasWaitingOnTheDeathFor_Teaches(RoomConfidence before, bool inTheRoomDiedIn, bool learned)
    {
        DieIn(Landing);
        Room? previous = before == RoomConfidence.PendingRespawn ? null
            : _graph.GetRoom(inTheRoomDiedIn ? Landing : Kingsport);

        _learner.OnRoomStateChanged(new RoomTransition(
            before, RoomConfidence.Confirmed, previous, _graph.GetRoom(Shrine), _now, Displayed: true));

        Assert.Equal(learned ? 1 : 0, _store.Count);
    }

    // ----- Where the list is the world's own --------------------------------

    // A zone Paradigm shares with Stock: a death that ends where the list says
    // is no news; one that doesn't is said, and the list stands.
    [Fact]
    public void OnParadigmsSharedZones_NothingIsLearned_AndAContradictionIsSaid()
    {
        DieIn(Darkwood);
        _tracker.NoteRoomObserved(TempleShown);
        Assert.Equal(0, _store.Count);
        Assert.Equal(0, Warnings);

        DieIn(Darkwood);
        _tracker.NoteRoomObserved(ShrineShown);
        Assert.Equal(0, _store.Count);
        LogEntry warning = Assert.Single(_log, e => e.Severity == LogSeverity.Warn);
        Assert.Contains("A death seen in 1/109 ended in 14/1900", warning.Message);
        Assert.Contains("1/2189 or 1/142", warning.Message);
        Assert.Contains("Not learned", warning.Message);
        Assert.Equal(0, BoardContradictions);
    }

    // The list is of where a HANG-UP's dead arrive. For a room with a leave-the-
    // game room that is not where a death ends: 17/3250's dead by a hang-up
    // arrive in the Temple, and a death seen there ends in its own graveyard,
    // 17/3247. That is the list agreeing, not a finding, and nothing is said.
    [Theory]
    [InlineData(HangupArrivalListTrust.VanillaStock)]
    [InlineData(HangupArrivalListTrust.Paradigm)]
    public void ADeathInARoomWithALeaveTheGameRoom_EndingInItsOwnGraveyard_IsNoNewsAndNoWarning(HangupArrivalListTrust trust)
    {
        _trust = trust;
        DieIn(new RoomKey(17, 3250));

        _tracker.NoteRoomObserved(new RoomObservation("Grand Chamber", new HashSet<Direction> { Direction.N }));

        Assert.Equal(new RoomKey(17, 3247), _tracker.State.CurrentRoom?.Key);
        Assert.Equal(0, _store.Count);
        Assert.Equal(0, Warnings);
        Assert.Equal(0, BoardContradictions);
    }

    // An unmodified Stock set, and a death that ends in a room the list doesn't
    // allow: the board's rooms were edited while it runs on the standard data, or
    // the map had the wrong room for the death. One such death is said and
    // changes nothing. The second marks the board: from then on it is a board
    // with edited rooms, and the reading is learned, and read.
    [Fact]
    public void OnAStandardSet_TwoDeathsTheListDoesNotAllow_MarkTheBoardAsEdited_AndTheSecondIsLearned()
    {
        _trust = HangupArrivalListTrust.VanillaStock;

        DieIn(Darkwood);
        _tracker.NoteRoomObserved(TempleShown);
        Assert.Equal(0, BoardContradictions);
        Assert.Equal(0, _store.Count);

        DieIn(Darkwood);
        _tracker.NoteRoomObserved(ShrineShown);

        Assert.Equal(1, BoardContradictions);
        Assert.Equal(HangupArrivalListTrust.VanillaStock, Trust);
        Assert.Equal(0, _store.Count);
        LogEntry first = Assert.Single(_log, e => e.Severity == LogSeverity.Warn);
        Assert.Contains("A death seen in 1/109 ended in 14/1900, where the standard list has the dead of that room arrive in 1/2189 or 1/142",
            first.Message);
        Assert.Contains("Nothing changes yet: 1 more such death(s)", first.Message);

        DieIn(Darkwood);
        _tracker.NoteRoomObserved(ShrineShown);

        Assert.Equal(2, BoardContradictions);
        Assert.Equal(HangupArrivalListTrust.Unverified, Trust);
        Assert.Equal(new[] { Shrine }, _store.ArrivalsFor(Darkwood));
        // One line a death: the board's own, which names the rooms.
        Assert.Equal(2, Warnings);
        Assert.Contains("That is 2 such deaths on this board", _log.Last(e => e.Severity == LogSeverity.Warn).Message);

        // Marked, the board is not counted again.
        DieIn(Darkwood);
        _tracker.NoteRoomObserved(VaultShown);
        Assert.Equal(2, BoardContradictions);
    }

    // On a board already known for edited rooms, the list is only Stock's word:
    // a death that ends somewhere else is learned, and the board isn't counted
    // again.
    [Fact]
    public void OnAnEditedBoard_ADeathThatEndsSomewhereElseThanTheList_IsLearned()
    {
        _trust = HangupArrivalListTrust.Unverified;

        DieIn(Darkwood);
        _tracker.NoteRoomObserved(TempleShown);
        Assert.Equal(0, _store.Count);

        DieIn(Darkwood);
        _tracker.NoteRoomObserved(ShrineShown);
        Assert.Equal(new[] { Shrine }, _store.ArrivalsFor(Darkwood));
        Assert.Equal(0, BoardContradictions);
    }

    // ----- A room, its land, and Albion --------------------------------------

    // Albion's dead are known to arrive in the Burial Vault: seeing it adds
    // nothing. Seen to end somewhere else, that is recorded as that room's own,
    // and said.
    [Fact]
    public void AnAlbionDeath_EndingInTheBurialVault_IsNoNews_AndOneThatDoesNotIsRecorded()
    {
        DieIn(Kingsport);
        _tracker.NoteRoomObserved(VaultShown);
        Assert.Equal(0, _store.Count);
        Assert.Equal(0, Warnings);

        DieIn(Kingsport);
        _tracker.NoteRoomObserved(ShrineShown);
        Assert.Equal(new[] { Shrine }, _store.ArrivalsFor(Kingsport));
        Assert.Contains("A death seen in 14/5000 ended in 14/1900, not in 14/1077 as Albion had it",
            Assert.Single(_log, e => e.Severity == LogSeverity.Warn).Message);
    }

    // A death seen in one room stands for its land: the path beside the landing
    // takes the landing's reading. A death on the path that ends in the same room
    // is no surprise, and is kept as the path's own reading all the same.
    [Fact]
    public void ADeathSeenInOneRoom_StandsForItsLand()
    {
        RoomKey path = new(14, 1814);
        DieIn(Landing);
        _tracker.NoteRoomObserved(ShrineShown);

        Assert.Equal(new[] { new LearnedArrival(Shrine, Landing) }, _lookup.For(path));
        Assert.Equal(new[] { new LearnedArrival(Shrine, Landing) }, _lookup.For(Landing));
        // Another map's room, and a room the landing's land doesn't reach, take nothing.
        Assert.Null(_lookup.For(Darkwood));
        Assert.Null(_lookup.For(Kingsport));

        // A death on the path that ends as the land has it makes no entry for the
        // path: it is counted on the landing's reading, which goes on speaking
        // for the path, and the land's reading is now borne out by two deaths.
        Assert.False(_lookup.LandSays(path).Settled);
        DieIn(path);
        _tracker.NoteRoomObserved(ShrineShown);
        Assert.False(_store.HasReadingOf(path));
        Assert.Equal((1, 1), (_store.Count, _store.SeenNearby(Landing)));
        Assert.Equal(new[] { new LearnedArrival(Shrine, Landing) }, _lookup.For(path));
        Assert.True(_lookup.LandSays(path).Settled);
        Assert.Contains(_log, e => e.Severity == LogSeverity.Info
            && e.Message.StartsWith("as its land has it: the dead of 14/1814 arrive in 14/1900, like those of 14/1813"));
        Assert.Equal(0, Warnings);
        Assert.Empty(_lookup.AtOddsWithTheirLand());

        // A room with a reading of its own keeps getting its own counted.
        DieIn(Landing);
        _tracker.NoteRoomObserved(ShrineShown);
        Assert.Equal(1, _store.SeenNearby(Landing));
        Assert.Equal(1, _store.Count);
    }

    // Two readings in one land, one each: a death in a third room that ends as
    // one of them has it is counted there, and that reading becomes the land's.
    [Fact]
    public void ADeathThatBearsOutOneOfTwoReadings_MakesItTheLands()
    {
        RoomKey path = new(14, 1814), clearing = new(14, 1815);
        DieIn(Landing);
        _tracker.NoteRoomObserved(ShrineShown);
        DieIn(path);
        _tracker.NoteRoomObserved(VaultShown);
        Assert.False(_lookup.LandSays(Landing).Settled);

        DieIn(clearing);
        _tracker.NoteRoomObserved(VaultShown);

        Assert.False(_store.HasReadingOf(clearing));
        Assert.Equal(1, _store.SeenNearby(path));
        (IReadOnlyList<LearnedArrival>? land, bool settled) = _lookup.LandSays(Landing);
        Assert.True(settled);
        Assert.Equal(new[] { new LearnedArrival(Vault, path) }, land);
        // The landing is now the exception.
        (RoomKey room, RoomKey own, _, bool exception) = Assert.Single(_lookup.AtOddsWithTheirLand());
        Assert.Equal((Landing, Shrine, true), (room, own, exception));

        // A grotto that leads onto the landing, with nothing leading back to it,
        // is in none of those rooms' lands: a death there is its own reading.
        RoomKey grotto = new(14, 1902);
        DieIn(grotto);
        _tracker.NoteRoomObserved(VaultShown);
        Assert.Equal(new[] { Vault }, _store.ArrivalsFor(grotto));
    }

    // A death in a room of that land that ends somewhere else is "definitely
    // recorded": it is that room's own reading, said in the log, and the land
    // keeps its reading for its other rooms.
    [Fact]
    public void ADeathThatEndsElsewhereThanItsLandSays_IsRecordedAsThatRoomsOwn()
    {
        RoomKey path = new(14, 1814);
        DieIn(Landing);
        _tracker.NoteRoomObserved(ShrineShown);

        DieIn(path);
        _tracker.NoteRoomObserved(VaultShown);

        Assert.Equal(new[] { new LearnedArrival(Vault, path) }, _lookup.For(path));
        Assert.Equal(new[] { new LearnedArrival(Shrine, Landing) }, _lookup.For(Landing));
        Assert.StartsWith("A death seen in 14/1814 ended in 14/1077, not in 14/1900 as a death seen in 14/1813, a room of the same land, had it.",
            Assert.Single(_log, e => e.Severity == LogSeverity.Warn).Message);
        // Two rooms, two readings, neither held by more: the land has two.
        Assert.All(_lookup.AtOddsWithTheirLand(), o => Assert.False(o.Exception));
        Assert.Equal(2, _lookup.AtOddsWithTheirLand().Count);
    }

    // A second death in the same room that ends somewhere else: the room becomes
    // disputed, and neither reading is used until one is seen again.
    [Fact]
    public void ASecondDeathThatEndsSomewhereElse_DisputesTheRoom_UntilOneIsSeenAgain()
    {
        DieIn(Landing);
        _tracker.NoteRoomObserved(ShrineShown);
        DieIn(Landing);
        _tracker.NoteRoomObserved(VaultShown);

        Assert.Null(_store.ArrivalsFor(Landing));
        Assert.Equal((0, 1), (_store.Count, _store.DisputedCount));
        Assert.Equal(1, Warnings);

        DieIn(Landing);
        _tracker.NoteRoomObserved(ShrineShown);
        Assert.Equal(new[] { Shrine }, _store.ArrivalsFor(Landing));
        Assert.Equal(1, Warnings);
    }
}
