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

/// <summary>
/// The last-resort route into combat: something is hurting us, nothing is
/// engaged, so read the attacker off its own attack line and engage it BY NAME.
///
/// The harness runs a real <see cref="CombatManager"/> behind the watcher,
/// because the naming IS the contract — the project owner's rule is that a bare
/// "a" must never be sent — so every assertion here is on the command that
/// reaches the wire, and it always carries a target.
/// </summary>
public sealed class FightBackWatcherTests
{
    private sealed class Harness : IDisposable
    {
        private readonly string _root;
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new();
        public PartyState Party { get; } = new();
        public LogService Log { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public CombatManager Combat { get; }
        public FightBackWatcher Watcher { get; }
        public List<byte[]> Sent { get; } = new();
        public Dictionary<int, MonsterOverlay> Overlays { get; } = new();
        public bool Enabled { get; set; } = true;
        public DateTimeOffset Now { get; set; } = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        public CombatSettings Settings { get; set; } = new()
        {
            NormalAttackCommand = "a",
            TargetOrder = TargetOrder.Normal,
        };

        public Harness()
        {
            _root = Path.Combine(Path.GetTempPath(),
                "mudplay-fightback-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_root, "alpha"));
            File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), "[]");
            GameDataCache cache = new(_root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");

            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log);
            Combat = new CombatManager(
                Router, Classifier, Monsters,
                resolveOverlay: n => Overlays.TryGetValue(n, out MonsterOverlay? o)
                                     ? o : new MonsterOverlay(),
                party: Party,
                readSettings: () => Settings,
                isEnabled: () => true,
                readOwnGivenName: () => "MudPlay",
                post: a => a(),
                log: Log);
            Combat.SetWireSender(b => Sent.Add(b));
            Combat.SetWeaponActuator((_, _, _) => { });
            Watcher = new FightBackWatcher(
                Router, Classifier,
                isEnabled: () => Enabled,
                currentTarget: () => Combat.CurrentTarget,
                now: () => Now,
                log: Log);
        }

        public void AddMonster(int number, string name) =>
            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}", Name: name,
                Links: new[] { new GameDataLink("Monsters", number) }));

        public void SetOverlay(int number, MonsterRelationship r) =>
            Overlays[number] = new MonsterOverlay { Relationship = r };

        public void Feed(string line) => Router.Dispatch(new LineExtractor.EmittedLine(
            line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        public void Hp(int hp) => Feed($"[HP={hp}]:");

        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);

        // Only real commands; CombatManager also sends a bare CR to force a room
        // re-display, which decodes to "" once trimmed.
        public List<string> Commands => Sent
            .Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'))
            .Where(s => s.Length > 0)
            .ToList();

        public void Dispose()
        {
            Watcher.Dispose();
            Combat.Dispose();
            Classifier.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }

    // The reported session, verbatim from the move that put the player in the
    // dark tunnel. "For the next hour it is fighting me without me doing
    // anything."
    private static readonly string[] Capture =
    {
        "Dark Cave, Tunnel",
        "Obvious exits: north, southwest",
        "[HP=236]:",
        "bugbear captain moves into the room from nowhere.",
        "[HP=236]:",
        "The room is very dark - you can\'t see anything",
        "[HP=236]:",
        "You do not see bugbear captain here!",
        "[HP=236]:",
        "bugbear captain moves into the from the northeast.",
        "[HP=236]:",
        "The bugbear captain swings at you with their greataxe!",
        "The bugbear captain all-out cleaves you for 20 damage!",
        "The shield spike stabs bugbear captain for 2 damage!",
        "[HP=216]:",
        "The bugbear captain swings at you with their greataxe!",
        "The bugbear captain swings at you with their greataxe!",
        "[HP=216]:",
    };

    [Fact]
    public void TheReportedSession_EngagesOnTheFirstRoundOfTheAttack()
    {
        using Harness h = new();
        h.AddMonster(963, "bugbear captain");
        int answeredAt = -1;

        for (int i = 0; i < Capture.Length; i++)
        {
            h.Feed(Capture[i]);
            if (answeredAt < 0 && h.Commands.Count > 0) answeredAt = i;
        }

        // Index 12 is the one landed blow. Everything before it is a move, a room
        // line, an arrival or a single miss — none of which is grounds to swing.
        Assert.Equal(12, answeredAt);
        Assert.Equal(new[] { "a bugbear captain" }, h.Commands);
    }

    [Fact]
    public void TheSameSession_WithNothingLanding_StillEngagesOnTheFirstRound()
    {
        // Take the one landed blow out and the client must still answer, because
        // the monster swings twice a round either way. This is the hole the swing
        // trigger closes.
        using Harness h = new();
        h.AddMonster(963, "bugbear captain");

        h.Feed("bugbear captain moves into the from the northeast.");
        h.Feed("[HP=236]:");
        h.Feed("The bugbear captain swings at you with their greataxe!");
        Assert.Empty(h.Commands);
        h.Feed("The bugbear captain swings at you with their greataxe!");

        Assert.Equal(new[] { "a bugbear captain" }, h.Commands);
    }

    [Fact]
    public void ALandedBlow_EngagesTheAttackerByName()
    {
        using Harness h = new();
        h.AddMonster(963, "bugbear captain");

        h.Feed("The bugbear captain all-out cleaves you for 20 damage!");

        // NAMED, never a bare "a" — the owner's rule.
        Assert.Equal(new[] { "a bugbear captain" }, h.Commands);
        Assert.Equal("bugbear captain", h.Combat.CurrentTarget);
    }

    [Fact]
    public void TheLongestNameWins_NotTheFirstWord()
    {
        // "bugbear captain swings at you" could be a monster called "bugbear"
        // that "captain swings", as far as the sentence goes. Both exist here, so
        // the resolver has to prefer the longer one or we engage the wrong mob.
        using Harness h = new();
        h.AddMonster(900, "bugbear");
        h.AddMonster(963, "bugbear captain");

        h.Feed("The bugbear captain all-out cleaves you for 20 damage!");

        Assert.Equal(new[] { "a bugbear captain" }, h.Commands);
    }

    [Fact]
    public void ANamedMonsterWithNoArticle_IsEngagedByName()
    {
        // "Goru-Nezar whomps you for 40 damage!" — a proper noun takes no
        // article, which is why the trigger cannot require one.
        using Harness h = new();
        h.AddMonster(311, "Goru-Nezar");

        h.Feed("Goru-Nezar whomps you for 40 damage!");

        Assert.Equal(new[] { "a Goru-Nezar" }, h.Commands);
    }

    [Fact]
    public void TheSameThingSwingingTwiceInARound_EngagesIt()
    {
        // The reported session is page after page of misses around one landed
        // blow, so waiting for damage costs rounds. A monster names itself on
        // every swing; an emote says its line once.
        using Harness h = new();
        h.AddMonster(963, "bugbear captain");

        h.Feed("The bugbear captain swings at you with their greataxe!");
        Assert.Empty(h.Commands);              // one swing proves nothing

        h.Feed("The bugbear captain swings at you with their greataxe!");
        Assert.Equal(new[] { "a bugbear captain" }, h.Commands);
    }

    [Fact]
    public void AnAttackerWeCannotName_IsNeverSwungAt()
    {
        // The whole point of the owner's rule: with no resolvable monster there
        // is nothing to attack, so we hold off rather than swinging blind at
        // whatever the room happens to contain.
        using Harness h = new();      // nothing in the monster data

        h.Feed("The bugbear captain all-out cleaves you for 20 damage!");
        h.Feed("Evil vibrations tear through you for 23 damage!");
        h.Feed("The bugbear captain swings at you with their greataxe!");
        h.Feed("The bugbear captain swings at you with their greataxe!");

        Assert.Empty(h.Commands);
        Assert.Null(h.Combat.CurrentTarget);
    }

    [Fact]
    public void ANonHostile_IsDroppedByTheOrdinaryFilter()
    {
        // Routing through the classifier is what buys this: even if a trigger
        // fires on something neutral, the engine's own hostility filter refuses
        // it. A bare swing had no such protection.
        using Harness h = new();
        h.AddMonster(50, "barmaid");
        h.SetOverlay(50, MonsterRelationship.Neutral);

        h.Feed("The barmaid smiles at you.");
        h.Feed("The barmaid smiles at you.");

        Assert.Empty(h.Commands);
        Assert.Null(h.Combat.CurrentTarget);
    }

    [Fact]
    public void RepeatedChatterFromDifferentSources_NeverFires()
    {
        using Harness h = new();
        h.AddMonster(50, "barmaid");
        h.AddMonster(51, "shopkeeper");

        h.Feed("The barmaid smiles at you.");
        h.Feed("The shopkeeper looks at you.");

        Assert.Empty(h.Commands);
    }

    [Fact]
    public void SwingsSpacedFurtherApartThanARound_DoNotPair()
    {
        using Harness h = new();
        h.AddMonster(50, "barmaid");

        h.Feed("The barmaid smiles at you.");
        h.Advance(30);
        h.Feed("The barmaid smiles at you.");

        Assert.Empty(h.Commands);
    }

    [Fact]
    public void AutoCombatOff_HoldsOff()
    {
        using Harness h = new();
        h.AddMonster(963, "bugbear captain");
        h.Enabled = false;

        h.Feed("The bugbear captain all-out cleaves you for 20 damage!");
        h.Hp(236);
        h.Hp(200);

        Assert.Empty(h.Commands);
    }

    [Fact]
    public void AlreadyEngaged_HoldsOff()
    {
        using Harness h = new();
        h.AddMonster(963, "bugbear captain");
        h.Feed("*Combat Engaged*");

        h.Feed("The bugbear captain all-out cleaves you for 20 damage!");

        Assert.Empty(h.Commands);
    }

    [Fact]
    public void SeveralBlowsInOneRound_EngageOnce()
    {
        using Harness h = new();
        h.AddMonster(963, "bugbear captain");

        h.Feed("The bugbear captain cleaves you for 8 damage!");
        h.Feed("The bugbear captain cleaves you for 11 damage!");
        h.Feed("The bugbear captain cleaves you for 4 damage!");

        Assert.Equal(new[] { "a bugbear captain" }, h.Commands);
    }

    [Fact]
    public void AnHpDrop_WithAnEmptyRoom_DoesNothing()
    {
        // A prompt names nobody. With no roster there is no target, and inventing
        // one is exactly what we are not allowed to do — so a damage-over-time
        // out of combat now costs nothing at all.
        using Harness h = new();
        h.Hp(236);

        h.Hp(228);
        h.Advance(10);
        h.Hp(220);

        Assert.Empty(h.Commands);
    }

    [Fact]
    public void OurOwnHits_AndBlowsOnPartyMembers_AreNotIncoming()
    {
        using Harness h = new();
        h.AddMonster(963, "bugbear captain");

        h.Feed("You hit bugbear captain for 41 damage!");
        h.Feed("You critically hit bugbear captain for 14 damage!");
        h.Feed("The bugbear captain cleaves Bob for 8 damage!");
        h.Feed("The shield spike stabs bugbear captain for 2 damage!");

        Assert.Empty(h.Commands);
    }
}
