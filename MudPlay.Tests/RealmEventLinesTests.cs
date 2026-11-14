using System.IO;
using MudPlay.Game.Combat;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// The set's Messages table rows of no spell that the client acts on (NMR 1.8.4): a
// monster's death text, a chase line (someone leaving through a command exit) and a
// monster entrance. Game output is fed as bytes through ServerFeed.
public sealed class RealmEventLinesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mudplay-realm-events-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
    }

    private static string Row(string name, string msg, string effects) =>
        $$"""{"ID":1,"Name":"{{name}}","Kind":"Other","Msg":"{{msg}}","EndMsg":"\u0000","Effects":"{{effects}}","Spell":0}""";

    // Stock v1.11p's own rows: Thrag's death text, a text two monsters share (named by
    // its words), a chase line and a monster entrance; and one text under two
    // monsters' names.
    private static readonly string Table = "[" + string.Join(",",
        Row("Thrag", "Thrag utters a deafening moan, and collapses.", "Death"),
        Row("collapses in a heap", "The goblin collapses in a heap.", "Death"),
        Row("orc", "The beast falls dead!", "Death"),
        Row("ogre", "The beast falls dead!", "Death"),
        Row("slips into the dark alley.", "{target} slips into the dark alley.", "Chase"),
        Row("A moss zombie rises from the ground!", "A moss zombie rises from the ground!", "CheckRoom"),
        """{"ID":1,"Name":"blades","Kind":"Self","Msg":"As you walk through the passage, blades sweep out and slice you!","EndMsg":"\u0000","Effects":"Damage,Trap","Spell":0}""") + "]";

    private static RealmEventLines Lines() =>
        RealmEventLines.From(TestRows.Table(Table), name => name is "Thrag" or "orc" or "ogre");

    [Fact]
    public void TheTablesLines_AreReadByKind()
    {
        RealmEventLines lines = Lines();

        Assert.True(lines.IsDeath("Thrag utters a deafening moan, and collapses.", out string? thrag));
        Assert.Equal("Thrag", thrag);
        Assert.True(lines.IsDeath("The goblin collapses in a heap.", out string? shared));
        Assert.Null(shared);
        Assert.True(lines.IsDeath("The beast falls dead!", out string? twoMonsters));
        Assert.Null(twoMonsters);
        Assert.True(lines.IsChase("Ragnar slips into the dark alley.", out string who));
        Assert.Equal("Ragnar", who);
        Assert.True(lines.IsEntrance("A moss zombie rises from the ground!"));
        Assert.True(lines.IsTrap("As you walk through the passage, blades sweep out and slice you!"));
        Assert.False(lines.IsKnown("The goblin swings at you, but misses!"));
        Assert.Equal("from the set's Messages table: 3 death lines (1 naming their monster), 1 chase lines, 1 monster entrances, 1 trap lines",
            lines.Summary);
    }

    [Fact]
    public void ATableOfAnotherShape_GivesNoLines()
    {
        RealmEventLines lines = RealmEventLines.From(TestRows.Table("""[{"Number":1,"Text":"hello"}]"""), _ => true);

        Assert.False(lines.IsKnown("Thrag utters a deafening moan, and collapses."));
        Assert.StartsWith("none: the set's Messages table isn't one this client reads", lines.Summary, StringComparison.Ordinal);
    }

    // ----- Death text ----------------------------------------------------------------

    private static (ServerFeed Feed, List<MonsterDeathLine> Deaths) DeathWatch()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        RealmEventLines lines = Lines();
        MonsterDeathLineWatcher watcher = new(router, () => lines);
        List<MonsterDeathLine> deaths = [];
        watcher.DiedToAnother += deaths.Add;
        return (new ServerFeed(router), deaths);
    }

    // A death text with no exp line of ours before the prompt is another's kill, and
    // is that monster's death (user, 2026-11-14).
    [Fact]
    public void ADeathTextWithNoExpLineOfOurs_IsAnothersKill()
    {
        (ServerFeed feed, List<MonsterDeathLine> deaths) = DeathWatch();

        feed.Line("Thrag utters a deafening moan, and collapses.");
        feed.Prompt();

        MonsterDeathLine death = Assert.Single(deaths);
        Assert.Equal("Thrag", death.Monster);
    }

    // Our own kill prints its death text, then our exp line: MonsterDeathWatcher counts
    // that one, so nothing is raised here.
    [Fact]
    public void ADeathTextOurExpLineFollows_IsOurKill_AndRaisesNothing()
    {
        (ServerFeed feed, List<MonsterDeathLine> deaths) = DeathWatch();

        feed.Line("Thrag utters a deafening moan, and collapses.");
        feed.Line("You gain 120000 experience.");
        feed.Prompt();

        Assert.Empty(deaths);
    }

    [Fact]
    public void ADeathTextSeveralMonstersShare_NamesNoOne()
    {
        (ServerFeed feed, List<MonsterDeathLine> deaths) = DeathWatch();

        feed.Line("The goblin collapses in a heap.");
        feed.Prompt();

        Assert.Empty(deaths);
    }

    // Two deaths in one output: the first had its exp line's chance when the second came.
    [Fact]
    public void TwoDeathsBeforeOneExpLine_TheFirstIsAnothers()
    {
        (ServerFeed feed, List<MonsterDeathLine> deaths) = DeathWatch();

        feed.Line("Thrag utters a deafening moan, and collapses.");
        feed.Line("Thrag utters a deafening moan, and collapses.");
        feed.Line("You gain 120000 experience.");
        feed.Prompt();

        Assert.Single(deaths);
    }

    // ----- Monster entrance ----------------------------------------------------------

    [Fact]
    public void AMonsterEntrance_ReDisplaysTheRoom_AndTheRosterIsNoLongerWhole()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        using RoomEntityClassifier classifier = new(router, new MonsterMessageStore(),
            new PlayerDatabase(new ProfileService(), () => null), new LogService());
        RealmEventLines lines = Lines();
        List<string> refreshes = [];
        using MonsterEntranceWatcher watcher = new(router, classifier, () => lines,
            reason => { refreshes.Add(reason); return true; });
        ServerFeed feed = new(router);
        feed.Line("Also here: barrow wight.");
        Assert.True(classifier.RosterIsWhole);

        feed.Line("A moss zombie rises from the ground!");

        Assert.Equal("monster entrance 'A moss zombie rises from the ground!'", Assert.Single(refreshes));
        Assert.False(classifier.RosterIsWhole);
        Assert.Equal("a monster entrance: 'A moss zombie rises from the ground!'", classifier.RosterNotWholeBecause);
    }

    // ----- Chase line ----------------------------------------------------------------

    // 1/1 "Street" has a west text exit into 1/2 "Dark Alley" and a north step into
    // 1/3, another "Dark Alley" with the same exits: only the move taken tells them apart.
    // 1/4 "Crossroads" has two text exits.
    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Street",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/3", "S": "0", "E": "0", "W": "1/2 (Text: go alley, enter alley)",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Dark Alley",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Dark Alley",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Crossroads",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/2 (Text: go alley)", "S": "1/3 (Text: go lane)", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private (RoomTracker Tracker, ServerFeed Feed) Following(string leader, int room = 1)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        FollowMoveObserver observer = new(router, tracker);
        RealmEventLines lines = Lines();
        observer.SetChaseLines(() => lines, name => name == leader);
        tracker.SetLocated(new RoomKey(1, room));
        return (tracker, new ServerFeed(router));
    }

    // The game moves a follower through a command exit with its leader and prints no
    // follow line (user, 2026-11-14): the leader's chase line is the move.
    [Fact]
    public void OurLeadersChaseLine_IsAFollowMoveThroughTheRoomsCommandExit()
    {
        (RoomTracker tracker, ServerFeed feed) = Following("Ragnar");

        feed.Line("Ragnar slips into the dark alley.");
        Assert.Equal(RoomConfidence.Pending, tracker.State.Confidence);

        tracker.NoteRoomObserved(new RoomObservation("Dark Alley", new HashSet<Direction> { Direction.S }));
        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 2), tracker.State.CurrentRoom!.Key);
    }

    [Fact]
    public void AnotherPlayersChaseLine_MovesNoOne()
    {
        (RoomTracker tracker, ServerFeed feed) = Following("Ragnar");

        feed.Line("Bjorn slips into the dark alley.");

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), tracker.State.CurrentRoom!.Key);
    }

    // With two command exits there's no telling which the leader took: no move is
    // guessed, and the room display that follows places us.
    [Fact]
    public void InARoomWithTwoCommandExits_NoMoveIsGuessed()
    {
        (RoomTracker tracker, ServerFeed feed) = Following("Ragnar", room: 4);

        feed.Line("Ragnar slips into the dark alley.");

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
    }
}
