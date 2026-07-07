using System.IO;
using FujinTerm.Game.Combat;
using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="DarkRoomCombatWatcher"/> — engages a monster that shares a dark
/// room with us, revealed only by its dark-cyan attack line (no "Also here:"
/// ever lists it). Covers the inject-on-attack path, the dark-room gate, the
/// unknown-attacker skip, per-round dedupe, and the "Your command had no
/// effect." retraction.
/// </summary>
public sealed class DarkRoomCombatWatcherTests
{
    private sealed class Harness : IDisposable
    {
        private readonly string _root;
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new();
        public LogService Log { get; } = new();
        public RoomTracker Tracker { get; }
        public RoomEntityClassifier Classifier { get; }
        public DarkRoomCombatWatcher Watcher { get; }
        public List<RoomEntitiesObservation> Observations { get; } = new();
        public string? CurrentTarget;

        public Harness()
        {
            _root = Path.Combine(Path.GetTempPath(),
                "fujinterm-darkcombat-tests-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_root, "alpha"));
            File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), "[]");
            GameDataCache cache = new(_root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");
            Tracker = new RoomTracker(graph);

            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log);
            Classifier.EntitiesObserved += Observations.Add;
            Watcher = new DarkRoomCombatWatcher(
                Router, Tracker, Classifier,
                currentTarget: () => CurrentTarget,
                log: Log);
        }

        // Arm RoomTracker.IsInDarkRoom the same way the wire does — a darkness
        // line with no pending move just flags the flag and holds.
        public void EnterDarkRoom() => Tracker.NoteDarkRoomEntered();

        public void AddMonster(int number, string name)
        {
            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}",
                Name: name,
                HitYou: Array.Empty<string>(),
                HitOther: Array.Empty<string>(),
                DeathLine: new[] { $"The {name} dies." },
                ArmorBlockYou: Array.Empty<string>(),
                ArmorBlockOther: Array.Empty<string>(),
                DodgeYou: Array.Empty<string>(),
                DodgeOther: Array.Empty<string>(),
                MissYou: Array.Empty<string>(),
                MissOther: Array.Empty<string>(),
                FlavorPrefixes: Array.Empty<string>(),
                AllowNoPrefix: true,
                Links: new[] { new GameDataLink("Monsters", number) }));
        }

        public void Feed(string line)
        {
            LineExtractor.EmittedLine emitted = new(
                line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false);
            Router.Dispatch(emitted);
        }

        public void Dispose()
        {
            Watcher.Dispose();
            Classifier.Dispose();
            try { Directory.Delete(_root, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    // ----- inject on attack line ---------------------------------------

    [Fact]
    public void KnownMonsterMissLine_InDark_InjectsForCombat()
    {
        using Harness h = new();
        h.AddMonster(1, "cave bear");
        h.EnterDarkRoom();

        h.Feed("The cave bear swings at you.");

        Assert.Single(h.Observations);
        RoomEntity injected = Assert.Single(h.Classifier.Current!.Value.Entities);
        Assert.Equal(EntityKind.Monster, injected.Kind);
        Assert.Equal("cave bear", injected.ResolvedName);
        Assert.Equal(1, injected.MonsterNumber);
    }

    [Fact]
    public void KnownMonsterHitLine_InDark_InjectsForCombat()
    {
        using Harness h = new();
        h.AddMonster(1, "cave bear");
        h.EnterDarkRoom();

        h.Feed("The cave bear claws you for 12 damage!");

        Assert.Single(h.Observations);
        RoomEntity injected = Assert.Single(h.Classifier.Current!.Value.Entities);
        Assert.Equal("cave bear", injected.ResolvedName);
        Assert.Equal(1, injected.MonsterNumber);
    }

    // ----- dark-room gate ----------------------------------------------

    [Fact]
    public void AttackLine_NotInDark_Ignored()
    {
        // In a lit room the normal "Also here:" pipeline owns the target list.
        // The watcher must never fabricate one off an attack line here.
        using Harness h = new();
        h.AddMonster(1, "cave bear");

        h.Feed("The cave bear swings at you.");

        Assert.Empty(h.Observations);
        Assert.Null(h.Classifier.Current);
    }

    [Fact]
    public void UnknownAttacker_InDark_NotInjected()
    {
        // An attacker with no Monsters-table link can't be auto-engaged
        // (CombatManager skips null-number monsters), so injecting one would
        // hold the gate without ever attacking — leave it out entirely.
        using Harness h = new();
        h.EnterDarkRoom();

        h.Feed("The mystery beast growls at you.");

        Assert.Empty(h.Observations);
        Assert.Null(h.Classifier.Current);
    }

    // ----- per-round dedupe --------------------------------------------

    [Fact]
    public void RepeatedAttackLine_InDark_InjectsOnce()
    {
        // A dark room re-emits the same mob's attack line every round. Once it's
        // in the observation, re-appending would pile duplicates — skip it.
        using Harness h = new();
        h.AddMonster(1, "cave bear");
        h.EnterDarkRoom();

        h.Feed("The cave bear swings at you.");
        h.Feed("The cave bear claws you for 8 damage!");
        h.Feed("The cave bear swings at you.");

        Assert.Single(h.Observations);
        Assert.Single(h.Classifier.Current!.Value.Entities);
    }

    // ----- "Your command had no effect." retraction --------------------

    [Fact]
    public void CommandNoEffect_InDark_WithTarget_RetractsEntity()
    {
        // We injected the mob off its attack line; it has since died / fled with
        // no visible death line. "Your command had no effect." is the game's tell
        // that our attack hit nobody — drop the phantom so combat stops swinging.
        using Harness h = new();
        h.AddMonster(1, "cave bear");
        h.EnterDarkRoom();
        h.Feed("The cave bear swings at you.");
        Assert.Single(h.Classifier.Current!.Value.Entities);

        h.CurrentTarget = "cave bear";
        h.Feed("Your command had no effect.");

        Assert.Empty(h.Classifier.Current!.Value.Entities);
        Assert.Equal(2, h.Observations.Count);      // inject + retract
    }

    [Fact]
    public void CommandNoEffect_NotInDark_DoesNotRetract()
    {
        // In a lit room the same line is the dropped / mortally-wounded bounce;
        // it must not retract a real "Also here:"-listed occupant.
        using Harness h = new();
        h.AddMonster(1, "cave bear");
        h.Feed("Also here: cave bear.");            // lit-room occupant list
        Assert.Single(h.Classifier.Current!.Value.Entities);

        h.CurrentTarget = "cave bear";
        h.Feed("Your command had no effect.");

        Assert.Single(h.Classifier.Current!.Value.Entities);
        Assert.Single(h.Observations);               // no retraction re-fire
    }
}
