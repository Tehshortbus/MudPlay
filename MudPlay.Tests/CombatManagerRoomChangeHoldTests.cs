using System.IO;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Attack Not Last and the follow Target Priorities hold only a room's opening: once
// the fight in a room is under way, a new pick there goes out at once. These tests pin
// which room the fight on record belongs to when we change rooms without a move of our
// own (a teleport, a drag with no drag line, a fear run, a death, a relog), driving
// the wire through the terminal, the line extractor, the router and the room tracker
// as the client runs them.
public sealed class CombatManagerRoomChangeHoldTests
{
    private const string Rooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Volcano Tunnel",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Lava Chamber",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Obsidian Hall",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "1/4", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Ash Pit",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "1/3",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 5, "Name": "Temple, Halls of the Dead",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "1/1", "D": "0" },
          { "Map Number": 1, "Room Number": 6, "Name": "Crater Floor",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "1/4" },
          { "Map Number": 1, "Room Number": 7, "Name": "Crater Floor",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "1/3" }
        ]
        """;

    private sealed class Harness : IDisposable
    {
        private readonly string _root;
        private readonly TerminalEmulator _terminal = new(80, 25);
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new();
        public PartyState Party { get; } = new();
        public LogService Log { get; } = new();
        public LineExtractor Lines { get; }
        public RoomTracker Tracker { get; }
        public RoomDisplayParser Display { get; }
        public DeathDetector Deaths { get; }
        public RoomEntityClassifier Classifier { get; }
        public CombatManager Combat { get; }
        public List<byte[]> Sent { get; } = new();
        public CombatSettings Settings { get; } = new()
        {
            NormalAttackCommand = "pu",
            TargetOrder = TargetOrder.Normal,
            AttackTiming = AttackTiming.AttackNotLast,
        };
        public bool Feared { get; set; }

        // The holds' in-round deadline: the projected round is a test value, and the
        // one-shot waits until the test fires it.
        public TimeSpan? ToRound { get; set; } = TimeSpan.FromSeconds(3);
        private Action? _deadline;
        public void FireDeadline() => _deadline?.Invoke();

        public Harness()
        {
            _root = Path.Combine(Path.GetTempPath(), "mudplay-roomchange-hold-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_root, "alpha"));
            File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), Rooms);
            GameDataCache cache = new(_root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");
            Tracker = new RoomTracker(graph);
            Tracker.SetFearProbe(() => Feared);

            // Wired in the order the client wires them: the router takes every line
            // first, then the room display parser reads the same lines.
            DefaultPatterns.Seed(Router);
            Lines = new LineExtractor(_terminal);
            Lines.LineEmitted += Router.Dispatch;
            Display = new RoomDisplayParser(Lines, Tracker, Log);
            Deaths = new DeathDetector(Tracker, Log);
            Deaths.AttachLineExtractor(Lines);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Tracker, Log);
            Classifier.AttachLineExtractor(Lines);
            Combat = new CombatManager(Router, Classifier, Monsters,
                resolveOverlay: _ => new MonsterOverlay(),
                party: Party,
                readSettings: () => Settings,
                isEnabled: () => true,
                readOwnGivenName: () => "MudPlay",
                post: a => a(),
                log: Log);
            Combat.SetWireSender(b => Sent.Add(b));
            Combat.SetFearGate(() => Feared);
            Combat.SetHoldDeadline(() => ToRound, (_, due) => _deadline = due);
            // As AppServices wires them.
            Tracker.StateChanged += t => Combat.NoteRoomTransition(t, Tracker.LastMoveSentAt);
            Tracker.PlayerDeathObserved += () => Combat.OnPlayerDeath();
            Tracker.PlayerDeathObserved += () => Classifier.NoteRoomChanged();

            Party.IsInParty = true;
            foreach (string n in new[] { "MudPlay", "Cidir NineteenTr", "Thresh II" })
                Party.Members.Add(new PartyMember { Name = n });
            Party.LeaderName = "Cidir NineteenTr";
            Players.RecordObservation("Cidir", "Priest", null, null, null, null, null, DateTime.UtcNow);
            Players.RecordObservation("Thresh", "Thief", null, null, null, null, null, DateTime.UtcNow);
            AddMonster(635, "tunnel horror");
            AddMonster(636, "giant skeleton");
            AddMonster(637, "cave bear");
        }

        private void AddMonster(int number, string name)
            => Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}", Name: name, Links: new[] { new GameDataLink("Monsters", number) }));

        public void Wire(params string[] lines)
            => _terminal.Feed(Encoding.Latin1.GetBytes(string.Concat(lines.Select(l => l + "\r\n"))));

        // A room display as the game prints it: the bright-cyan name, who is here,
        // and the exits line the tracker places the room on.
        public void ShowRoom(string name, string? alsoHere, string exits)
        {
            List<string> lines = new() { $"\u001b[1;36m{name}\u001b[0m" };
            if (alsoHere is not null) lines.Add($"Also here: {alsoHere}.");
            lines.Add($"Obvious exits: {exits}.");
            Wire(lines.ToArray());
        }

        // Our own step: the walker tells the tracker, and the outbound observer the
        // combat engine, before the move's bytes go out.
        public void SendMove(Direction direction)
        {
            Tracker.NoteMoveSent(direction);
            Combat.NoteMoveSent();
        }

        public IEnumerable<string> AllSent => Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'));
        public int Punches => AllSent.Count(s => s.StartsWith("pu "));
        public string LastSent => Sent.Count == 0 ? "" : Encoding.Latin1.GetString(Sent[^1]).TrimEnd('\r');

        public void Dispose()
        {
            Combat.Dispose();
            Classifier.Dispose();
            Deaths.Dispose();
            Display.Dispose();
            try { Directory.Delete(_root, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    // In the Volcano Tunnel the party opens on the tunnel horror, we attack behind
    // them, and it dies: the fight there is under way.
    private static void FightInTheTunnel(Harness h)
    {
        h.ShowRoom("Volcano Tunnel", "tunnel horror", "north");
        Assert.Equal(0, h.Punches);
        h.Wire("Thresh moves to attack tunnel horror.");
        Assert.Equal("pu tunnel horror", h.LastSent);
        h.Wire("*Combat Engaged*", "Thresh stabs tunnel horror for 50 damage!", "You gain 100 experience.");
        Assert.Equal(1, h.Punches);
    }

    [Fact]
    public void Teleport_AfterAFight_NextRoomHoldsThenTheDeadlineAttacks()
    {
        using Harness h = new();
        FightInTheTunnel(h);

        h.ShowRoom("Obsidian Hall", "giant skeleton", "east");     // no move of ours
        Assert.Equal(1, h.Punches);                                 // held for the party

        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
        Assert.Equal(2, h.Punches);
    }

    [Fact]
    public void Teleport_AfterAFight_PartyOpensTheNextRoom_WeAttackBehindThem()
    {
        using Harness h = new();
        FightInTheTunnel(h);

        h.ShowRoom("Obsidian Hall", "giant skeleton, Thresh", "east");
        h.Wire("Thresh moves to attack giant skeleton.");
        Assert.Equal("pu giant skeleton", h.LastSent);
        h.FireDeadline();
        Assert.Equal(2, h.Punches);
    }

    [Fact]
    public void DraggedThroughFoliage_NoDragLine_NextRoomHolds()
    {
        // A text exit with a pass message moves a follower with no
        // " -- Following your Party leader <dir> -- " line (GAME_MECHANICS
        // "Following the leader: follow / drag movement").
        using Harness h = new();
        FightInTheTunnel(h);

        h.Wire("You push through the dense foliage, and walk onto a small path.");
        h.ShowRoom("Ash Pit", "giant skeleton", "west");
        Assert.Equal(1, h.Punches);

        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
    }

    [Fact]
    public void FearRun_NoMoveSent_NextRoomHolds()
    {
        // Fear runs us through an obvious exit with no move of ours and no line for it
        // (GAME_MECHANICS "Fear"); attacks wait for it to wear off.
        using Harness h = new();
        FightInTheTunnel(h);

        h.Feared = true;
        h.ShowRoom("Lava Chamber", "giant skeleton", "south");
        Assert.Equal(new RoomKey(1, 2), h.Tracker.State.CurrentRoom!.Key);
        h.Feared = false;
        Assert.Equal(1, h.Punches);

        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
    }

    [Fact]
    public void DeathAndRespawn_TheRespawnRoomHolds()
    {
        using Harness h = new();
        FightInTheTunnel(h);

        h.Wire("You have been killed!", "But, due to a miracle, you have been saved.", "You have 7 lives left.");
        h.ShowRoom("Temple, Halls of the Dead", "giant skeleton", "up");
        Assert.Equal(1, h.Punches);

        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
    }

    [Fact]
    public void Relog_BackInTheSameRoom_HoldsForTheOpening()
    {
        // Out to the board's menu and back in: the fight on record is from before,
        // and the room shown on the way in is a new opening even though it is the same
        // room.
        using Harness h = new();
        FightInTheTunnel(h);

        h.Combat.NoteGameLeft();                                   // as AppServices does at the menu
        h.Classifier.NoteGameLeft();
        h.ShowRoom("Volcano Tunnel", "giant skeleton", "north");
        Assert.Equal(1, h.Punches);

        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
    }

    [Fact]
    public void Reconnect_BackInTheSameRoom_HoldsForTheOpening()
    {
        using Harness h = new();
        FightInTheTunnel(h);

        h.Combat.OnDisconnected();                                 // as the disconnect handler does
        h.Classifier.NoteGameLeft();
        h.ShowRoom("Volcano Tunnel", "giant skeleton", "north");
        Assert.Equal(1, h.Punches);

        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
    }

    [Fact]
    public void SameRoomShownAgain_FightUnderWay_PicksOnTheExitsLine()
    {
        // The room shown again after a kill is the room the fight is in: the next pick
        // goes out once the exits line places it, without waiting for the deadline.
        using Harness h = new();
        h.ShowRoom("Volcano Tunnel", "tunnel horror, giant skeleton", "north");
        h.Wire("Thresh moves to attack giant skeleton.");
        Assert.Equal("pu tunnel horror", h.LastSent);
        h.Wire("*Combat Engaged*", "You gain 100 experience.");     // our target dies

        h.Wire("\u001b[1;36mVolcano Tunnel\u001b[0m", "Also here: giant skeleton.");
        Assert.Equal(1, h.Punches);                                 // not yet placed
        h.Wire("Obvious exits: north.");
        Assert.Equal("pu giant skeleton", h.LastSent);
        Assert.Equal(2, h.Punches);

        h.FireDeadline();
        Assert.Equal(2, h.Punches);
    }

    [Fact]
    public void SameRoomShownAgain_FollowLeader_TakesTheLeadersTargetOnTheExitsLine()
    {
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.Default;
        h.Settings.TargetPriority = TargetPriority.FollowLeader;
        h.ShowRoom("Volcano Tunnel", "tunnel horror, cave bear, giant skeleton, Cidir", "north");
        h.Wire("Cidir moves to attack tunnel horror.");
        Assert.Equal("pu tunnel horror", h.LastSent);
        h.Wire("*Combat Engaged*", "Cidir smashes giant skeleton for 80 damage!", "You gain 100 experience.");

        h.Wire("\u001b[1;36mVolcano Tunnel\u001b[0m", "Also here: cave bear, giant skeleton, Cidir.");
        Assert.Equal(1, h.Punches);                                 // not yet placed
        h.Wire("Obvious exits: north.");
        Assert.Equal("pu giant skeleton", h.LastSent);              // the leader's, not our own cave bear
        Assert.Equal(2, h.Punches);
    }

    [Fact]
    public void TeleportIntoARoomTheTrackerCantPlace_HoldsUntilTheDeadline()
    {
        // Two rooms share the name and exits, so nothing tells whether this is the room
        // the fight was in: the hold stands, and the deadline still attacks in the round.
        using Harness h = new();
        FightInTheTunnel(h);

        h.ShowRoom("Crater Floor", "giant skeleton", "down");
        Assert.Equal(1, h.Punches);

        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
        Assert.Equal(2, h.Punches);
    }

    [Fact]
    public void StepIntoTheNextRoom_StillHolds()
    {
        // The step with our own move, for contrast: the move sent drops the fight.
        using Harness h = new();
        FightInTheTunnel(h);

        h.SendMove(Direction.N);
        h.ShowRoom("Lava Chamber", "giant skeleton", "south");
        Assert.Equal(new RoomKey(1, 2), h.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(1, h.Punches);

        h.FireDeadline();
        Assert.Equal("pu giant skeleton", h.LastSent);
    }

    [Fact]
    public void BugReport_NamesARoomNotYetPlaced()
    {
        using Harness h = new();
        FightInTheTunnel(h);

        h.Wire("\u001b[1;36mObsidian Hall\u001b[0m", "Also here: giant skeleton.");
        Assert.Contains("not yet placed", h.Combat.AttackOrderRoomState);
        h.Wire("Obvious exits: east.");
        Assert.Contains("fight under way here: no", h.Combat.AttackOrderRoomState);
    }
}
