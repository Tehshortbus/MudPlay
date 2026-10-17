using System.IO;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// DarkRoomCombatWatcher — engages a monster that shares a dark
// room with us, revealed only by its dark-cyan attack line (no "Also here:"
// ever lists it). Covers the inject-on-attack path, the dark-room gate, the
// unknown-attacker skip, per-round dedupe, and the "Your command had no
// effect." retraction.
public sealed class DarkRoomCombatWatcherTests
{
    private sealed class Harness : IDisposable
    {
        private readonly string _root;
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new(new ProfileService(), () => null);
        public LogService Log { get; } = new();
        public RoomTracker Tracker { get; }
        public RoomEntityClassifier Classifier { get; }
        public DarkRoomCombatWatcher Watcher { get; }
        public List<RoomEntitiesObservation> Observations { get; } = new();
        public string? CurrentTarget;

        public Harness()
        {
            _root = Path.Combine(Path.GetTempPath(),
                "mudplay-darkcombat-tests-" + Path.GetRandomFileName());
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

    // The reported session, verbatim from the move that put the player in the
    // dark tunnel.
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
    public void ReportedSession_RevealsOnTheFirstSwing()
    {
        // The monster swings before it lands anything, and MobMisses is specific
        // enough to act on at once, so the reveal comes on that line — index 11,
        // the first of the attack.
        using Harness h = new();
        h.AddMonster(963, "bugbear captain");
        h.EnterDarkRoom();
        int revealedAt = -1;

        for (int i = 0; i < Capture.Length; i++)
        {
            h.Feed(Capture[i]);
            if (revealedAt < 0 && h.Observations.Count > 0) revealedAt = i;
        }

        Assert.Equal(11, revealedAt);
        Assert.Equal("bugbear captain", h.Watcher.LastRevealName);
        RoomEntity injected = Assert.Single(h.Classifier.Current!.Value.Entities);
        Assert.Equal("bugbear captain", injected.ResolvedName);
        Assert.Equal(963, injected.MonsterNumber);
    }

    [Fact]
    public void ReportedSession_InALitRoom_RevealsNothing()
    {
        // Same wire, no darkness flag. In a lit room "Also here:" is
        // authoritative and this watcher must not fabricate a target, because
        // nothing here would withdraw it.
        using Harness h = new();
        h.AddMonster(963, "bugbear captain");

        foreach (string line in Capture) h.Feed(line);

        Assert.Empty(h.Observations);
        Assert.Null(h.Watcher.LastRevealName);
    }

    [Fact]
    public void TheHitLinesName_IsReadOffTheLine_NotTheCaptureGroup()
    {
        // MobHits captures "bugbear captain all-out" out of this sentence, which
        // resolves to nothing. The longest prefix that names a monster does.
        using Harness h = new();
        h.AddMonster(963, "bugbear captain");
        h.EnterDarkRoom();

        h.Feed("The bugbear captain all-out cleaves you for 20 damage!");

        Assert.Equal("bugbear captain", h.Watcher.LastRevealName);
    }

    [Fact]
    public void TheLongestNameWins_NotTheShorterOneThatAlsoExists()
    {
        using Harness h = new();
        h.AddMonster(900, "bugbear");
        h.AddMonster(963, "bugbear captain");
        h.EnterDarkRoom();

        h.Feed("The bugbear captain all-out cleaves you for 20 damage!");

        Assert.Equal("bugbear captain", h.Watcher.LastRevealName);
    }

    [Fact]
    public void AProperName_WithNoArticle_IsRevealedOnARepeatedSwing()
    {
        // "Goru-Nezar swings at you!" carries no article, so MobMisses cannot see
        // it. The article-optional swing pattern can, and being loose it needs the
        // same attacker twice inside a round.
        using Harness h = new();
        h.AddMonster(311, "Goru-Nezar");
        h.EnterDarkRoom();

        h.Feed("Goru-Nezar swings at you!");
        Assert.Empty(h.Observations);

        h.Feed("Goru-Nezar swings at you!");
        Assert.Equal("Goru-Nezar", h.Watcher.LastRevealName);
    }

    [Fact]
    public void AnEmoteFromANonMonster_RevealsNothing_EvenRepeated()
    {
        // The swing shape is also the shape of an emote. Repetition alone is not
        // enough: it still has to resolve to a monster in the game data.
        using Harness h = new();
        h.EnterDarkRoom();

        h.Feed("The barmaid smiles at you.");
        h.Feed("The barmaid smiles at you.");

        Assert.Empty(h.Observations);
        Assert.NotNull(h.Watcher.LastHeldOffReason);
    }

    [Fact]
    public void TheUncontractedRefusal_RetractsTheTarget()
    {
        // The realm prints "You do not see <X> here!". A retraction that only
        // listened for the contraction left the phantom in place, and
        // AlreadyPresent then suppressed every later reveal for that room.
        using Harness h = new();
        h.AddMonster(963, "bugbear captain");
        h.EnterDarkRoom();
        h.Feed("The bugbear captain swings at you with their greataxe!");
        Assert.Single(h.Classifier.Current!.Value.Entities);

        h.CurrentTarget = "bugbear captain";
        h.Feed("You do not see bugbear captain here!");

        Assert.Empty(h.Classifier.Current!.Value.Entities);
    }

    [Fact]
    public void ARefusalNamingSomethingElse_LeavesTheTarget()
    {
        // The same refusal answers a cast at a hiding party member and a `get` that
        // found nothing. Neither means the monster we're fighting has gone.
        using Harness h = new();
        h.AddMonster(963, "bugbear captain");
        h.EnterDarkRoom();
        h.Feed("The bugbear captain swings at you with their greataxe!");
        h.CurrentTarget = "bugbear captain";

        h.Feed("You do not see Bob here!");
        h.Feed("You don't see rod here.");

        Assert.Single(h.Classifier.Current!.Value.Entities);
    }

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

    // ----- party leader's "moves to attack" reveal ---------------------

    [Fact]
    public void PartyAttackAnnounce_InDark_InjectsForCombat()
    {
        // Following a leader into a dark room, their "moves to attack" announce
        // (a same-room broadcast) names the monster the display can't. Keying off
        // it lets us follow suit before the mob first swings at us.
        using Harness h = new();
        h.AddMonster(1, "cave bear");
        h.EnterDarkRoom();

        h.Feed("Tristian moves to attack cave bear.");

        Assert.Single(h.Observations);
        RoomEntity injected = Assert.Single(h.Classifier.Current!.Value.Entities);
        Assert.Equal(EntityKind.Monster, injected.Kind);
        Assert.Equal("cave bear", injected.ResolvedName);
        Assert.Equal(1, injected.MonsterNumber);
    }

    [Fact]
    public void PartyAttackAnnounce_NotInDark_Ignored()
    {
        // A lit room already lists the monster under "Also here:"; the announce
        // must not fabricate a second target there.
        using Harness h = new();
        h.AddMonster(1, "cave bear");

        h.Feed("Tristian moves to attack cave bear.");

        Assert.Empty(h.Observations);
        Assert.Null(h.Classifier.Current);
    }

    [Fact]
    public void PartyAttackAnnounce_UnknownTarget_NotInjected()
    {
        // No Monsters-table link (or a PvP target that resolves to a Player) —
        // CombatManager can't engage it, so leave the entity list empty.
        using Harness h = new();
        h.EnterDarkRoom();

        h.Feed("Tristian moves to attack some stranger.");

        Assert.Empty(h.Observations);
        Assert.Null(h.Classifier.Current);
    }

    [Fact]
    public void PartyAttackThenMobAttack_SameMonster_InjectsOnce()
    {
        // The leader's announce and the mob's own attack line both name the same
        // monster in the same round — the per-round dedupe keeps it to one entry.
        using Harness h = new();
        h.AddMonster(1, "cave bear");
        h.EnterDarkRoom();

        h.Feed("Tristian moves to attack cave bear.");
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
    public void CommandNoEffect_InDark_AnsweringAnotherCommand_DoesNotRetract()
    {
        // The line answers whatever command was echoed ahead of it. A cast that
        // did nothing says nothing about the monster still swinging at us
        // (report stock-20261004-150645).
        using Harness h = new();
        h.AddMonster(1, "cave bear");
        h.EnterDarkRoom();
        h.Feed("The cave bear swings at you.");
        h.CurrentTarget = "cave bear";

        DateTimeOffset at = DateTimeOffset.UtcNow;
        h.Router.Dispatch(new LineExtractor.EmittedLine(
            "[HP=32/MA=39]:", Array.Empty<CellAttributes>(), at, IsPromptLine: true));
        h.Router.Dispatch(new LineExtractor.EmittedLine(
            "swan", Array.Empty<CellAttributes>(), at, IsPromptLine: false));
        h.Feed("Your command had no effect.");

        Assert.Single(h.Classifier.Current!.Value.Entities);
        Assert.Single(h.Observations);
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
