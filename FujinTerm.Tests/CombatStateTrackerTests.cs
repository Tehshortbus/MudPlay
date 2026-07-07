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
        public Dictionary<int, MonsterOverlay> Overlays { get; } = new();

        // ----- seehidden clear override (PR 4.c-b) -------------------
        public bool ClearWhenSeenHidden { get; set; }
        public bool AutoSneakEnabled { get; set; }
        public HashSet<int> SeeHidden { get; } = new();

        public void WireSeeHiddenGate() =>
            Tracker.SetSeeHiddenClearGate(
                clearWhenSeenHidden: () => ClearWhenSeenHidden,
                isAutoSneakEnabled:  () => AutoSneakEnabled,
                hasSeeHidden:        n => SeeHidden.Contains(n));

        // ----- actionability gate (PR-iii) ---------------------------
        // Monster Numbers we declare un-actionable (no weapon hits, every
        // attack spell level-blocked). The gate holds only while at least
        // one engageable hostile is NOT in this set.
        public HashSet<int> Unactionable { get; } = new();

        public void WireActionabilityGate() =>
            Tracker.SetActionabilityGate(n => !Unactionable.Contains(n));

        public Harness()
        {
            Router = new MessageRouter();
            DefaultPatterns.Seed(Router);
            Coordinator = new MovementCoordinator(Log);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log);
            Tracker = new CombatStateTracker(
                Router, Coordinator, Classifier, Monsters, State,
                () => AutoAttackEnabled,
                resolveOverlay: n => Overlays.TryGetValue(n, out MonsterOverlay? o)
                                     ? o : new MonsterOverlay(),
                log: Log);
        }

        public void SetOverlay(int number, MonsterRelationship? relationship = null,
                               MonsterAttackPriority? priority = null)
        {
            Overlays[number] = new MonsterOverlay
            {
                Relationship = relationship,
                Priority     = priority,
            };
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
    public void ShopkeeperFlaggedFriend_DoesNotAssertGate()
    {
        using Harness h = new();
        h.AddMonster(7, "shopkeeper", killable: true);
        h.SetOverlay(7, relationship: MonsterRelationship.Friend);

        h.Feed("Also here: shopkeeper.");

        Assert.False(h.CombatGateHeld);
    }

    [Fact]
    public void UnknownToDataMonster_EmptyDeathLine_AssertsGate()
    {
        // Acid-slime regression: empty DeathLine in monster messages
        // doesn't mean unkillable; the engageable predicate now
        // ignores it.
        using Harness h = new();
        h.AddMonster(99, "acid slime", killable: false);

        h.Feed("Also here: acid slime.");

        Assert.True(h.CombatGateHeld);
    }

    [Fact]
    public void MixedHostileAndFriendly_AssertsOnHostile()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat",  killable: true);
        h.AddMonster(7, "shopkeeper", killable: true);
        h.SetOverlay(7, relationship: MonsterRelationship.Friend);

        h.Feed("Also here: giant rat, shopkeeper.");

        Assert.True(h.CombatGateHeld);
    }

    // ----- HasHostileMonster — auto-attack-independent danger signal ----

    [Fact]
    public void HasHostileMonster_TrueWithHostile_EvenWhenAutoAttackOff()
    {
        // The gate short-circuits off when auto-attack is disabled, so a manual
        // player never asserts it — but the room is still dangerous. The
        // emergency-hangup signal must report the hostile regardless.
        using Harness h = new() { AutoAttackEnabled = false };
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");

        Assert.False(h.CombatGateHeld);            // gate stays down (auto-attack off)
        Assert.False(h.Tracker.HasEngageableHostiles);
        Assert.True(h.Tracker.HasHostileMonster);  // but the danger is real
    }

    [Fact]
    public void HasHostileMonster_FalseWhenRoomClear()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");                 // player only
        Assert.False(h.Tracker.HasHostileMonster);
    }

    [Fact]
    public void HasHostileMonster_FalseWithOnlyFriendly()
    {
        using Harness h = new();
        h.AddMonster(7, "shopkeeper", killable: true);
        h.SetOverlay(7, relationship: MonsterRelationship.Friend);

        h.Feed("Also here: shopkeeper.");

        Assert.False(h.Tracker.HasHostileMonster);
    }

    [Fact]
    public void HasHostileMonster_ClearsWhenRoomCleared()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);
        h.Feed("Also here: giant rat.");
        Assert.True(h.Tracker.HasHostileMonster);

        h.Feed("Also here: Bob.");                 // moved on — room clear
        Assert.False(h.Tracker.HasHostileMonster);
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

    // ----- HasRoomNpc (PR 4.b decision #1) ---------------------------

    [Fact]
    public void HasRoomNpc_NoMonsters_False()
    {
        using Harness h = new();
        h.Feed("Also here: Bob.");          // player only
        Assert.False(h.Tracker.HasRoomNpc);
    }

    [Fact]
    public void HasRoomNpc_FriendlyNpc_True()
    {
        // Friendly NPC doesn't assert the combat gate, but still blocks
        // sneak — HasRoomNpc must be true even though HasEngageableHostiles
        // is false.
        using Harness h = new();
        h.AddMonster(7, "shopkeeper", killable: true);
        h.SetOverlay(7, relationship: MonsterRelationship.Friend);

        h.Feed("Also here: shopkeeper.");

        Assert.True(h.Tracker.HasRoomNpc);
        Assert.False(h.Tracker.HasEngageableHostiles);
    }

    [Fact]
    public void HasRoomNpc_Hostile_True()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");

        Assert.True(h.Tracker.HasRoomNpc);
    }

    [Fact]
    public void HasRoomNpc_TracksEvenWhenAutoAttackDisabled()
    {
        // The auto-attack gate early-returns, but NPC-presence tracking
        // runs first so the sneak-block signal stays live.
        using Harness h = new() { AutoAttackEnabled = false };
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");

        Assert.True(h.Tracker.HasRoomNpc);
        Assert.False(h.CombatGateHeld);
    }

    [Fact]
    public void HasRoomNpc_ClearsWhenRoomEmpties()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        Assert.True(h.Tracker.HasRoomNpc);

        h.Feed("Also here: Bob.");          // moved on, players only
        Assert.False(h.Tracker.HasRoomNpc);
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

    // ----- seehidden clear override (PR 4.c-b) -----------------------

    [Fact]
    public void SeeHiddenOverride_CombatOff_AssertsGateAndLatches()
    {
        // Stealth runner: combat OFF, AutoSneak ON, toggle ON, the room
        // holds a SeeHidden monster → force-clear engages, holding the
        // walker gate so the runner stops instead of dragging mobs.
        using Harness h = new() { AutoAttackEnabled = false };
        h.WireSeeHiddenGate();
        h.ClearWhenSeenHidden = true;
        h.AutoSneakEnabled = true;
        h.SeeHidden.Add(1);
        h.AddMonster(1, "crystal golem", killable: true);

        h.Feed("Also here: crystal golem.");

        Assert.True(h.CombatGateHeld);
        Assert.True(h.Tracker.SeeHiddenClearActive);
    }

    [Fact]
    public void SeeHiddenOverride_HoldsUntilRoomCleared_ThenReleases()
    {
        // Latch must hold through the kill of the SeeHidden monster and
        // keep clearing any other hostiles, releasing only when the room
        // is empty of engageables.
        using Harness h = new() { AutoAttackEnabled = false };
        h.WireSeeHiddenGate();
        h.ClearWhenSeenHidden = true;
        h.AutoSneakEnabled = true;
        h.SeeHidden.Add(1);
        h.AddMonster(1, "crystal golem", killable: true);
        h.AddMonster(2, "giant rat", killable: true);

        h.Feed("Also here: crystal golem, giant rat.");
        Assert.True(h.CombatGateHeld);
        Assert.True(h.Tracker.SeeHiddenClearActive);

        // SeeHidden monster dead; a non-seehidden hostile remains. Latch
        // must STILL hold (room not empty) even though no seehidden mob
        // is present this observation.
        h.Feed("Also here: giant rat.");
        Assert.True(h.CombatGateHeld);
        Assert.True(h.Tracker.SeeHiddenClearActive);

        // Room cleared → latch releases, gate clears.
        h.Feed("Also here: Bob.");
        Assert.False(h.CombatGateHeld);
        Assert.False(h.Tracker.SeeHiddenClearActive);
    }

    [Fact]
    public void SeeHiddenOverride_Dormant_WhenToggleOff()
    {
        using Harness h = new() { AutoAttackEnabled = false };
        h.WireSeeHiddenGate();
        h.ClearWhenSeenHidden = false;        // user opted out
        h.AutoSneakEnabled = true;
        h.SeeHidden.Add(1);
        h.AddMonster(1, "crystal golem", killable: true);

        h.Feed("Also here: crystal golem.");

        Assert.False(h.CombatGateHeld);
        Assert.False(h.Tracker.SeeHiddenClearActive);
    }

    [Fact]
    public void SeeHiddenOverride_Dormant_WhenAutoSneakOff()
    {
        // Not stealthing the route → no override (a non-stealth runner
        // wouldn't be re-sneaking anyway).
        using Harness h = new() { AutoAttackEnabled = false };
        h.WireSeeHiddenGate();
        h.ClearWhenSeenHidden = true;
        h.AutoSneakEnabled = false;
        h.SeeHidden.Add(1);
        h.AddMonster(1, "crystal golem", killable: true);

        h.Feed("Also here: crystal golem.");

        Assert.False(h.CombatGateHeld);
        Assert.False(h.Tracker.SeeHiddenClearActive);
    }

    [Fact]
    public void SeeHiddenOverride_Dormant_WhenNoSeeHiddenMonster()
    {
        // Room has hostiles but none see hidden → sneak still works, no
        // override needed.
        using Harness h = new() { AutoAttackEnabled = false };
        h.WireSeeHiddenGate();
        h.ClearWhenSeenHidden = true;
        h.AutoSneakEnabled = true;
        // SeeHidden set empty.
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");

        Assert.False(h.CombatGateHeld);
        Assert.False(h.Tracker.SeeHiddenClearActive);
    }

    [Fact]
    public void SeeHiddenOverride_DropsLatch_WhenAutoAttackTurnedOn()
    {
        // Latched in combat-off, then user flips master auto-attack on:
        // normal gate logic owns pausing, the override latch drops.
        using Harness h = new() { AutoAttackEnabled = false };
        h.WireSeeHiddenGate();
        h.ClearWhenSeenHidden = true;
        h.AutoSneakEnabled = true;
        h.SeeHidden.Add(1);
        h.AddMonster(1, "crystal golem", killable: true);

        h.Feed("Also here: crystal golem.");
        Assert.True(h.Tracker.SeeHiddenClearActive);

        h.AutoAttackEnabled = true;
        h.Feed("Also here: crystal golem.");
        Assert.False(h.Tracker.SeeHiddenClearActive);
        Assert.True(h.CombatGateHeld);        // normal gate now holds it
    }

    [Fact]
    public void SeeHiddenOverride_NotWired_BehavesAsBefore()
    {
        // Without SetSeeHiddenClearGate, combat-off never holds the gate.
        using Harness h = new() { AutoAttackEnabled = false };
        h.AddMonster(1, "crystal golem", killable: true);

        h.Feed("Also here: crystal golem.");

        Assert.False(h.CombatGateHeld);
        Assert.False(h.Tracker.SeeHiddenClearActive);
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
        Assert.Contains("actionable=1/1", last.Value.Reason);
        Assert.Contains("first=giant rat", last.Value.Reason);
    }

    // ----- actionability gate (PR-iii) -------------------------------

    [Fact]
    public void SoleHostileUnactionable_ReleasesGate()
    {
        using Harness h = new();
        h.WireActionabilityGate();
        h.AddMonster(1, "stone golem", killable: true);
        h.Unactionable.Add(1);              // can't hit, no eligible spell

        h.Feed("Also here: stone golem.");

        // Engageable but un-actionable → walker moves past, gate not held.
        Assert.False(h.CombatGateHeld);
    }

    [Fact]
    public void ActionablePlusUnactionable_HoldsGate()
    {
        using Harness h = new();
        h.WireActionabilityGate();
        h.AddMonster(1, "stone golem", killable: true);
        h.AddMonster(2, "giant rat", killable: true);
        h.Unactionable.Add(1);              // golem un-actionable; rat is fine

        h.Feed("Also here: stone golem, giant rat.");

        // One killable hostile remains → gate holds so we stop to fight it.
        Assert.True(h.CombatGateHeld);
    }

    [Fact]
    public void Unwired_AllHostilesActionable_HoldsGate()
    {
        using Harness h = new();
        // No WireActionabilityGate — fail-open: every engageable hostile
        // counts as actionable, exactly the pre-PR-iii behavior.
        h.AddMonster(1, "stone golem", killable: true);

        h.Feed("Also here: stone golem.");

        Assert.True(h.CombatGateHeld);
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
    public void CombatStatus_Off_DoesNotFlipInCombatFalse()
    {
        // Server emits *Combat Off* whenever auto-attack stops for
        // any reason — including spell-casts mid-round with the mob
        // still alive. We must NOT treat that as "out of combat" or
        // HealthManager would start resting next to a hostile. The
        // authoritative end-of-combat signal is the room going clear
        // of engageable monsters (covered by Off_RoomClear_Flips...).
        using Harness h = new();
        h.Feed("*Combat Engaged*");
        Assert.True(h.State.InCombat);

        h.Feed("*Combat Off*");
        Assert.True(h.State.InCombat);     // unchanged — mob still presumed alive
    }

    [Fact]
    public void RoomClear_FlipsInCombatFalse()
    {
        // Authoritative end-of-combat: when the classifier emits an
        // observation with no engageable monsters, InCombat goes
        // false. CombatStateTracker.OnEntitiesObserved's "room
        // cleared" branch owns this transition.
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");          // assert gate + InCombat reflects fight
        h.Feed("*Combat Engaged*");
        Assert.True(h.State.InCombat);

        h.Feed("Also here: Bob.");                // monster gone — only player
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
