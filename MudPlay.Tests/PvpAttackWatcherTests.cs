using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Pvp;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// PvpAttackWatcher: which lines count as another player attacking us, and when
// that marks them an Enemy.
public sealed class PvpAttackWatcherTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new(new ProfileService(), () => null);
        public PartyState Party { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public RoomEntryWatcher Entry { get; }
        public PvpRoomSafety Room { get; }
        public PartySplitTracker PartySplit { get; }
        public TimeSpan Hold { get; set; } = TimeSpan.FromMinutes(2);
        public PvpAttackWatcher Watcher { get; }
        public List<PvpAttack> Attacks { get; } = new();

        public bool PvpEnabled { get; set; } = true;
        public bool FlipFriends { get; set; }
        public DateTimeOffset Clock { get; set; } = DateTimeOffset.Now;
        public DateTimeOffset? LastMoveSentAt { get; private set; }

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, new LogService());
            Room = new PvpRoomSafety(
                Router, Classifier,
                pvpEnabled: () => PvpEnabled,
                inParty: Party.HasMember,
                classOf: _ => null,
                levelOf: _ => null,
                roomAttackFromLevel: _ => null,
                realm: () => RealmType.ParaMud,
                lastMoveSentAt: () => LastMoveSentAt,
                now: () => Clock);
            Entry = new RoomEntryWatcher(Router, Classifier, now: () => Clock);
            Entry.ArrivalObserved += Room.NoteArrival;
            PartySplit = new PartySplitTracker(Party, () => Hold, () => "Hero", () => Clock);
            Watcher = new PvpAttackWatcher(
                Router, Classifier, Room, Players, Party, PartySplit,
                pvpEnabled: () => PvpEnabled,
                flipFriends: () => FlipFriends,
                ownGivenName: () => "Hero",
                now: () => Clock);
            Watcher.Attacked += Attacks.Add;

            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: "M1", Name: "giant rat", Links: new[] { new GameDataLink("Monsters", 1) }));
            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: "M2", Name: "Gorgon", Links: new[] { new GameDataLink("Monsters", 2) }));
            Players.RecordObservation("Bob", "Mage", null, null, null, null, null, DateTime.UtcNow);
        }

        public PlayerRelationship RelationshipOf(string given) =>
            Players.Find(given)?.Relationship ?? PlayerRelationship.Neutral;

        // Our own move going out, a moment after what went before and before what follows.
        public void Move()
        {
            Clock += TimeSpan.FromMilliseconds(1);
            LastMoveSentAt = Clock;
            Clock += TimeSpan.FromMilliseconds(1);
        }

        public void Feed(string line) => Router.Dispatch(new LineExtractor.EmittedLine(
            line, Array.Empty<CellAttributes>(), Clock, IsPromptLine: false));

        public void Dispose()
        {
            Watcher.Dispose();
            PartySplit.Dispose();
            Room.Dispose();
            Entry.Dispose();
            Classifier.Dispose();
        }
    }

    // ----- an attack aimed at us ---------------------------------------

    [Fact]
    public void NeutralMovesToAttackUs_IsMarkedEnemy_AndSaved()
    {
        using Harness h = new();
        h.Feed("Bob moves to attack you!");

        PvpAttack attack = Assert.Single(h.Attacks);
        Assert.Equal(("Bob", PvpAttackKind.Announced, PlayerRelationship.Enemy, true),
            (attack.Player, attack.Kind, attack.Relationship, attack.MarkedEnemy));
        Assert.Equal(PlayerRelationship.Enemy, h.RelationshipOf("Bob"));
    }

    [Fact]
    public void PromptGluedAnnounce_IsRecognised()
    {
        using Harness h = new();
        h.Feed("[HP=100/MA=50]:Bob moves to attack you!");

        Assert.Equal(PlayerRelationship.Enemy, h.RelationshipOf("Bob"));
    }

    [Fact]
    public void AttackerWithNoRecord_GetsOne_MarkedEnemy()
    {
        using Harness h = new();
        h.Feed("Zed moves to attack you!");

        Assert.Equal(PlayerRelationship.Enemy, h.RelationshipOf("Zed"));
        Assert.True(Assert.Single(h.Attacks).MarkedEnemy);
    }

    [Fact]
    public void EnemyAttacksAgain_IsReported_NotMarkedTwice_AndKeepsTheirOwnResponse()
    {
        using Harness h = new();
        h.Players.SetRelationship("Bob", PlayerRelationship.Enemy, PvpAction.Flee);
        h.Feed("Bob moves to attack you!");

        PvpAttack attack = Assert.Single(h.Attacks);
        Assert.False(attack.MarkedEnemy);
        Assert.Equal(PlayerRelationship.Enemy, attack.Relationship);
        Assert.Equal(PvpAction.Flee, h.Players.Find("Bob")!.PvpResponse);
    }

    [Fact]
    public void FriendAttacksUs_StaysAFriend_UnlessTheSettingSaysOtherwise()
    {
        using Harness h = new();
        h.Players.SetRelationship("Bob", PlayerRelationship.Friend, null);

        h.Feed("Bob moves to attack you!");
        Assert.Equal(PlayerRelationship.Friend, h.RelationshipOf("Bob"));
        Assert.Equal(PlayerRelationship.Friend, Assert.Single(h.Attacks).Relationship);

        h.FlipFriends = true;
        h.Feed("Bob moves to attack you!");
        Assert.Equal(PlayerRelationship.Enemy, h.RelationshipOf("Bob"));
        Assert.True(h.Attacks[^1].MarkedEnemy);
    }

    [Fact]
    public void PvpNotEnabledForTheRealm_NothingIsMarked()
    {
        using Harness h = new() { PvpEnabled = false };
        h.Feed("Bob moves to attack you!");

        Assert.Empty(h.Attacks);
        Assert.Equal(PlayerRelationship.Neutral, h.RelationshipOf("Bob"));
    }

    [Fact]
    public void PartyMemberOrOurOwnName_IsIgnored()
    {
        using Harness h = new();
        h.Party.Members.Add(new PartyMember { Name = "Bob Smith" });
        h.Feed("Bob moves to attack you!");
        h.Feed("Hero moves to attack you!");

        Assert.Empty(h.Attacks);
        Assert.Equal(PlayerRelationship.Neutral, h.RelationshipOf("Bob"));
    }

    // The announce for a monster target, and the onlookers' form for a player
    // target, are not attacks on us.
    [Theory]
    [InlineData("Bob moves to attack giant rat.")]
    [InlineData("Bob moves to attack Ann!")]
    public void AnnounceAimedElsewhere_IsNotAnAttackOnUs(string line)
    {
        using Harness h = new();
        h.Feed(line);

        Assert.Empty(h.Attacks);
    }

    // ----- a damage line, with no announce seen --------------------------

    [Theory]
    [InlineData("Bob hits you for 12 damage!")]
    [InlineData("Bob critically hits you for 40 damage!")]
    [InlineData("Bob surprise backstabs you for 88 damage!")]
    public void DamageLineNamingAPlayer_IsAnAttack(string line)
    {
        using Harness h = new();
        h.Feed(line);

        Assert.Equal(PvpAttackKind.Damage, Assert.Single(h.Attacks).Kind);
        Assert.Equal(PlayerRelationship.Enemy, h.RelationshipOf("Bob"));
    }

    [Theory]
    [InlineData("The giant rat bites you for 3 damage!")]
    [InlineData("Gorgon bites you for 30 damage!")]                          // a named monster
    [InlineData("A hellish storm of fire and brimstone scorches you for 50 damage!")]
    [InlineData("Stranger hits you for 5 damage!")]                          // not a player we know
    public void DamageLineFromAnythingElse_IsNot(string line)
    {
        using Harness h = new();
        h.Feed(line);

        Assert.Empty(h.Attacks);
    }

    // ----- room attacks --------------------------------------------------

    [Fact]
    public void PlayerWalksInOnUs_ThenRoomAttacks_IsAnAttack()
    {
        using Harness h = new();
        h.Move();
        h.Feed("Also here: giant rat.");
        h.Feed("Bob walks into the room from the south.");
        h.Feed("Bob moves to attack everyone in the room.");

        Assert.Equal(PvpAttackKind.RoomAttack, Assert.Single(h.Attacks).Kind);
        Assert.Equal(PlayerRelationship.Enemy, h.RelationshipOf("Bob"));
    }

    // We walked in on them: their room, their room attack, not aimed at us.
    [Fact]
    public void RoomAttackInARoomThatWasTheirs_IsNotAnAttack()
    {
        using Harness h = new();
        h.Move();
        h.Feed("Also here: Bob, giant rat.");
        h.Feed("Bob moves to attack everyone in the room.");

        Assert.Empty(h.Attacks);
        Assert.Equal(PlayerRelationship.Neutral, h.RelationshipOf("Bob"));
    }

    [Fact]
    public void ArrivalSeenInTheLastRoom_DoesNotCountInThisOne()
    {
        using Harness h = new();
        h.Move();
        h.Feed("Also here: giant rat.");
        h.Feed("Bob walks into the room from the south.");

        h.Move();
        h.Feed("Also here: Bob, giant rat.");
        h.Feed("Bob moves to attack everyone in the room.");

        Assert.Empty(h.Attacks);
    }

    // A teleport broke the party up; the room attack goes out before they rejoin.
    [Fact]
    public void RoomAttackFromSomeoneJustOutOfOurParty_IsNotAnAttack_UntilTheGraceRunsOut()
    {
        using Harness h = new();
        PartyMember bob = new() { Name = "Bob" };
        h.Party.Members.Add(bob);
        h.Move();
        h.Feed("Also here: giant rat.");
        h.Party.Members.Remove(bob);
        h.Feed("Bob walks into the room from the south.");

        h.Feed("Bob moves to attack everyone in the room.");
        Assert.Empty(h.Attacks);

        h.Clock += h.Hold + TimeSpan.FromSeconds(1);
        h.Feed("Bob moves to attack everyone in the room.");
        Assert.Single(h.Attacks);
    }

    // Aimed at us is aimed at us, party a moment ago or not.
    [Fact]
    public void DirectAttackFromSomeoneJustOutOfOurParty_IsAnAttack()
    {
        using Harness h = new();
        PartyMember bob = new() { Name = "Bob" };
        h.Party.Members.Add(bob);
        h.Party.Members.Clear();

        h.Feed("Bob moves to attack you!");

        Assert.Equal(PlayerRelationship.Enemy, h.RelationshipOf("Bob"));
    }

    [Fact]
    public void RecentAttacks_KeepOneEntryPerBurst_AndDropTheOldest()
    {
        using Harness h = new();
        h.Feed("Bob moves to attack you!");
        h.Feed("Bob hits you for 12 damage!");
        h.Feed("Bob hits you for 9 damage!");
        Assert.Single(h.Watcher.Recent);
        Assert.Equal(3, h.Attacks.Count);     // every one is still reported

        for (int i = 0; i < 14; i++)
        {
            h.Clock += TimeSpan.FromSeconds(30);
            h.Feed("Bob moves to attack you!");
        }
        Assert.Equal(12, h.Watcher.Recent.Count);
    }
}
