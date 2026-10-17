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

// End-to-end: a monster revealed only in a dark room (by its own attack line
// or a party member's "moves to attack" announce) is injected by
// DarkRoomCombatWatcher into the shared entity observation, then
// engaged by CombatManager through the SAME hostility filter and
// Target Order ranking a lit-room "Also here:" monster gets. Pins the contract
// that dark-room combat honors Settings → Combat, and only spins up on hostiles.
public sealed class DarkRoomCombatIntegrationTests
{
    private sealed class Harness : IDisposable
    {
        private readonly string _root;
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new(new ProfileService(), () => null);
        public PartyState Party { get; } = new();
        public LogService Log { get; } = new();
        public RoomTracker Tracker { get; }
        public RoomEntityClassifier Classifier { get; }
        public CombatManager Combat { get; }
        public DarkRoomCombatWatcher Watcher { get; }
        // The roster keepers for the wire's own arrival / departure notices. In
        // production AppServices wires both beside the dark watcher; a dark room's
        // roster leans on them the same way a lit one does, because a monster that
        // walks in or out says so whether or not you can see it.
        public RoomEntryWatcher Entry { get; }
        public RoomDepartureWatcher Departure { get; }
        public List<byte[]> Sent { get; } = new();
        public Dictionary<int, MonsterOverlay> Overlays { get; } = new();
        public CombatSettings Settings { get; set; } = new()
        {
            NormalAttackCommand = "a",
            TargetOrder = TargetOrder.Normal,
        };

        public Harness()
        {
            _root = Path.Combine(Path.GetTempPath(),
                "mudplay-darkcombat-integ-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_root, "alpha"));
            File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), "[]");
            GameDataCache cache = new(_root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");
            Tracker = new RoomTracker(graph);

            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log);
            Combat = new CombatManager(Router, Classifier, Monsters,
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
            Watcher = new DarkRoomCombatWatcher(
                Router, Tracker, Classifier,
                currentTarget: () => Combat.CurrentTarget,
                log: Log);
            Entry = new RoomEntryWatcher(Router, Classifier, Log);
            Departure = new RoomDepartureWatcher(Router, Classifier, Log);
        }

        public void EnterDarkRoom() => Tracker.NoteDarkRoomEntered();

        public void SetOverlay(int monsterNumber, MonsterRelationship relationship) =>
            Overlays[monsterNumber] = new MonsterOverlay { Relationship = relationship };

        public void AddMonster(int number, string name)
        {
            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}",
                Name: name,
                Links: new[] { new GameDataLink("Monsters", number) }));
        }

        public void Feed(string line)
        {
            LineExtractor.EmittedLine emitted = new(
                line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false);
            Router.Dispatch(emitted);
        }

        public string LastSent => Sent.Count == 0
            ? string.Empty
            : Encoding.Latin1.GetString(Sent[^1]).TrimEnd('\r');

        // Count only real commands (attacks), not the bare "\r" room-refresh
        // nudges CombatManager sends to force a re-display when a combat line
        // arrives before the roster is known. A CR decodes to "" once trimmed.
        public int AttackSends => Sent.Count(b =>
            Encoding.Latin1.GetString(b).TrimEnd('\r').Length > 0);

        public void Dispose()
        {
            Departure.Dispose();
            Entry.Dispose();
            Watcher.Dispose();
            Combat.Dispose();
            Classifier.Dispose();
            try { Directory.Delete(_root, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    [Fact]
    public void CompassArrival_InDark_SpinsUsIntoCombat()
    {
        // Reported with a capture: a bugbear captain pursued the player into a dark
        // tunnel and swung every round while the client sat idle — "you can see it
        // sits there until I die".
        //
        // The mob announced itself with the server's generic compass arrival, and
        // that one string omits the word "room" (a stock typo the server reproduces
        // deliberately), so RoomEntryArrival did not match it and nothing added the
        // mob to the roster. A lit room recovers on its next "Also here:"; a dark
        // room never re-displays one.
        using Harness h = new();
        h.AddMonster(963, "bugbear captain");
        h.EnterDarkRoom();

        h.Feed("bugbear captain moves into the from the northeast.");

        Assert.Equal("a bugbear captain", h.LastSent);
        Assert.Equal("bugbear captain", h.Combat.CurrentTarget);
    }

    [Fact]
    public void CompassDeparture_InDark_DropsTheTarget()
    {
        // The mirror: it walks away with "just left to the <dir>.", which carried
        // neither "out" nor "the room", so nothing retracted it and the engine kept
        // swinging at a monster the server then answered for with "You do not see
        // bugbear captain here!".
        using Harness h = new();
        h.AddMonster(963, "bugbear captain");
        h.EnterDarkRoom();
        h.Feed("bugbear captain moves into the from the northeast.");
        Assert.Equal("bugbear captain", h.Combat.CurrentTarget);

        h.Feed("bugbear captain just left to the southwest.");

        Assert.Null(h.Combat.CurrentTarget);
    }

    [Fact]
    public void ReportedSession_InDark_EngagesTheAttackerByName()
    {
        // End to end: the wire from the reported session drives a real
        // CombatManager, and what goes out names the monster.
        using Harness h = new();
        h.AddMonster(963, "bugbear captain");
        h.EnterDarkRoom();

        h.Feed("bugbear captain moves into the from the northeast.");
        h.Feed("The bugbear captain swings at you with their greataxe!");

        Assert.Equal("a bugbear captain", h.LastSent);
        Assert.Equal("bugbear captain", h.Combat.CurrentTarget);
    }

    [Fact]
    public void ANonHostileAttacker_InDark_IsNotEngaged()
    {
        // Injecting through the classifier is what buys this: the ordinary
        // hostility filter still refuses, so a trigger cannot open on something
        // set Neutral.
        using Harness h = new();
        h.AddMonster(50, "barmaid");
        h.SetOverlay(50, MonsterRelationship.Neutral);
        h.EnterDarkRoom();

        h.Feed("The barmaid swings at you.");

        // AttackSends, not Sent: CombatManager also emits a bare CR to force a
        // room re-display, which is not an attack.
        Assert.Equal(0, h.AttackSends);
        Assert.Null(h.Combat.CurrentTarget);
    }

    [Fact]
    public void PartyAttackAnnounce_InDark_SpinsUsIntoCombat()
    {
        // The leader engages a monster the dark room never named; the announce
        // reveals it and CombatManager follows suit with the swing command.
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.EnterDarkRoom();

        h.Feed("Tristian moves to attack giant rat.");

        Assert.Equal("a giant rat", h.LastSent);
        Assert.Equal("giant rat", h.Combat.CurrentTarget);
    }

    [Fact]
    public void PartyAttackReveal_NeutralMonster_InDark_NotEngaged()
    {
        // A party member attacking a non-hostile (Relationship=Neutral) must not
        // drag us in — the hostility filter that guards lit rooms guards here too.
        using Harness h = new();
        h.AddMonster(1, "merchant");
        h.SetOverlay(1, MonsterRelationship.Neutral);
        h.EnterDarkRoom();

        h.Feed("Tristian moves to attack merchant.");

        Assert.Empty(h.Sent);
        Assert.Null(h.Combat.CurrentTarget);
    }

    [Fact]
    public void SecondHostileReveal_DoesNotThrashValidTarget()
    {
        // Dark reveals arrive one mob per line, so the first hostile named is the
        // only candidate when we first pick — we engage it and stay put. A second
        // hostile revealed a beat later must NOT yank us off a target we're still
        // validly swinging at; a re-pick only fires when the current target is gone.
        // Target Order therefore never thrashes a live fight — it decides among the
        // candidates present at pick time, which the death re-pick below exercises.
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "goblin");
        h.EnterDarkRoom();

        h.Feed("Tristian moves to attack giant rat.");
        Assert.Equal("giant rat", h.Combat.CurrentTarget);
        int afterFirst = h.Sent.Count;

        h.Feed("Grib moves to attack goblin.");

        Assert.Equal("giant rat", h.Combat.CurrentTarget);   // unchanged
        Assert.Equal(afterFirst, h.Sent.Count);              // no second swing
    }

    [Fact]
    public void RepickAfterDeath_HonorsTargetOrderReverse()
    {
        // Three hostiles revealed in witness order (rat, goblin, orc); we engage the
        // first (rat). When it dies, the re-pick ranks the surviving witnessed set
        // [goblin, orc] through the SAME TargetOrder a lit-room re-pick uses — under
        // Reverse that's the last-witnessed survivor, orc. This is the honest place
        // Target Order surfaces in the sequential dark flow: at a re-pick over 2+
        // survivors, exactly as a reversed "Also here:" list would resolve.
        using Harness h = new();
        h.Settings.TargetOrder = TargetOrder.Reverse;
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "goblin");
        h.AddMonster(3, "orc");
        h.EnterDarkRoom();

        h.Feed("Tristian moves to attack giant rat.");
        h.Feed("Grib moves to attack goblin.");
        h.Feed("Vex moves to attack orc.");
        Assert.Equal("giant rat", h.Combat.CurrentTarget);   // first-revealed engaged
        int beforeDeath = h.Sent.Count;

        // The rat dies: the death drops it from the roster, and the surviving set
        // re-fires EntitiesObserved.
        // A kill as the client sees one: the exp line, the kill's *Combat Off*, and
        // the death event AppServices routes to NoteUnattributedDeath.
        h.Feed("You gain 10 experience.");
        h.Feed("*Combat Off*");
        h.Combat.NoteUnattributedDeath();

        Assert.True(h.Sent.Count > beforeDeath, "expected a fresh swing at a survivor");
        Assert.Equal("a orc", h.LastSent);                   // Reverse → last-witnessed survivor
        Assert.Equal("orc", h.Combat.CurrentTarget);
    }

    [Fact]
    public void MobAndPartyReveal_SameMonster_EngagedOnce()
    {
        // The leader's announce and the mob's own attack line name the same
        // monster; per-round dedupe keeps a single target, one swing.
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.EnterDarkRoom();

        h.Feed("Tristian moves to attack giant rat.");
        h.Feed("The giant rat bites you.");

        Assert.Equal("giant rat", h.Combat.CurrentTarget);
        Assert.Single(h.Sent);
    }

    [Fact]
    public void RepeatedSameMonsterAttacks_InDark_EngageOneTargetOneSwing()
    {
        // A dark room re-emits a mob's attack line every round, and a single mob
        // can swing more than once per round — four "cave lizard swings at you"
        // lines do NOT mean four cave lizards. Name dedupe collapses them to one
        // injected entity, so CombatManager engages one target and swings once,
        // never fabricating phantom lizards it would then chase around an empty
        // room. This pins the contract end-to-end (watcher → classifier → combat).
        using Harness h = new();
        h.AddMonster(1, "cave lizard");
        h.EnterDarkRoom();

        h.Feed("The cave lizard swings at you.");
        h.Feed("The cave lizard rips you for 6 damage!");
        h.Feed("The cave lizard swings at you.");
        h.Feed("The cave lizard swings at you.");

        Assert.Equal("cave lizard", h.Combat.CurrentTarget);
        Assert.Equal(1, h.AttackSends);              // one swing, not four
        Assert.Equal("a cave lizard", h.LastSent);
    }

    [Fact]
    public void SurvivorReReveals_AfterDedupedMobDies_NeitherStuckNorPhantom()
    {
        // Two cave lizards share the dark room, but their attack lines are
        // indistinguishable, so they dedupe to one injected entity. We kill the
        // first and the roster empties. The survivor's next swing re-reveals it,
        // re-engaging — proving the one-entity view neither strands a real second
        // mob (under-count self-heals) nor leaves a phantom to chase once the room
        // is genuinely clear.
        using Harness h = new();
        h.AddMonster(1, "cave lizard");
        h.EnterDarkRoom();

        h.Feed("The cave lizard swings at you.");
        h.Feed("The cave lizard swings at you.");     // 2nd lizard, same name → deduped
        Assert.Equal(1, h.AttackSends);
        Assert.Equal("cave lizard", h.Combat.CurrentTarget);

        // First lizard dies.
        // A kill as the client sees one: the exp line, the kill's *Combat Off*, and
        // the death event AppServices routes to NoteUnattributedDeath.
        h.Feed("You gain 10 experience.");
        h.Feed("*Combat Off*");
        h.Combat.NoteUnattributedDeath();
        Assert.Null(h.Combat.CurrentTarget);          // roster empty, target cleared

        // The surviving lizard swings again — re-revealed, re-engaged.
        h.Feed("The cave lizard swings at you.");
        Assert.Equal("cave lizard", h.Combat.CurrentTarget);
        Assert.Equal(2, h.AttackSends);               // one swing per genuine engage
    }

    [Fact]
    public void TwoDistinctlyNamedMobs_InDark_BothEngagedInTurn()
    {
        // In the dark, a distinct name IS a distinct enemy: we're swung at by a
        // "cave lizard" while a partner announces "small cave lizard". Even if they
        // shared a monster record, these are two separate mobs — dedupe keys on the
        // name string, so BOTH inject and both are engaged in turn. (Collapsing them
        // by monster number would silently drop the second, live enemy.)
        using Harness h = new();
        h.AddMonster(1, "cave lizard");
        h.AddMonster(2, "small cave lizard");
        h.EnterDarkRoom();

        h.Feed("The cave lizard swings at you.");
        h.Feed("Tristian moves to attack small cave lizard.");

        Assert.Equal(2, h.Classifier.Current!.Value.Entities.Count);   // two distinct enemies
        Assert.Equal("cave lizard", h.Combat.CurrentTarget);           // engaged first
        Assert.Equal(1, h.AttackSends);                                // survivor not thrashed to

        // The first lizard dies; the re-pick engages the still-present second mob.
        // A kill as the client sees one: the exp line, the kill's *Combat Off*, and
        // the death event AppServices routes to NoteUnattributedDeath.
        h.Feed("You gain 10 experience.");
        h.Feed("*Combat Off*");
        h.Combat.NoteUnattributedDeath();

        Assert.Equal("small cave lizard", h.Combat.CurrentTarget);
        Assert.Equal(2, h.AttackSends);
    }
}
