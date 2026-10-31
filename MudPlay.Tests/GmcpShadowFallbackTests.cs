using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Gmcp;
using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// Shadow as a fallback: the text is the source, and the server's data stands in only
// where the text hasn't answered (before its first reading of a kind) or can't (a map
// that would otherwise ask the game or give up as Lost, a monster with no max HP in
// the game data).
public sealed partial class GmcpIntegrationTests
{
    // Two rooms named alike, so a display of one can't say which: 1/1 leads north to
    // 1/2, and 1/2 back south. The handshake recording has the server put us in 1,1.
    private const string TwinSquares = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Town Square",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Town Square",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private const string InventoryText =
        "You are carrying 2 gold crowns, a torch\r\nYou have no keys.\r\nWealth: 200 copper farthings\r\nEncumbrance: 1/100 - None [1%]\r\n";

    // A walk the recovery gate drives, recording what it is told.
    private sealed class HeldWalk : IRecoverableEngine
    {
        public List<RoomKey> Resumes { get; } = new();
        public List<Direction> Backtracks { get; } = new();
        public string Name => "Walk";
        public RoomKey? JourneyOrigin => null;
        public Direction? PeekNextPlannedDirection() => null;
        public IReadOnlyList<Direction> PeekPlannedDirections(int count) => Array.Empty<Direction>();
        public void SendBacktrackMove(Direction direction) => Backtracks.Add(direction);
        public void PauseForRecovery(string reason) { }
        public void ResumeAfterRecovery(RoomKey recoveredAnchor) => Resumes.Add(recoveredAnchor);
        public void AbortFromRecoveryFailure(string detail) { }
    }

    // A stalled walk asks the game where it is. The server's room answers, as an `rm`
    // reply would, and the walk resumes from it with no walk-back.
    [Fact]
    public void ShadowRoom_PlacesAStalledWalk_AtTheServersRoom()
    {
        using Session session = new(active: false, rooms: TwinSquares);
        session.Replay("01-handshake");
        session.Tracker.SetLocated(new RoomKey(1, 2));
        EngineRecoveryGate gate = new(session.Graph, session.Tracker);
        GmcpPositionLocator locator = new(session.Gmcp, session.Tracker, gate, session.Timers.Arm, session.Log);
        gate.TryResync = locator.TryLocate;
        HeldWalk walk = new();
        gate.Attach(walk);

        gate.NoteEngineStalled("move never confirmed");
        Assert.True(gate.AwaitingAuthoritativeResync);
        session.Timers.RunAll();

        Assert.Equal(new RoomKey(1, 1), session.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(RoomConfidence.Confirmed, session.Tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), Assert.Single(walk.Resumes));
        Assert.Empty(walk.Backtracks);
        Assert.False(gate.AwaitingAuthoritativeResync);
    }

    // Room.Info Off, or a server room the map doesn't have, leaves the ask to the game.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShadowRoom_LeavesTheAskToTheGame_WhenOffOrNotOnTheMap(bool off)
    {
        using Session session = new(active: false, rooms: off ? TwinSquares : null);
        session.Replay("01-handshake");
        if (off) session.Gmcp.State.SetMode("Anomaly.Room.Info", GmcpMode.Off);
        GmcpPositionLocator locator = new(session.Gmcp, session.Tracker,
            new EngineRecoveryGate(session.Graph, session.Tracker), session.Timers.Arm);

        Assert.False(locator.TryLocate("lost"));
        Assert.Equal(0, session.Timers.Pending);
    }

    // A move sent after the ask, from the room the server has us in, hasn't landed:
    // the answer keeps it, as an `rm` answer naming the room a later move leaves does.
    [Fact]
    public void ShadowRoom_KeepsAMoveSentSinceTheAsk()
    {
        using Session session = new(active: false, rooms: TwinSquares);
        session.Replay("01-handshake");
        session.Tracker.SetLocated(new RoomKey(1, 1));
        GmcpPositionLocator locator = new(session.Gmcp, session.Tracker,
            new EngineRecoveryGate(session.Graph, session.Tracker), session.Timers.Arm);
        RoomKey? resolved = null;

        Assert.True(locator.TryLocateOnce("re-fix", key => resolved = key, () => { }));
        session.Tracker.NoteMoveSent(Direction.N);
        session.Timers.RunAll();

        Assert.Equal(new RoomKey(1, 1), resolved);
        Assert.True(session.Tracker.HasQueuedMoves);
    }

    // The server's room data comes only on a room change. Older than a move still
    // unanswered, it can't say where that move left us (the link died with the
    // answer on the way, report paradigm-20261031-051153), and the ask goes to the
    // game; the room data the move brings answers it.
    [Fact]
    public void ShadowRoom_OlderThanAMoveStillUnanswered_LeavesTheAskToTheGame()
    {
        using Session session = new(active: false, rooms: TwinSquares);
        session.Replay("01-handshake");
        session.Tracker.SetLocated(new RoomKey(1, 1));
        GmcpPositionLocator locator = new(session.Gmcp, session.Tracker,
            new EngineRecoveryGate(session.Graph, session.Tracker), session.Timers.Arm);
        session.Tracker.NoteMoveSent(Direction.N);

        Assert.False(locator.TryLocate("stall"));
        Assert.Equal(0, session.Timers.Pending);

        session.Send("Anomaly.Session.Reset", new { scope = "room", room = "1,2" });
        Dictionary<string, JsonElement> room = GmcpStateTests.Frames("01-handshake").First(f =>
            f.TryGetProperty("pkg", out JsonElement package) && package.GetString() == "Anomaly.Room.Info")
            .GetProperty("msg").EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        room["room"] = JsonSerializer.SerializeToElement("1,2");
        session.Send("Anomaly.Room.Info", room);
        RoomKey? resolved = null;

        Assert.True(locator.TryLocateOnce("stall", key => resolved = key, () => { }));
        session.Timers.RunAll();
        Assert.Equal(new RoomKey(1, 2), resolved);
    }

    // With no engine driving, a display no single room fits would leave the map Lost.
    // The server's room is taken there, when it is the room the display names.
    [Theory]
    [InlineData("Town Square", true)]
    [InlineData("Dark Alley", false)]
    public void ShadowRoom_IsTakenWhereTheDisplaysRunOut_WhenItIsTheRoomShown(string shown, bool placed)
    {
        using Session session = new(active: false, rooms: TwinSquares);
        session.Tracker.ServerRoom = name => session.Gmcp.ServerRoom(name);
        session.Replay("01-handshake");

        session.Text($"[HP=150]:\r\n{shown}\r\nObvious exits: east\r\n");

        Assert.Equal(placed ? new RoomKey(1, 1) : null, session.Tracker.State.CurrentRoom?.Key);
        if (placed) Assert.Equal(RoomConfidence.Confirmed, session.Tracker.State.Confidence);
    }

    // The server's stats and level stand in until the stat screen; the screen's own
    // reading then stands, and a later server snapshot is only compared.
    [Fact]
    public void ShadowStats_StandInUntilTheStatScreen()
    {
        using Session session = new(active: false);
        session.Replay("01-handshake");
        Assert.Equal(70, session.Stats.Strength);
        Assert.Equal(12, session.Stats.Level);

        session.Text("Name: Tester                  Lives/CP:           9/0\r\n"
            + "Strength:   60       Agility:   80          Tracking:     0\r\n[HP=150]:\r\n");
        Assert.Equal(60, session.Stats.Strength);
        session.Send("Anomaly.Char.Stats", session.Gmcp.State.Snapshot("Anomaly.Char.Stats")!.Value);

        Assert.Equal(60, session.Stats.Strength);
    }

    // The server's spell list stands in until the text's; the book is then handed
    // back to the text ahead of the text's write, so the text's list stands. Written
    // first, the text's list was composed over the server's held one, and the
    // server's names stayed learned. (This harness has no spell data, so a listed
    // spell resolves to nothing learned; what is pinned is that the server's don't
    // stay.)
    [Theory]
    [InlineData("You have no spells.\r\n")]
    [InlineData("You have the following spells:\r\nLevel Mana Short Spell Name\r\n  2    5  star starlight\r\n[HP=150]:\r\n")]
    public void ShadowSpells_StandInUntilTheTextsList_WhichThenStands(string textList)
    {
        using Session session = new(active: false);
        EnterMilestone2(session);
        JsonElement spells = GmcpMilestone2StateTests.Example("Anomaly.Spells.List");
        session.Gmcp.Receive(new("Anomaly.Spells.List", spells));
        Assert.Contains("blaze", session.Book.ObtainedNames);

        session.Text(textList);
        Assert.DoesNotContain("blaze", session.Book.ObtainedNames);
        Assert.Null(session.Book.ServerSpell(spells.GetProperty("spells")[0].GetProperty("id").GetInt32()));
        session.Gmcp.Receive(new("Anomaly.Spells.List", spells));

        Assert.DoesNotContain("blaze", session.Book.ObtainedNames);
    }

    // The server's inventory stands in until the text's `i`, and again in a new
    // session, which hasn't had the text's yet.
    [Fact]
    public void ShadowInventory_StandsInUntilTheTextsInventory_AndAgainInANewSession()
    {
        using Session session = new(active: false);
        EnterMilestone2(session);
        JsonElement items = GmcpMilestone2StateTests.Example("Anomaly.Items.List");
        session.Gmcp.Receive(new("Anomaly.Items.List", items));
        Assert.True(session.Inventory.IsLoaded);
        long server = session.Inventory.Snapshot.Currency.TotalCopperValue;
        Assert.NotEqual(200, server);

        session.Text(InventoryText);
        Assert.Equal(200, session.Inventory.Snapshot.Currency.TotalCopperValue);
        session.Gmcp.Receive(new("Anomaly.Items.List", items));
        Assert.Equal(200, session.Inventory.Snapshot.Currency.TotalCopperValue);

        session.Send("Anomaly.Session.Reset", new { scope = "all", reason = "enter" });
        session.Gmcp.Receive(new("Anomaly.Items.List", items));
        Assert.Equal(server, session.Inventory.Snapshot.Currency.TotalCopperValue);
    }

    // The server's party stands in, snapshot after snapshot, until the text's `par`;
    // the server's own writes don't count as that reading.
    [Fact]
    public void ShadowParty_StandsInUntilTheTextsPar()
    {
        using Session session = new(active: false);
        EnterMilestone2(session);
        session.Gmcp.Receive(new("Anomaly.Group.Members", GmcpMilestone2StateTests.Example("Anomaly.Group.Members")));
        session.Gmcp.Receive(new("Anomaly.Group.Members", GmcpMilestone2StateTests.Example("Anomaly.Group.Members.2")));
        Assert.Contains(session.Party.State.Members, m => m.HpPercent < 0);

        session.Text("The following people are in your travel party:\r\n  Gpriest (Priest) [M: 80%] [H:100%] - Frontrank\r\n[HP=150]:\r\n");
        Assert.DoesNotContain(session.Party.State.Members, m => m.HpPercent < 0);
        session.Gmcp.Receive(new("Anomaly.Group.Members", GmcpMilestone2StateTests.Example("Anomaly.Group.Members.2")));

        Assert.DoesNotContain(session.Party.State.Members, m => m.HpPercent < 0);
    }

    [Fact]
    public void OffPackages_StandInForNothing()
    {
        using Session session = new(active: false);
        session.Gmcp.State.SetMode("Anomaly.Items.List", GmcpMode.Off);
        EnterMilestone2(session);

        session.Gmcp.Receive(new("Anomaly.Items.List", GmcpMilestone2StateTests.Example("Anomaly.Items.List")));

        Assert.False(session.Inventory.IsLoaded);
    }

    // A monster the game data gives no max HP has no estimate, so the server's reading
    // stands in and follows its updates, which the status bar is told of. With a max
    // HP the text's estimate stands.
    [Theory]
    [InlineData(null, 12, 5, 1)]
    [InlineData(100, 100, 100, 0)]
    public void ShadowHp_StandsInForAMonsterWithNoMaxHp(int? dataMax, int first, int afterUpdate, int announced)
    {
        using Session session = new(active: false, monsterTable: """[{ "Number": 12, "Name": "giant rat" }]""",
            maxHp: _ => dataMax);
        session.Replay("07-recovery-request");
        session.Text("Also here: large giant rat.\r\n");
        Assert.Equal(first, session.Hp.Estimate("large giant rat"));
        int changes = 0;
        session.Hp.EstimateChanged += _ => changes++;

        session.Send("Anomaly.Mob.Update", new { uid = 102, room = "1,301", full = false, hp = 5 });

        Assert.Equal(afterUpdate, session.Hp.Estimate("large giant rat"));
        Assert.Equal(announced, changes);
    }
}
