using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Pvp;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// PvpRoomSafety: whose presence holds our room attacks, and when another player's
// room attack in their own room sends a running walk on to the next room.
public sealed class PvpRoomSafetyTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new(new ProfileService(), () => null);
        public RoomEntityClassifier Classifier { get; }
        public RoomEntryWatcher Watcher { get; }
        public PvpRoomSafety Safety { get; }

        public bool PvpEnabled { get; set; } = true;
        public RealmType Realm { get; set; } = RealmType.Stock;
        public HashSet<string> Party { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Classes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Levels { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset? LastMoveSentAt { get; private set; }
        public DateTimeOffset Clock { get; private set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public int RoomAttacksSeen { get; private set; }

        // Class → the level its first room attack comes at (0 = none). A class
        // missing here isn't in the game data: unknown.
        public Dictionary<string, int> RoomAttackFrom { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Mage"] = 11,
            ["Warrior"] = 0,
        };

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, new LogService());
            Safety = new PvpRoomSafety(
                Router, Classifier,
                pvpEnabled: () => PvpEnabled,
                inParty: Party.Contains,
                classOf: given => Classes.TryGetValue(given, out string? cls) ? cls : null,
                levelOf: given => Levels.TryGetValue(given, out int level) ? level : null,
                roomAttackFromLevel: cls => RoomAttackFrom.TryGetValue(cls, out int from) ? from : null,
                realm: () => Realm,
                lastMoveSentAt: () => LastMoveSentAt,
                now: () => Clock);
            Safety.RoomAttackSeen += () => RoomAttacksSeen++;
            Watcher = new RoomEntryWatcher(Router, Classifier, now: () => Clock);
            Watcher.ArrivalObserved += Safety.NoteArrival;

            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: "M1", Name: "giant rat", Links: new[] { new GameDataLink("Monsters", 1) }));
            AddPlayer("Bob", "Mage");
            AddPlayer("Ann", "Warrior");
        }

        public void AddPlayer(string given, string? cls)
        {
            Players.Players.Add(new PlayerRecord(
                GivenName: given, FamilyName: string.Empty, Class: cls, Race: "Human",
                Alignment: "Neutral", Title: null, Gang: null, Role: null,
                FirstSeenUtc: DateTime.UtcNow, LastSeenUtc: DateTime.UtcNow));
            if (cls is not null) Classes[given] = cls;
        }

        // Our own move going out, a moment after what went before and before what follows.
        public void Move()
        {
            Clock += TimeSpan.FromMilliseconds(1);
            LastMoveSentAt = Clock;
            Clock += TimeSpan.FromMilliseconds(1);
        }

        // Step into a room with nobody and nothing in it.
        public void MoveIntoEmptyRoom()
        {
            Move();
            Classifier.NoteRoomChanged();
        }

        public void Feed(string line) => Router.Dispatch(new LineExtractor.EmittedLine(
            line, Array.Empty<CellAttributes>(), Clock, IsPromptLine: false));

        public void Dispose()
        {
            Safety.Dispose();
            Watcher.Dispose();
            Classifier.Dispose();
        }
    }

    private const string ThreeRats = "giant rat, giant rat, giant rat";

    // ----- our own room attacks ----------------------------------------

    [Fact]
    public void PlayerOutsideTheParty_HoldsOurRoomAttacks()
    {
        using Harness h = new();
        h.Feed("Also here: Bob, giant rat.");

        Assert.StartsWith("Bob is here", h.Safety.RoomAttackHeldBy());
    }

    [Fact]
    public void PvpOffForTheRealm_HoldsNothing()
    {
        using Harness h = new() { PvpEnabled = false };
        h.Feed($"Also here: Bob, {ThreeRats}.");

        Assert.Null(h.Safety.RoomAttackHeldBy());
        Assert.Null(h.Safety.LeaveRoomReason());
    }

    // Only party members are spared. Who a non-member is to us (Friend, Neutral,
    // Enemy) makes no difference to a room spell.
    [Fact]
    public void PartyMembers_DontHold_EveryoneElseDoes()
    {
        using Harness h = new();
        h.Party.Add("Bob");
        h.Feed("Also here: Bob, giant rat.");
        Assert.Null(h.Safety.RoomAttackHeldBy());

        h.Feed("Also here: Bob, Ann, giant rat.");
        Assert.StartsWith("Ann is here", h.Safety.RoomAttackHeldBy());
    }

    [Fact]
    public void HoldLiftsWhenThePlayerJoinsTheParty()
    {
        using Harness h = new();
        h.Feed("Also here: Bob, giant rat.");
        Assert.StartsWith("Bob is here", h.Safety.RoomAttackHeldBy());

        h.Party.Add("Bob");
        Assert.Null(h.Safety.RoomAttackHeldBy());
    }

    [Fact]
    public void PlayerWhoWalksIn_Holds()
    {
        using Harness h = new();
        h.Feed("Also here: giant rat.");
        Assert.Null(h.Safety.RoomAttackHeldBy());

        h.Feed("Bob walks into the room from the south.");
        Assert.StartsWith("Bob is here", h.Safety.RoomAttackHeldBy());
    }

    // A teleport split the party: whoever dropped out may land in the room any
    // moment, so room attacks are held until they are back or the hold runs out.
    [Fact]
    public void MemberWhoDroppedOutOfTheParty_HoldsRoomAttacks_UntilBackOrTheHoldRunsOut()
    {
        using Harness h = new();
        PartyState party = new();
        PartyMember bob = new() { Name = "Bob" };
        party.Members.Add(new PartyMember { Name = "Hero" });
        party.Members.Add(bob);
        DateTimeOffset clock = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        using PartySplitTracker split = new(party, () => TimeSpan.FromSeconds(120), () => "Hero", () => clock);
        h.Safety.SetPartySplit(split);
        h.Feed("Also here: giant rat.");
        Assert.Null(h.Safety.RoomAttackHeldBy());

        party.Members.Remove(bob);
        Assert.StartsWith("Bob dropped out", h.Safety.RoomAttackHeldBy());

        party.Members.Add(bob);
        Assert.Null(h.Safety.RoomAttackHeldBy());

        party.Members.Clear();                  // the whole party gone, our own row with it
        Assert.StartsWith("Bob dropped out", h.Safety.RoomAttackHeldBy());
        clock += TimeSpan.FromSeconds(121);
        Assert.Null(h.Safety.RoomAttackHeldBy());
    }

    // A name the room reader couldn't place counts as a player while `who` is asked.
    [Fact]
    public void UnplacedNameThatMayBeAPlayer_Holds()
    {
        using Harness h = new();
        h.Feed("Also here: Zed, giant rat.");
        Assert.Null(h.Safety.RoomAttackHeldBy());

        h.Safety.SetStrangerProbe(name => name == "Zed");
        Assert.StartsWith("Zed is here", h.Safety.RoomAttackHeldBy());
    }

    // ----- Stock: guessing that a resident is rooming -------------------

    [Fact]
    public void Stock_RoomAttackClassHereFirstWithThreeMonsters_Leave()
    {
        using Harness h = new();
        h.Move();
        h.Feed($"Also here: Bob, {ThreeRats}.");

        Assert.Contains("Bob (Mage)", h.Safety.LeaveRoomReason());
    }

    [Fact]
    public void Stock_TwoMonsters_Stay()
    {
        using Harness h = new();
        h.Move();
        h.Feed("Also here: Bob, giant rat, giant rat.");

        Assert.Null(h.Safety.LeaveRoomReason());
    }

    [Fact]
    public void Stock_ClassWithNoRoomAttack_Stay()
    {
        using Harness h = new();
        h.Move();
        h.Feed($"Also here: Ann, {ThreeRats}.");

        Assert.Null(h.Safety.LeaveRoomReason());
    }

    [Fact]
    public void Stock_ClassNotKnown_OrNotInTheGameData_Leave()
    {
        using Harness h = new();
        h.AddPlayer("Cal", cls: null);
        h.AddPlayer("Dee", "Gladiator");

        h.Move();
        h.Feed($"Also here: Cal, {ThreeRats}.");
        Assert.Contains("Cal (class unknown)", h.Safety.LeaveRoomReason());

        h.Move();
        h.Feed($"Also here: Dee, {ThreeRats}.");
        Assert.Contains("Dee (Gladiator)", h.Safety.LeaveRoomReason());
    }

    [Fact]
    public void Stock_KnownLevelBelowTheClassesFirstRoomAttack_Stay()
    {
        using Harness h = new();
        h.Levels["Bob"] = 8;
        h.Move();
        h.Feed($"Also here: Bob, {ThreeRats}.");
        Assert.Null(h.Safety.LeaveRoomReason());

        h.Levels["Bob"] = 11;
        Assert.NotNull(h.Safety.LeaveRoomReason());
    }

    // It is our room when they walk in on us; that is not a reason to leave.
    [Fact]
    public void Stock_PlayerArrivedAfterUs_Stay_EvenOnARedisplay()
    {
        using Harness h = new();
        h.Move();
        h.Feed($"Also here: {ThreeRats}.");
        h.Feed("Bob walks into the room from the south.");
        Assert.Null(h.Safety.LeaveRoomReason());

        h.Feed($"Also here: Bob, {ThreeRats}.");
        Assert.Null(h.Safety.LeaveRoomReason());
    }

    [Fact]
    public void Stock_EmptyRoomOnArrival_LaterArrivalsArentResidents()
    {
        using Harness h = new();
        h.MoveIntoEmptyRoom();
        h.Feed("Bob walks into the room from the south.");
        h.Feed($"Also here: Bob, {ThreeRats}.");

        Assert.Null(h.Safety.LeaveRoomReason());
    }

    [Fact]
    public void PartyMember_IsNoReasonToLeave()
    {
        using Harness h = new();
        h.Party.Add("Bob");
        h.Move();
        h.Feed($"Also here: Bob, {ThreeRats}.");

        Assert.Null(h.Safety.LeaveRoomReason());
    }

    // ----- Paradigm: the game says so -----------------------------------

    [Fact]
    public void Paradigm_NoGuessingFromClassAndMonsterCount()
    {
        using Harness h = new() { Realm = RealmType.ParaMud };
        h.Move();
        h.Feed($"Also here: Bob, {ThreeRats}.");

        Assert.Null(h.Safety.LeaveRoomReason());
    }

    [Fact]
    public void Paradigm_PoisedLine_Leave_AndTellsTheCombatGate()
    {
        using Harness h = new() { Realm = RealmType.ParaMud };
        h.Move();
        h.Feed("Also here: Bob, giant rat.");
        h.Feed("Bob is poised to assault the room!");

        Assert.Equal("Bob is room-attacking here", h.Safety.LeaveRoomReason());
        Assert.Equal(1, h.RoomAttacksSeen);
    }

    [Fact]
    public void PoisedPartyMember_IsNotAnnounced_AndIsNoReasonToLeave()
    {
        using Harness h = new() { Realm = RealmType.ParaMud };
        h.Party.Add("Bob");
        h.Move();
        h.Feed("Also here: Bob, giant rat.");
        h.Feed("Bob is poised to assault the room!");

        Assert.Null(h.Safety.LeaveRoomReason());
        Assert.Equal(0, h.RoomAttacksSeen);
    }

    // ----- a room attack announced while we stand there ------------------

    [Theory]
    [InlineData(RealmType.Stock)]
    [InlineData(RealmType.ParaMud)]
    public void ResidentAnnouncesARoomAttack_Leave(RealmType realm)
    {
        using Harness h = new() { Realm = realm };
        h.Move();
        h.Feed("Also here: Bob.");
        Assert.Null(h.Safety.LeaveRoomReason());

        h.Feed("A giant rat walks into the room from the north.");
        h.Feed("Bob moves to attack everyone in the room.");

        Assert.Equal("Bob is room-attacking here", h.Safety.LeaveRoomReason());
    }

    [Fact]
    public void SingleTargetAnnounce_IsNotARoomAttack()
    {
        using Harness h = new() { Realm = RealmType.ParaMud };
        h.Move();
        h.Feed("Also here: Bob, giant rat.");
        h.Feed("Bob moves to attack giant rat.");

        Assert.Null(h.Safety.LeaveRoomReason());
        Assert.Equal(0, h.RoomAttacksSeen);
    }

    // A player who walks in on us and rooms is attacking us, not a room to walk out of.
    [Fact]
    public void NewcomerAnnouncesARoomAttack_Stay()
    {
        using Harness h = new() { Realm = RealmType.ParaMud };
        h.Move();
        h.Feed("Also here: giant rat.");
        h.Feed("Bob walks into the room from the south.");
        h.Feed("Bob moves to attack everyone in the room.");

        Assert.Null(h.Safety.LeaveRoomReason());
    }

    // ----- who we watched walk in ----------------------------------------

    [Fact]
    public void ArrivedAfterUs_OnlyForAPlayerSeenWalkingIn_SinceOurLastMove()
    {
        using Harness h = new();
        h.Move();
        h.Feed("Also here: Ann, giant rat.");
        h.Feed("Bob walks into the room from the south.");
        h.Feed("A giant rat walks into the room from the north.");

        Assert.True(h.Safety.ArrivedAfterUs("Bob"));
        Assert.False(h.Safety.ArrivedAfterUs("Ann"));    // here before us
        Assert.False(h.Safety.ArrivedAfterUs("Cal"));    // never seen

        h.Move();
        Assert.False(h.Safety.ArrivedAfterUs("Bob"));
    }

    [Fact]
    public void RoomAttackSeenInTheLastRoom_DoesNotFollowUsToTheNext()
    {
        using Harness h = new() { Realm = RealmType.ParaMud };
        h.Move();
        h.Feed("Also here: Bob, giant rat.");
        h.Feed("Bob is poised to assault the room!");
        Assert.NotNull(h.Safety.LeaveRoomReason());

        // Bob is in the next room too, not rooming this time.
        h.Move();
        h.Feed("Also here: Bob, giant rat.");
        Assert.Null(h.Safety.LeaveRoomReason());
    }
}
