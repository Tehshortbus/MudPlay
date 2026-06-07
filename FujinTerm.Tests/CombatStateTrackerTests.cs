using FujinTerm.Game;
using FujinTerm.Game.Combat;
using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.0b sub-C — <see cref="CombatStateTracker"/> Combat-gate
/// room-clear semantics + <see cref="PlayerState.InCombat"/> drive.
/// </summary>
public sealed class CombatStateTrackerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; }
        public MovementCoordinator Coordinator { get; }
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new();
        public PlayerState State { get; } = new();
        public LogService Log { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public CombatStateTracker Tracker { get; }

        public bool AutoAttackEnabled { get; set; } = true;

        public Harness()
        {
            Router = new MessageRouter();
            DefaultPatterns.Seed(Router);
            Coordinator = new MovementCoordinator(Log);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log);
            Tracker = new CombatStateTracker(
                Router, Coordinator, Classifier, Monsters, State,
                () => AutoAttackEnabled, Log);
        }

        public void AddMonster(int number, string name, bool killable,
                               bool allowNoPrefix = true,
                               params string[] flavorPrefixes)
        {
            // killable = DeathLine populated. Shopkeepers / quest-givers
            // pass killable=false to test the engageability gate.
            string[] deathLines = killable
                ? new[] { $"The {name} dies." }
                : Array.Empty<string>();
            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}",
                Name: name,
                HitYou: Array.Empty<string>(),
                HitOther: Array.Empty<string>(),
                DeathLine: deathLines,
                ArmorBlockYou: Array.Empty<string>(),
                ArmorBlockOther: Array.Empty<string>(),
                DodgeYou: Array.Empty<string>(),
                DodgeOther: Array.Empty<string>(),
                MissYou: Array.Empty<string>(),
                MissOther: Array.Empty<string>(),
                FlavorPrefixes: flavorPrefixes,
                AllowNoPrefix: allowNoPrefix,
                Links: new[] { new GameDataLink("Monsters", number) }));
        }

        public void Feed(string line)
        {
            LineExtractor.EmittedLine emitted = new(
                line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false);
            Router.Dispatch(emitted);
        }

        public bool CombatGateHeld =>
            Coordinator.AssertedGates.Contains(MovementCoordinator.CombatGate);

        public void Dispose()
        {
            Tracker.Dispose();
            Classifier.Dispose();
        }
    }

    // ----- Combat gate — room-clear semantics ------------------------

    [Fact]
    public void NoMonsters_GateStaysClear()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");          // player only
        Assert.False(h.CombatGateHeld);
    }

    [Fact]
    public void OneKillableMonster_AssertsGate()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");

        Assert.True(h.CombatGateHeld);
    }

    [Fact]
    public void ShopkeeperOnly_DoesNotAssertGate()
    {
        using Harness h = new();
        h.AddMonster(7, "shopkeeper", killable: false);

        h.Feed("Also here: shopkeeper.");

        Assert.False(h.CombatGateHeld);
    }

    [Fact]
    public void MixedKillableAndFriendly_AssertsOnKillable()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat",  killable: true);
        h.AddMonster(7, "shopkeeper", killable: false);

        h.Feed("Also here: giant rat, shopkeeper.");

        Assert.True(h.CombatGateHeld);
    }

    [Fact]
    public void RoomClearedAfterPreviouslyKillable_ClearsGate()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        Assert.True(h.CombatGateHeld);

        // Walk into a new room with no monsters → classifier emits
        // empty entity list → gate clears.
        h.Feed("Also here: Bob.");
        Assert.False(h.CombatGateHeld);
    }

    [Fact]
    public void StillKillableInRoom_GateRemainsAsserted()
    {
        // Mid-fight: room re-displays with the remaining mob.
        // Gate must stay asserted across the re-display, NOT clear
        // and re-assert (that would burn two history entries).
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        Assert.True(h.CombatGateHeld);
        int historyAfterFirst = h.Coordinator.History.Count;

        h.Feed("Also here: giant rat.");
        Assert.True(h.CombatGateHeld);
        Assert.Equal(historyAfterFirst, h.Coordinator.History.Count);
    }

    // ----- master switch ---------------------------------------------

    [Fact]
    public void AutoAttackDisabled_GateNeverAsserts()
    {
        using Harness h = new() { AutoAttackEnabled = false };
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");

        Assert.False(h.CombatGateHeld);
    }

    [Fact]
    public void AutoAttackDisabledMidFight_ClearsGate()
    {
        // Gate asserted, user flips master toggle off, next classifier
        // observation clears the gate defensively.
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        Assert.True(h.CombatGateHeld);

        h.AutoAttackEnabled = false;
        h.Feed("Also here: giant rat.");
        Assert.False(h.CombatGateHeld);
    }

    // ----- gate-history Asserter is set ------------------------------

    [Fact]
    public void GateHistoryRecordsAsserterAndReason()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");

        GateTransitionEntry? last = h.Coordinator.History
            .FirstOrDefault(e => e.Gate == MovementCoordinator.CombatGate);
        Assert.NotNull(last);
        Assert.True(last!.Value.Asserted);
        Assert.Equal(CombatStateTracker.AsserterName, last.Value.Asserter);
        Assert.Contains("targetable=1", last.Value.Reason);
        Assert.Contains("first=giant rat", last.Value.Reason);
    }

    // ----- PlayerState.InCombat drive --------------------------------

    [Fact]
    public void InCombat_StartsFalse()
    {
        using Harness h = new();
        Assert.False(h.State.InCombat);
    }

    [Fact]
    public void CombatStatus_EngagedFlipsInCombatTrue()
    {
        using Harness h = new();
        h.Feed("*Combat Engaged*");
        Assert.True(h.State.InCombat);
    }

    [Fact]
    public void CombatStatus_OffFlipsInCombatFalse()
    {
        using Harness h = new();
        h.Feed("*Combat Engaged*");
        Assert.True(h.State.InCombat);

        h.Feed("*Combat Off*");
        Assert.False(h.State.InCombat);
    }

    [Fact]
    public void UserHitsLine_FlipsInCombatTrue()
    {
        // Damage-line drive: even before CombatStatus arrives, observing
        // a damage line flips us to InCombat=true. Pattern shape:
        // "<source> [critically ]<verb> <target> for N damage!" with the
        // bang anchor at end.
        using Harness h = new();
        h.Feed("Fujin hits a giant rat for 5 damage!");
        Assert.True(h.State.InCombat);
    }

    [Fact]
    public void MobHitsLine_FlipsInCombatTrue()
    {
        // Pattern shape: "The <target> <verb> you for N damage!"
        using Harness h = new();
        h.Feed("The giant rat bites you for 3 damage!");
        Assert.True(h.State.InCombat);
    }

    [Fact]
    public void MobMissesLine_FlipsInCombatTrue()
    {
        // Pattern shape: "The <target> <verb> at you" — bare "at you"
        // without trailing punctuation matters to the regex.
        using Harness h = new();
        h.Feed("The giant rat swings at you");
        Assert.True(h.State.InCombat);
    }
}
