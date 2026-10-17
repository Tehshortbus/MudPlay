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

// CombatStateTracker Combat-gate
// room-clear semantics + PlayerState.InCombat drive.
public sealed class CombatStateTrackerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; }
        public MovementCoordinator Coordinator { get; }
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new(new ProfileService(), () => null);
        public PlayerState State { get; } = new();
        public LogService Log { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public CombatStateTracker Tracker { get; }

        public bool AutoAttackEnabled { get; set; } = true;
        public Dictionary<int, MonsterOverlay> Overlays { get; } = new();

        // Drives the dark-room probe: when true the idle-stall watchdog skips its
        // resync CR (a blind CR re-emits nothing and false-confirms the movement
        // loop's in-flight step). Default false → the watchdog resyncs as before.
        public bool Dark { get; set; }

        // ----- seehidden clear override ------------------------------
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

        // ----- monster count window (CombatManager parity) -----------
        public CombatSettings Settings { get; } = new();
        public bool MovementActive { get; set; } = true;

        public void WireMonsterCountWindow() =>
            Tracker.SetMonsterCountWindow(() => Settings);

        public void WireMovementActiveGate() =>
            Tracker.SetMovementActiveGate(() => MovementActive);

        // ----- break-before-run gate (auto-combat-off flee courtesy) -----
        // Commands the tracker pushes to the wire (decoded, trailing \r
        // stripped) and the BreakBeforeFleeing setting the gate reads.
        public List<string> Sent { get; } = new();
        // Raw wire bytes decoded without trimming — lets the watchdog test see a
        // bare "\r" that Sent would collapse to an empty entry.
        public List<string> SentRaw { get; } = new();
        public bool BreakBeforeRunning { get; set; }

        // Injected clock so the idle-stall watchdog's 6s threshold is testable
        // without wall-clock waits. Constant by default (existing tests never
        // advance it and never drive OnCombatTick, so the stamps stay inert).
        public DateTimeOffset FakeNow { get; set; } = DateTimeOffset.UnixEpoch;

        public void WireSender() =>
            Tracker.SetWireSender(bytes =>
            {
                Sent.Add(System.Text.Encoding.Latin1.GetString(bytes).TrimEnd('\r', '\n'));
                SentRaw.Add(System.Text.Encoding.Latin1.GetString(bytes));
            });

        // Stands in for CombatManager.CurrentTarget — non-null once we've swung.
        public bool AttackInFlight { get; set; }

        public void WireBreakBeforeRun()
        {
            WireSender();
            Tracker.SetBreakBeforeRunGate(() => BreakBeforeRunning);
            Tracker.SetAttackInFlightGate(() => AttackInFlight);
        }

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
                log: Log,
                clock: () => FakeNow);
            Tracker.SetDarkRoomProbe(() => Dark);
        }

        public void SetOverlay(int number, MonsterRelationship? relationship = null,
                               MonsterAttackPriority? priority = null,
                               bool? killOnSight = null)
        {
            Overlays[number] = new MonsterOverlay
            {
                Relationship = relationship,
                Priority     = priority,
                KillOnSight  = killOnSight,
            };
        }

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
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.True(h.CombatGateHeld);
    }

    [Fact]
    public void ResetCombatState_FiresCombatForceCleared_WithGateAlreadyDown()
    {
        // The force-clear (idle-stall watchdog / Reset States) must fire
        // CombatForceCleared AFTER dropping the gate, so a deferred cash/item
        // collect re-run from that event sees no engageable hostiles and flushes
        // instead of stranding the Acquisition gate (report paradigm-20260814-131551).
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");
        Assert.True(h.Tracker.HasEngageableHostiles);

        bool gateDownWhenFired = true;   // negated below if the event sees the gate still up
        bool fired = false;
        h.Tracker.CombatForceCleared += () =>
        {
            fired = true;
            gateDownWhenFired = !h.Tracker.HasEngageableHostiles;
        };

        h.Tracker.ResetCombatState("test force-clear");

        Assert.True(fired);
        Assert.True(gateDownWhenFired);
        Assert.False(h.Tracker.HasEngageableHostiles);
    }

    [Fact]
    public void ShopkeeperFlaggedFriend_DoesNotAssertGate()
    {
        using Harness h = new();
        h.AddMonster(7, "shopkeeper");
        h.SetOverlay(7, relationship: MonsterRelationship.Friend);

        h.Feed("Also here: shopkeeper.");

        Assert.False(h.CombatGateHeld);
    }

    [Fact]
    public void MixedHostileAndFriendly_AssertsOnHostile()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.AddMonster(7, "shopkeeper");
        h.SetOverlay(7, relationship: MonsterRelationship.Friend);

        h.Feed("Also here: giant rat, shopkeeper.");

        Assert.True(h.CombatGateHeld);
    }

    // A passive neutral doesn't hold the walker gate — until the user hand-engages it,
    // at which point it fights like a hostile and the gate must hold so the walker can't
    // stroll off mid-fight (report paradigm-20260814).
    [Fact]
    public void UserEngagedPassiveNeutral_HoldsGate()
    {
        using Harness h = new();
        h.AddMonster(1, "townsperson");
        h.SetOverlay(1, relationship: MonsterRelationship.Neutral);   // passive — no KillOnSight

        HashSet<string> engaged = new(StringComparer.OrdinalIgnoreCase);
        h.Tracker.SetUserEngagedInstanceGate(raw => engaged.Contains(raw));

        h.Feed("Also here: townsperson.");
        Assert.False(h.Tracker.HasEngageableHostiles);   // passive neutral left alone
        Assert.False(h.CombatGateHeld);

        engaged.Add("townsperson");
        h.Feed("Also here: townsperson.");
        Assert.True(h.Tracker.HasEngageableHostiles);    // now fights like a hostile
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
        h.AddMonster(1, "giant rat");

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
        h.AddMonster(7, "shopkeeper");
        h.SetOverlay(7, relationship: MonsterRelationship.Friend);

        h.Feed("Also here: shopkeeper.");

        Assert.False(h.Tracker.HasHostileMonster);
    }

    [Fact]
    public void KillOnSightNeutral_Engageable_ButNotAnAttackingHostile()
    {
        // A KillOnSight neutral IS a target (gate asserts so we engage it) but it
        // never attacks on sight — so it must NOT read as a hostile, which is what
        // the health rest-block keys on. That's what lets us rest between neutral
        // kills: the un-engaged ones don't block resting.
        using Harness h = new();
        h.AddMonster(9, "storm giant");
        h.SetOverlay(9, relationship: MonsterRelationship.Neutral, killOnSight: true);

        h.Feed("Also here: storm giant.");

        Assert.True(h.Tracker.HasEngageableHostiles);   // we do engage it
        Assert.False(h.Tracker.HasHostileMonster);      // but it isn't an on-sight attacker
    }

    [Fact]
    public void PassiveNeutral_NeitherEngageableNorHostile()
    {
        using Harness h = new();
        h.AddMonster(9, "storm giant");
        h.SetOverlay(9, relationship: MonsterRelationship.Neutral);   // no kill-on-sight

        h.Feed("Also here: storm giant.");

        Assert.False(h.Tracker.HasEngageableHostiles);
        Assert.False(h.Tracker.HasHostileMonster);
    }

    [Fact]
    public void UnresolvableMonster_NoRecordNumber_NotEngaged()
    {
        // A name that matches a message record with no Monsters link (a greet-only NPC like an
        // "old man" quest-giver), with no Monsters table to fall back to, resolves to no Number.
        // The engine must NOT proactively attack something it can't identify — fail closed —
        // rather than defaulting the unresolved entity to a fightable enemy. Regression: the
        // neutral-NPC on-sight-attack bug.
        using Harness h = new();
        h.Monsters.Messages.Add(new MonsterMessageRecord(Id: "OM", Name: "old man", Links: null));

        h.Feed("Also here: old man.");

        Assert.False(h.Tracker.HasEngageableHostiles);
        Assert.False(h.CombatGateHeld);
    }

    [Fact]
    public void HasHostileMonster_ClearsWhenRoomCleared()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");
        Assert.True(h.Tracker.HasHostileMonster);

        h.Feed("Also here: Bob.");                 // moved on — room clear
        Assert.False(h.Tracker.HasHostileMonster);
    }

    [Fact]
    public void RoomClearedAfterPreviouslyKillable_ClearsGate()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");

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
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.True(h.CombatGateHeld);
        int historyAfterFirst = h.Coordinator.History.Count;

        h.Feed("Also here: giant rat.");
        Assert.True(h.CombatGateHeld);
        Assert.Equal(historyAfterFirst, h.Coordinator.History.Count);
    }

    // ----- engaged-target abandonment on room change -----------------

    [Fact]
    public void RoomChangeWhileGated_FiresEngagedTargetAbandoned_AndClearsGate()
    {
        // An in-flight move confirmed and swapped rooms while the combat gate
        // was held on a live hostile — we left a fight we hadn't finished. The
        // tracker must signal abandonment (so the walker halts) yet still clear
        // the gate for the new room (holding would deadlock — an empty room
        // emits no further observation to release it).
        using Harness h = new();
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.True(h.CombatGateHeld);

        string? reason = null;
        h.Tracker.EngagedTargetAbandoned += r => reason = r;

        h.Classifier.NoteRoomChanged();          // move confirmed → synthetic wipe

        Assert.NotNull(reason);                  // abandonment signalled
        Assert.False(h.CombatGateHeld);          // gate cleared → no deadlock
    }

    [Fact]
    public void RosterEmptiedInPlaceWhileGated_DoesNotFireAbandonment()
    {
        // A room spell's kills cover every hostile listed, and the roster is dropped
        // where we stand, on the same wipe a move raises. Nobody left the room
        // (report paradigm-20261010-145330).
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        RoomKey? room = new RoomKey(14, 8647);
        h.Tracker.SetCurrentRoomProbe(() => room);

        h.Feed("Also here: giant rat, giant rat.");
        Assert.True(h.CombatGateHeld);

        bool fired = false;
        h.Tracker.EngagedTargetAbandoned += _ => fired = true;

        h.Classifier.NoteRoomChanged();

        Assert.False(fired);
        Assert.False(h.CombatGateHeld);
    }

    [Fact]
    public void RoomChangeWhileGated_WithRoomProbe_StillFiresAbandonment()
    {
        // The real case: a move already sent lands us in another room with the
        // hostile left alive behind us.
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        RoomKey? room = new RoomKey(14, 8647);
        h.Tracker.SetCurrentRoomProbe(() => room);

        h.Feed("Also here: giant rat.");
        Assert.True(h.CombatGateHeld);

        bool fired = false;
        h.Tracker.EngagedTargetAbandoned += _ => fired = true;

        room = new RoomKey(14, 8656);
        h.Classifier.NoteRoomChanged();

        Assert.True(fired);
        Assert.False(h.CombatGateHeld);
    }

    [Fact]
    public void GateHeldBeforeTheMoveConfirmed_ThenEmptiedInPlace_DoesNotFireAbandonment()
    {
        // A new room's "Also here:" holds the gate while the tracker still names the
        // room we left; the roster re-emitted on the confirm is what places the fight.
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        RoomKey? room = new RoomKey(14, 8638);
        h.Tracker.SetCurrentRoomProbe(() => room);

        h.Feed("Also here: giant rat.");          // seen before the exits line confirms the move
        room = new RoomKey(14, 8647);
        h.Feed("Also here: giant rat.");          // the confirmed room's roster, re-emitted
        Assert.True(h.CombatGateHeld);

        bool fired = false;
        h.Tracker.EngagedTargetAbandoned += _ => fired = true;

        h.Classifier.NoteRoomChanged();

        Assert.False(fired);
    }

    [Fact]
    public void PursuerFollowedUsIn_ThenEmptiedInPlace_DoesNotFireAbandonment()
    {
        // A pursuer kept across our move raises no observation in the new room; its
        // next swing at us is what says the fight is here.
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        RoomKey? room = new RoomKey(14, 8638);
        h.Tracker.SetCurrentRoomProbe(() => room);

        h.Feed("Also here: giant rat.");
        room = new RoomKey(14, 8647);             // we moved, and it came with us
        h.Feed("The giant rat bites you for 3 damage!");

        bool fired = false;
        h.Tracker.EngagedTargetAbandoned += _ => fired = true;

        h.Classifier.NoteRoomChanged();

        Assert.False(fired);
    }

    [Fact]
    public void WipeWithNoRoomKnown_StillFiresAbandonment()
    {
        // Our own death takes the tracker's room away before the roster is wiped:
        // with nowhere to compare, the wipe reads as leaving the fight, as it did.
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        RoomKey? room = new RoomKey(14, 8647);
        h.Tracker.SetCurrentRoomProbe(() => room);

        h.Feed("Also here: giant rat.");

        bool fired = false;
        h.Tracker.EngagedTargetAbandoned += _ => fired = true;

        room = null;
        h.Classifier.NoteRoomChanged();

        Assert.True(fired);
    }

    [Fact]
    public void DeathClearWhileGated_DoesNotFireAbandonment()
    {
        // Killing the last hostile empties the room via a Death observation —
        // that's a room we cleared by winning, NOT an abandoned fight. The
        // abandonment signal must stay silent so the walker keeps its route.
        using Harness h = new();
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.True(h.CombatGateHeld);

        bool fired = false;
        h.Tracker.EngagedTargetAbandoned += _ => fired = true;

        h.Classifier.RemoveDeadEntity("giant rat");   // killed it → Death wipe

        Assert.False(fired);                     // victory, not abandonment
        Assert.False(h.CombatGateHeld);          // gate cleared normally
    }

    [Fact]
    public void RoomChangeWithGateDown_DoesNotFireAbandonment()
    {
        // Normal walking through an empty room (no hostile engaged, gate never
        // held) must not trip the abandonment signal on every room change.
        using Harness h = new();
        h.Feed("Also here: Bob.");               // player only — gate down
        Assert.False(h.CombatGateHeld);

        bool fired = false;
        h.Tracker.EngagedTargetAbandoned += _ => fired = true;

        h.Classifier.NoteRoomChanged();

        Assert.False(fired);
    }

    // ----- HasRoomNpc ------------------------------------------------

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
        h.AddMonster(7, "shopkeeper");
        h.SetOverlay(7, relationship: MonsterRelationship.Friend);

        h.Feed("Also here: shopkeeper.");

        Assert.True(h.Tracker.HasRoomNpc);
        Assert.False(h.Tracker.HasEngageableHostiles);
    }

    [Fact]
    public void HasRoomNpc_Hostile_True()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.True(h.Tracker.HasRoomNpc);
    }

    [Fact]
    public void HasRoomNpc_TracksEvenWhenAutoAttackDisabled()
    {
        // The auto-attack gate early-returns, but NPC-presence tracking
        // runs first so the sneak-block signal stays live.
        using Harness h = new() { AutoAttackEnabled = false };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.True(h.Tracker.HasRoomNpc);
        Assert.False(h.CombatGateHeld);
    }

    [Fact]
    public void HasRoomNpc_ClearsWhenRoomEmpties()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");

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
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.False(h.CombatGateHeld);
    }

    [Fact]
    public void AutoAttackDisabledMidFight_ClearsGate()
    {
        // Gate asserted, user flips master toggle off, next classifier
        // observation clears the gate defensively.
        using Harness h = new();
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.True(h.CombatGateHeld);

        h.AutoAttackEnabled = false;
        h.Feed("Also here: giant rat.");
        Assert.False(h.CombatGateHeld);
    }

    [Fact]
    public void ReObservingAfterSuppressionLifts_AssertsGate()
    {
        // The stale-CurrentRoom lair race (report paradigm-20260915-130624): a lair's
        // hostile is first observed while the loop's "only attack in lair rooms"
        // suppression still reads the previous, non-lair connector — auto-attack
        // effectively OFF at observe time, so the gate never holds and the loop would
        // walk straight through. On room confirmation the classifier re-emits the same
        // roster with the suppression now lifted (CurrentRoom points at the real lair),
        // and THAT re-observation must assert the gate so the loop holds to fight.
        using Harness h = new() { AutoAttackEnabled = false };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.False(h.CombatGateHeld);   // suppressed at first observe → no hold

        h.AutoAttackEnabled = true;       // room confirmed as a lair → suppression lifts
        h.Feed("Also here: giant rat.");  // classifier re-emits the kept roster
        Assert.True(h.CombatGateHeld);    // now holds to fight
    }

    [Fact]
    public void OnAutoAttackChanged_Off_ReleasesGateWithoutNewObservation()
    {
        // Toggling auto-combat off mid-round used to leave the walker gated
        // until the next room re-display. OnAutoAttackChanged re-evaluates the
        // last observation at once so the gate drops immediately.
        using Harness h = new();
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.True(h.CombatGateHeld);

        h.AutoAttackEnabled = false;        // toggle flips the delegate...
        h.Tracker.OnAutoAttackChanged();    // ...and notifies the tracker
        Assert.False(h.CombatGateHeld);     // released without a re-display
    }

    // Report paradigm-20260928-130856: the gate went up on the re-entry room while the
    // loop was still stopped by the disconnect; the loop then resumed and the room read
    // as suppressed ("do not attack"), so the engine never attacked and nothing
    // re-displayed the room — the character stood being hit for 18 minutes. The combat
    // tick now notices the gate held under a suppressed room and lets it go.
    [Fact]
    public void GateHeldWhenTheRoomTurnsSuppressed_ReleasesOnTheNextTick()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");
        Assert.True(h.CombatGateHeld);

        h.AutoAttackEnabled = false;   // the resumed loop suppresses combat here
        h.Tracker.OnCombatTick();

        Assert.False(h.CombatGateHeld);
    }

    [Fact]
    public void OnAutoAttackChanged_NoObservationYet_NoThrow()
    {
        // Guarded no-op before any room has been observed.
        using Harness h = new();
        h.AutoAttackEnabled = false;
        h.Tracker.OnAutoAttackChanged();
        Assert.False(h.CombatGateHeld);
    }

    // ----- break-before-run on auto-combat toggle-off ----------------

    [Fact]
    public void OnAutoAttackChanged_Off_InCombat_BreakBeforeRunning_SendsBreak()
    {
        // BreakBeforeFleeing checked: toggling auto-combat off mid-fight must
        // send "break" before the walker is released to run, so the game
        // actually leaves combat instead of dragging the mob.
        using Harness h = new();
        h.WireBreakBeforeRun();
        h.BreakBeforeRunning = true;
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");    // gate asserted on the hostile
        h.Feed("*Combat Engaged*");         // InCombat true
        Assert.True(h.CombatGateHeld);

        h.AutoAttackEnabled = false;
        h.Tracker.OnAutoAttackChanged();

        Assert.Contains("break", h.Sent);
        Assert.False(h.CombatGateHeld);     // then released to run
    }

    [Fact]
    public void OnAutoAttackChanged_Off_BreakBeforeRunningOff_NoBreak()
    {
        // Setting unchecked: no courtesy break, the walker just runs.
        using Harness h = new();
        h.WireBreakBeforeRun();
        h.BreakBeforeRunning = false;
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        h.Feed("*Combat Engaged*");
        Assert.True(h.CombatGateHeld);

        h.AutoAttackEnabled = false;
        h.Tracker.OnAutoAttackChanged();

        Assert.DoesNotContain("break", h.Sent);
        Assert.False(h.CombatGateHeld);
    }

    // Report stock-20260914-003246: the user toggled auto-combat off in the window
    // between the engine's attack going out and the server's *Combat Engaged* coming
    // back. InCombat was still false, so the break was skipped and the walker ran off
    // still engaged. The engine's own target answers the same question immediately.
    [Fact]
    public void OnAutoAttackChanged_Off_AttackInFlightBeforeEngagedReply_SendsBreak()
    {
        using Harness h = new();
        h.WireBreakBeforeRun();
        h.BreakBeforeRunning = true;
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");    // gate asserted on the hostile
        Assert.True(h.CombatGateHeld);
        h.AttackInFlight = true;            // we swung...
        Assert.False(h.State.InCombat);     // ...but *Combat Engaged* hasn't landed

        h.AutoAttackEnabled = false;
        h.Tracker.OnAutoAttackChanged();

        Assert.Contains("break", h.Sent);
        Assert.False(h.CombatGateHeld);
    }

    // The break trigger is "did we send an attack", physical OR spell. A combat spell
    // sets CombatManager.CastingSpellTarget rather than CurrentTarget (separate modes,
    // only one live at a time), so a spell attack must break exactly like a swing —
    // the harness gate stands in for "either target is set".
    [Fact]
    public void OnAutoAttackChanged_Off_SpellAttackInFlight_SendsBreak()
    {
        using Harness h = new();
        h.WireBreakBeforeRun();
        h.BreakBeforeRunning = true;
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.True(h.CombatGateHeld);
        h.AttackInFlight = true;            // spell announced, no *Combat Engaged* yet
        Assert.False(h.State.InCombat);

        h.AutoAttackEnabled = false;
        h.Tracker.OnAutoAttackChanged();

        Assert.Contains("break", h.Sent);
    }

    // The attack-in-flight signal must not manufacture a fight on its own: with no
    // gate asserted there is no hostile being held for, so a stale target can't
    // produce a stray break into an empty room.
    [Fact]
    public void OnAutoAttackChanged_Off_AttackInFlightButNoGate_NoBreak()
    {
        using Harness h = new();
        h.WireBreakBeforeRun();
        h.BreakBeforeRunning = true;
        h.AttackInFlight = true;

        h.Feed("Also here: Bob.");          // player only — no gate, no combat
        Assert.False(h.CombatGateHeld);

        h.AutoAttackEnabled = false;
        h.Tracker.OnAutoAttackChanged();

        Assert.DoesNotContain("break", h.Sent);
    }

    [Fact]
    public void OnAutoAttackChanged_Off_NotInCombat_NoBreak()
    {
        // Toggling auto-combat off in a safe room (no gate, not in combat)
        // must not emit a stray break.
        using Harness h = new();
        h.WireBreakBeforeRun();
        h.BreakBeforeRunning = true;

        h.Feed("Also here: Bob.");          // player only — no gate, no combat
        Assert.False(h.CombatGateHeld);

        h.AutoAttackEnabled = false;
        h.Tracker.OnAutoAttackChanged();

        Assert.DoesNotContain("break", h.Sent);
    }

    [Fact]
    public void OnAutoAttackChanged_Off_NotWired_NoThrow()
    {
        // Without SetWireSender / SetBreakBeforeRunGate the toggle-off path is
        // a no-op — no break, no throw.
        using Harness h = new();
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        h.Feed("*Combat Engaged*");

        h.AutoAttackEnabled = false;
        h.Tracker.OnAutoAttackChanged();

        Assert.Empty(h.Sent);
        Assert.False(h.CombatGateHeld);
    }

    // ----- seehidden clear override ----------------------------------

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
        h.AddMonster(1, "crystal golem");

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
        h.AddMonster(1, "crystal golem");
        h.AddMonster(2, "giant rat");

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
    public void CombatOffClear_AfterEngaging_FiresCombatSpentStealth()
    {
        // A combat-OFF force-clear (see-hidden / rest engage-to-clear) SPENDS sneak
        // during the fight, but the auto-attack-OFF clear branch used to flip
        // InCombat without firing CombatSpentStealth — leaving the FSM stale-Sneaking
        // so the pre-move / post-cast re-sneak no-op'd (a cleared see-hidden room was
        // left unsneaked). The clear must fire the reset.
        using Harness h = new() { AutoAttackEnabled = false };
        h.WireSeeHiddenGate();
        h.ClearWhenSeenHidden = true;
        h.AutoSneakEnabled = true;
        h.SeeHidden.Add(1);
        h.AddMonster(1, "crystal golem");

        int stealthSpent = 0;
        h.Tracker.CombatSpentStealth += _ => stealthSpent++;

        h.Feed("Also here: crystal golem.");   // force-clear engages, gate held
        h.Feed("*Combat Engaged*");             // InCombat true (we engaged)
        h.Feed("Also here: Bob.");              // room cleared of hostiles

        Assert.Equal(1, stealthSpent);
        Assert.False(h.State.InCombat);
    }

    [Fact]
    public void CombatOffClear_WithoutEngaging_DoesNotFireCombatSpentStealth()
    {
        // Guard: a walk-past of a hostile-free room with auto-attack off never
        // engaged, so InCombat is false and the stealth reset must NOT fire — it
        // would wrongly drop a sneak we still hold.
        using Harness h = new() { AutoAttackEnabled = false };

        int stealthSpent = 0;
        h.Tracker.CombatSpentStealth += _ => stealthSpent++;

        h.Feed("Also here: Bob.");   // hostile-free, never engaged

        Assert.Equal(0, stealthSpent);
    }

    [Fact]
    public void SeeHiddenOverride_Dormant_WhenToggleOff()
    {
        using Harness h = new() { AutoAttackEnabled = false };
        h.WireSeeHiddenGate();
        h.ClearWhenSeenHidden = false;        // user opted out
        h.AutoSneakEnabled = true;
        h.SeeHidden.Add(1);
        h.AddMonster(1, "crystal golem");

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
        h.AddMonster(1, "crystal golem");

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
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.False(h.CombatGateHeld);
        Assert.False(h.Tracker.SeeHiddenClearActive);
    }

    [Fact]
    public void SeeHiddenOverride_StaysLatched_WhenAutoAttackOn()
    {
        // The override now force-clears with auto-attack ON or OFF: flipping master
        // auto-attack on mid-clear keeps the latch (and gate) so the room is still
        // fully cleared (bypassing Min/Max via CombatManager's latch read) rather
        // than being dropped to the normal count-gated behaviour.
        using Harness h = new() { AutoAttackEnabled = false };
        h.WireSeeHiddenGate();
        h.ClearWhenSeenHidden = true;
        h.AutoSneakEnabled = true;
        h.SeeHidden.Add(1);
        h.AddMonster(1, "crystal golem");

        h.Feed("Also here: crystal golem.");
        Assert.True(h.Tracker.SeeHiddenClearActive);

        h.AutoAttackEnabled = true;
        h.Feed("Also here: crystal golem.");
        Assert.True(h.Tracker.SeeHiddenClearActive);   // latch persists with combat on
        Assert.True(h.CombatGateHeld);
    }

    [Fact]
    public void SeeHiddenOverride_ArmsFresh_WithAutoAttackOn()
    {
        // Armed directly with auto-attack ON — a stealth runner whose sneak is
        // broken by a see-hidden monster force-clears the room even when combat is
        // enabled, so the room can't be skipped by the Min/Max gate.
        using Harness h = new() { AutoAttackEnabled = true };
        h.WireSeeHiddenGate();
        h.ClearWhenSeenHidden = true;
        h.AutoSneakEnabled = true;
        h.SeeHidden.Add(1);
        h.AddMonster(1, "crystal golem");

        h.Feed("Also here: crystal golem.");

        Assert.True(h.Tracker.SeeHiddenClearActive);
        Assert.True(h.CombatGateHeld);
    }

    // Report paradigm-20261007-143049: the see-hidden room came after a 78-second
    // walk with no fighting. The stall watchdog counted that silence against the gate
    // it had just seen go up, dropped it on the next one-second poll, and the walker
    // left the room the character had just attacked in.
    [Fact]
    public void SeeHiddenOverride_GateRaisedAfterAQuietWalk_IsNotDroppedByTheStallWatchdog()
    {
        using Harness h = new() { AutoAttackEnabled = false };
        h.WireSender();
        h.WireSeeHiddenGate();
        h.ClearWhenSeenHidden = true;
        h.AutoSneakEnabled = true;
        h.SeeHidden.Add(1);
        h.AddMonster(1, "gorgon");
        h.Feed("The gorgon lunges at you with its horns, but misses!");   // the last fight
        h.FakeNow = h.FakeNow.AddSeconds(78);

        h.Feed("Also here: gorgon.");
        Assert.True(h.CombatGateHeld);

        h.FakeNow = h.FakeNow.AddSeconds(0.1);
        h.Tracker.OnCombatTick();
        Assert.True(h.CombatGateHeld);
        Assert.True(h.Tracker.SeeHiddenClearActive);
        Assert.Empty(h.SentRaw);

        // Six quiet seconds from there is a stall, as for any other fight.
        h.FakeNow = h.FakeNow.AddSeconds(7);
        h.Tracker.OnCombatTick();
        Assert.False(h.CombatGateHeld);
    }

    // Report paradigm-20261007-143049 and the user's ruling on it (2026-10-07): the
    // sneak a see-hidden monster broke stays broken until the character sneaks
    // again. Carried into the next room before that, it is exposed there for the
    // same reason, so that room is cleared too.
    private static Harness RunnerWhoseSneakAGorgonBroke()
    {
        Harness h = new() { AutoAttackEnabled = false };
        h.WireSeeHiddenGate();
        h.ClearWhenSeenHidden = true;
        h.AutoSneakEnabled = true;
        h.SeeHidden.Add(1);
        h.AddMonster(1, "gorgon");
        h.AddMonster(2, "brigand");

        h.Feed("Also here: gorgon.");
        Assert.True(h.Tracker.SneakBrokenBySeeHidden);
        h.Classifier.NoteRoomChanged();          // carried out of the gorgon's room
        Assert.False(h.CombatGateHeld);
        return h;
    }

    [Fact]
    public void SeeHiddenBreak_IsCarriedIntoTheNextRoom_WhichIsClearedToo()
    {
        using Harness h = RunnerWhoseSneakAGorgonBroke();

        h.Feed("Also here: brigand.");           // nothing here sees hidden

        Assert.True(h.CombatGateHeld);
        Assert.True(h.Tracker.SeeHiddenClearActive);

        h.Feed("Also here: Bob.");               // cleared: free to re-sneak
        Assert.False(h.CombatGateHeld);
        Assert.True(h.Tracker.SneakBrokenBySeeHidden);   // until the sneak takes
    }

    [Fact]
    public void SeeHiddenBreak_EndsOnceSneakingAgain()
    {
        using Harness h = RunnerWhoseSneakAGorgonBroke();

        h.Tracker.NoteSneakRegained();
        h.Feed("Also here: brigand.");

        Assert.False(h.Tracker.SneakBrokenBySeeHidden);
        Assert.False(h.CombatGateHeld);          // sneaking past it, as before the break
        Assert.False(h.Tracker.SeeHiddenClearActive);
    }

    [Fact]
    public void SeeHiddenBreak_IsDroppedWhenTheOptionIsSwitchedOff()
    {
        using Harness h = RunnerWhoseSneakAGorgonBroke();

        h.ClearWhenSeenHidden = false;
        h.Feed("Also here: brigand.");

        Assert.False(h.CombatGateHeld);
        Assert.False(h.Tracker.SneakBrokenBySeeHidden);
    }

    // The user's rule (2026-10-07): a see-hidden room over the room thresholds isn't
    // stopped in, like any other fight outside them. The break is still on, so the
    // first room that does meet them is where the character stops and clears.
    [Fact]
    public void SeeHiddenBreak_RoomOutsideTheMonsterWindow_IsWalkedThrough_StillBroken()
    {
        using Harness h = new() { AutoAttackEnabled = false };
        h.WireSeeHiddenGate();
        h.WireMonsterCountWindow();
        h.MovementActive = true;
        h.Settings.MaxMonstersInRoom = 2;
        h.ClearWhenSeenHidden = true;
        h.AutoSneakEnabled = true;
        h.SeeHidden.Add(1);
        h.AddMonster(1, "gorgon");
        h.AddMonster(2, "brigand");
        h.AddMonster(3, "harpy");

        h.Feed("Also here: gorgon, brigand, harpy.");    // three, over the max of two
        Assert.False(h.CombatGateHeld);
        Assert.False(h.Tracker.SeeHiddenClearActive);
        Assert.True(h.Tracker.SneakBrokenBySeeHidden);

        h.Classifier.NoteRoomChanged();
        h.Feed("Also here: brigand, harpy.");            // two: inside the window
        Assert.True(h.CombatGateHeld);
        Assert.True(h.Tracker.SeeHiddenClearActive);
    }

    // Once stopped, the room is cleared whatever the count does: a third monster
    // walking in doesn't turn the clear into a room to leave.
    [Fact]
    public void SeeHiddenClear_OnceStopped_IsHeldPastTheWindow()
    {
        using Harness h = new() { AutoAttackEnabled = false };
        h.WireSeeHiddenGate();
        h.WireMonsterCountWindow();
        h.MovementActive = true;
        h.Settings.MaxMonstersInRoom = 2;
        h.ClearWhenSeenHidden = true;
        h.AutoSneakEnabled = true;
        h.SeeHidden.Add(1);
        h.AddMonster(1, "gorgon");
        h.AddMonster(2, "brigand");
        h.AddMonster(3, "harpy");

        h.Feed("Also here: gorgon, brigand.");
        Assert.True(h.CombatGateHeld);

        h.Feed("Also here: gorgon, brigand, harpy.");
        Assert.True(h.CombatGateHeld);
        Assert.True(h.Tracker.SeeHiddenClearActive);
    }

    // The stall watchdog gives up on a room, not on the break; Reset States is the
    // user starting over.
    [Fact]
    public void SeeHiddenBreak_SurvivesTheStallWatchdog_ButNotResetStates()
    {
        using Harness h = RunnerWhoseSneakAGorgonBroke();

        h.Tracker.ResetCombatState("idle-stall watchdog");
        Assert.True(h.Tracker.SneakBrokenBySeeHidden);

        h.Tracker.ResetCombatState("Reset States (manual)", forgetSneakBreak: true);
        Assert.False(h.Tracker.SneakBrokenBySeeHidden);
    }

    [Fact]
    public void SeeHiddenOverride_NotWired_BehavesAsBefore()
    {
        // Without SetSeeHiddenClearGate, combat-off never holds the gate.
        using Harness h = new() { AutoAttackEnabled = false };
        h.AddMonster(1, "crystal golem");

        h.Feed("Also here: crystal golem.");

        Assert.False(h.CombatGateHeld);
        Assert.False(h.Tracker.SeeHiddenClearActive);
    }

    // ----- clear hostiles when sneak fails ------------------------------

    private static Harness SneakFailHarness()
    {
        Harness h = new() { AutoAttackEnabled = false };
        h.WireSeeHiddenGate();
        h.Tracker.SetSneakFailClearGate(() => true);
        h.AutoSneakEnabled = true;
        h.WireMonsterCountWindow();
        h.WireMovementActiveGate();
        h.AddMonster(1, "orc");
        h.AddMonster(2, "goblin");
        return h;
    }

    [Fact]
    public void SneakFail_LoudEntryIntoARoomInsideTheThresholds_HoldsToClearIt()
    {
        using Harness h = SneakFailHarness();
        h.Tracker.NoteSneakEntry(held: false);

        h.Feed("Also here: orc.");

        Assert.True(h.Tracker.SneakFailClearActive);
        Assert.True(h.CombatGateHeld);
    }

    [Fact]
    public void SneakFail_RoomOutsideTheThresholds_IsWalkedThrough()
    {
        using Harness h = SneakFailHarness();
        h.Settings.MaxMonstersInRoom = 1;
        h.Tracker.NoteSneakEntry(held: false);

        h.Feed("Also here: orc, goblin.");

        Assert.False(h.Tracker.SneakFailClearActive);
        Assert.False(h.CombatGateHeld);
    }

    [Fact]
    public void SneakFail_HeldEntryOrALaterMove_DoesNotClear()
    {
        using Harness h = SneakFailHarness();
        h.Tracker.NoteSneakEntry(held: true);
        h.Feed("Also here: orc.");
        Assert.False(h.CombatGateHeld);

        h.Tracker.NoteSneakEntry(held: false);
        h.Tracker.NoteMoveSent();
        h.Feed("Also here: goblin.");
        Assert.False(h.CombatGateHeld);
    }

    [Fact]
    public void SneakFail_SilentLossReRunsTheRoomAlreadyShown()
    {
        using Harness h = SneakFailHarness();
        h.Feed("Also here: orc.");
        Assert.False(h.CombatGateHeld);

        h.Tracker.NoteSilentSneakLoss();

        Assert.True(h.Tracker.SneakFailClearActive);
        Assert.True(h.CombatGateHeld);
    }

    [Fact]
    public void SneakFail_WithCombatOn_LeavesItToTheNormalGate()
    {
        using Harness h = SneakFailHarness();
        h.AutoAttackEnabled = true;
        h.Tracker.NoteSneakEntry(held: false);

        h.Feed("Also here: orc.");

        Assert.False(h.Tracker.SneakFailClearActive);
        Assert.True(h.CombatGateHeld);
    }

    // ----- gate-history Asserter is set ------------------------------

    [Fact]
    public void GateHistoryRecordsAsserterAndReason()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");

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
        h.AddMonster(1, "stone golem");
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
        h.AddMonster(1, "stone golem");
        h.AddMonster(2, "giant rat");
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
        h.AddMonster(1, "stone golem");

        h.Feed("Also here: stone golem.");

        Assert.True(h.CombatGateHeld);
    }

    // ----- monster count window (CombatManager parity) ----------------

    // Reproduces the deadlock from a live capture: a room stuffed with more
    // hostiles than CombatSettings.MaxMonstersInRoom allows, WHILE a walker
    // is actively driving us through. CombatManager silently declines to
    // engage such a room (its own min/max skip), but before this fix the
    // tracker didn't know about that window at all — it held the walker gate
    // for ANY actionable hostile regardless of room population. Combat
    // refusing to fight + the walker unable to leave = the character stands
    // there absorbing hits from every monster in the room with no recourse.
    [Fact]
    public void RoomOverMax_MovementActive_ReleasesGate()
    {
        using Harness h = new();
        h.WireMonsterCountWindow();
        h.WireMovementActiveGate();
        h.MovementActive = true;
        h.Settings.MaxMonstersInRoom = 2;
        h.AddMonster(1, "zombie");
        h.AddMonster(2, "fierce zombie");
        h.AddMonster(3, "small skeleton");

        h.Feed("Also here: zombie, fierce zombie, small skeleton.");

        // 3 hostiles > Max=2 → outside the window, same as CombatManager's own
        // skip. A walker IS driving us through, so the gate must release to
        // let it move past, not stand here.
        Assert.False(h.CombatGateHeld);
    }

    // A second live capture caught the idle case: logging straight into the
    // SAME over-populated room with no loop / walk-to queued (DefaultTask =
    // Do Nothing). Releasing the walker gate accomplishes nothing when
    // there's no walker running to move away — the character just stands
    // there, unengaged AND undefended. The window must not apply while
    // genuinely idle; fight back regardless of room population.
    [Fact]
    public void RoomOverMax_NotMovementActive_HoldsGate()
    {
        using Harness h = new();
        h.WireMonsterCountWindow();
        h.WireMovementActiveGate();
        h.MovementActive = false;   // no walker/loop attached — idle
        h.Settings.MaxMonstersInRoom = 2;
        h.AddMonster(1, "zombie");
        h.AddMonster(2, "fierce zombie");
        h.AddMonster(3, "small skeleton");

        h.Feed("Also here: zombie, fierce zombie, small skeleton.");

        Assert.True(h.CombatGateHeld);
    }

    [Fact]
    public void RoomWithinWindow_HoldsGate()
    {
        using Harness h = new();
        h.WireMonsterCountWindow();
        h.WireMovementActiveGate();
        h.MovementActive = true;
        h.Settings.MaxMonstersInRoom = 2;
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.True(h.CombatGateHeld);
    }

    [Fact]
    public void Unwired_MonsterCountWindow_HoldsGateRegardlessOfCount()
    {
        using Harness h = new();
        // No WireMonsterCountWindow — fail-open: room population never gates,
        // exactly the pre-fix behavior.
        h.AddMonster(1, "zombie");
        h.AddMonster(2, "fierce zombie");
        h.AddMonster(3, "small skeleton");

        h.Feed("Also here: zombie, fierce zombie, small skeleton.");

        Assert.True(h.CombatGateHeld);
    }

    // ----- PlayerState.InCombat drive --------------------------------

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
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");          // assert gate + InCombat reflects fight
        h.Feed("*Combat Engaged*");
        Assert.True(h.State.InCombat);

        h.Feed("Also here: Bob.");                // monster gone — only player
        Assert.False(h.State.InCombat);
    }

    [Fact]
    public void AutoAttackOff_RoomClear_FlipsInCombatFalse()
    {
        // With auto-attack off the gate never holds, but a room clear of
        // engageable hostiles is still the authoritative out-of-combat signal.
        // Without this, InCombat stays stuck true and HealthManager never rests.
        using Harness h = new();
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        h.Feed("*Combat Engaged*");
        Assert.True(h.State.InCombat);

        h.AutoAttackEnabled = false;              // user toggles auto-combat off
        h.Feed("Also here: Bob.");                // room now clear
        Assert.False(h.State.InCombat);           // out of combat → rest can go
    }

    [Fact]
    public void RoomClear_InCombatFalseTransition_SeesGateAlreadyCleared()
    {
        // Regression (report stock-20260730-184622): the InCombat→false transition
        // fires HealthManager.Evaluate synchronously, and the rest guard reads
        // HasEngageableHostiles (derived from the combat gate). If the gate still
        // read asserted at that instant, the rest would be blocked as "hostile
        // present" and deferred to the next prompt tick — a multi-second stall
        // after the room clears. On room clear the gate must already read cleared
        // the moment InCombat flips false.
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");
        h.Feed("*Combat Engaged*");
        Assert.True(h.State.InCombat);
        Assert.True(h.Tracker.HasEngageableHostiles);

        bool? engageableAtTransition = null;
        h.State.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerState.InCombat) && !h.State.InCombat)
                engageableAtTransition = h.Tracker.HasEngageableHostiles;
        };

        h.Feed("Also here: Bob.");                // room clear → InCombat false

        Assert.True(engageableAtTransition.HasValue, "InCombat→false should have fired");
        Assert.False(engageableAtTransition!.Value); // gate already clear at the transition
        Assert.False(h.State.InCombat);
        Assert.False(h.Tracker.HasEngageableHostiles);
        Assert.False(h.CombatGateHeld);           // coordinator gate released too
    }

    [Fact]
    public void AutoAttackOff_HostileStillHere_KeepsInCombatTrue()
    {
        // Turning auto-attack off next to a live mob must NOT declare us out of
        // combat — HealthManager would otherwise rest right next to the hostile.
        using Harness h = new();
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        h.Feed("*Combat Engaged*");
        Assert.True(h.State.InCombat);

        h.AutoAttackEnabled = false;
        h.Feed("Also here: giant rat.");          // mob still here
        Assert.False(h.CombatGateHeld);           // gate down (manual player)
        Assert.True(h.State.InCombat);            // but still in combat — no rest
    }

    [Fact]
    public void UserHitsLine_FlipsInCombatTrue()
    {
        // Damage-line drive: even before CombatStatus arrives, observing
        // a damage line flips us to InCombat=true. Pattern shape:
        // "<source> [critically ]<verb> <target> for N damage!" with the
        // bang anchor at end.
        using Harness h = new();
        h.Feed("MudPlay hits a giant rat for 5 damage!");
        Assert.True(h.State.InCombat);
    }

    // A room's own damage reads like a hit and is no fight (user, 2026-10-09). The
    // probe is the round clock's test, the one AppServices hands every consumer.
    [Theory]
    [InlineData("You are seared by the flames for 46 damage!", OffRoundDamageLinesTests.MagmaHeat)]
    [InlineData("You are seared by the flames for 46 damage!", 0)]             // room not placed: the wording gives it away
    [InlineData("A chaotic storm assaults you for 30 damage!", OffRoundDamageLinesTests.ChaosStorm)]
    public void RoomSpellDamage_DoesNotPutUsInCombat(string line, int roomSpell)
    {
        using Harness h = new();
        h.Tracker.SetNotCombatLineProbe(OffRoundDamageLinesTests.ProbeIn(roomSpell));
        h.Feed(line);
        Assert.False(h.State.InCombat);
    }

    [Theory]
    [InlineData("A chaotic storm assaults you for 30 damage!", 0)]             // the same text, cast by a monster elsewhere
    [InlineData("A chaotic storm assaults you for 30 damage!", OffRoundDamageLinesTests.MagmaHeat)]
    [InlineData("You are struck by a dark force for 80 damage!", OffRoundDamageLinesTests.MagmaHeat)]
    [InlineData("The giant rat bites you for 3 damage!", OffRoundDamageLinesTests.MagmaHeat)]
    [InlineData("The giant rat lunges at you, but misses!", OffRoundDamageLinesTests.MagmaHeat)]
    public void AMonstersAttack_StillPutsUsInCombat(string line, int roomSpell)
    {
        using Harness h = new();
        h.Tracker.SetNotCombatLineProbe(OffRoundDamageLinesTests.ProbeIn(roomSpell));
        h.Feed(line);
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

    // ----- manual reset escape hatch (report paradigm-20260716-011443) ----

    [Fact]
    public void ResetCombatState_ClearsGateAndInCombat()
    {
        // Reset States must force the tracker idle when a stale roster (fallback
        // death whose forced re-display never wiped the room) left the Combat gate
        // stuck asserted and InCombat true. Clearing conditions alone didn't touch
        // the gate, so the Fighting chip stayed lit over an empty room.
        using Harness h = new();
        h.AddMonster(1, "roc hatchling");

        h.Feed("Also here: roc hatchling.");        // asserts gate
        h.Feed("*Combat Engaged*");                 // flips InCombat true
        Assert.True(h.CombatGateHeld);
        Assert.True(h.State.InCombat);

        h.Tracker.ResetCombatState("test");

        Assert.False(h.CombatGateHeld);
        Assert.False(h.State.InCombat);
    }

    // ----- idle-stall watchdog (reports 162423 / 163553) ----------------

    [Fact]
    public void IdleStallWatchdog_GateHeldNoActivity_ForcesRedisplay()
    {
        // Regression: the final kill can route through a path that never forces
        // a room re-display, so the tracker never sees the empty room and the
        // Combat gate stays asserted forever — the walker hangs "fighting" an
        // empty room. Single-phase watchdog: once the gate has been held past the
        // idle threshold with no combat activity, it fires a bare resync CR AND
        // force-clears the gate in the same tick.
        using Harness h = new();
        h.WireSender();
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");           // stamps activity + asserts gate
        Assert.True(h.CombatGateHeld);

        // A tick before the threshold does nothing.
        h.FakeNow = h.FakeNow.AddSeconds(5);
        h.Tracker.OnCombatTick();
        Assert.Empty(h.SentRaw);
        Assert.True(h.CombatGateHeld);

        // Past the 6s threshold with the gate still held and no activity → the
        // resync CR goes out and the stuck gate clears in the same tick.
        h.FakeNow = h.FakeNow.AddSeconds(2);        // 7s total
        h.Tracker.OnCombatTick();
        Assert.Contains("\r", h.SentRaw);
        Assert.False(h.CombatGateHeld);
    }

    // A ShadowRest holds combat beside a monster on purpose; that quiet isn't a stall,
    // so the watchdog doesn't re-display the room every 6 s (report
    // paradigm-20260930-193005).
    [Fact]
    public void IdleStallWatchdog_CombatHeldOnPurpose_LeavesTheRoomAlone()
    {
        using Harness h = new();
        h.WireSender();
        h.Tracker.SetCombatHeldOnPurposeProbe(() => true);
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");

        h.FakeNow = h.FakeNow.AddSeconds(7);
        h.Tracker.OnCombatTick();

        Assert.Empty(h.SentRaw);
        Assert.True(h.CombatGateHeld);
    }

    [Fact]
    public void IdleStallWatchdog_DarkRoom_ClearsGateWithoutResyncCr()
    {
        // In a dark room the watchdog still force-clears the stuck gate, but skips
        // the resync CR: a blind CR re-emits no "Also here:" to re-observe and its
        // "you can't see anything" reply is dead-reckoned as a false confirm of the
        // movement loop's in-flight step (the double-step-past-lairs bug).
        using Harness h = new();
        h.WireSender();
        h.Dark = true;
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.True(h.CombatGateHeld);

        h.FakeNow = h.FakeNow.AddSeconds(7);        // past the 6s threshold
        h.Tracker.OnCombatTick();

        Assert.Empty(h.SentRaw);                    // no blind resync CR
        Assert.False(h.CombatGateHeld);             // gate still cleared
    }

    [Fact]
    public void IdleStallWatchdog_CombatActivity_ResetsStall()
    {
        // A live-but-slow fight must never trip the watchdog: any combat line
        // (or a room observation still holding a killable monster) refreshes the
        // activity stamp, so a genuinely ongoing fight is left alone.
        using Harness h = new();
        h.WireSender();
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.True(h.CombatGateHeld);

        h.FakeNow = h.FakeNow.AddSeconds(5);
        h.Feed("The giant rat bites you for 3 damage!");   // fresh activity
        h.FakeNow = h.FakeNow.AddSeconds(2);               // 7s since gate, 2s since hit
        h.Tracker.OnCombatTick();
        Assert.Empty(h.SentRaw);                            // not stalled — no CR
    }

    [Fact]
    public void IdleStallWatchdog_MobAttackNoDamage_ResetsStall()
    {
        // Regression (report stock-20260730-190736): on realms whose melee carries
        // no damage number ("The X slashes you with their shortsword!") or is
        // armour-deflected ("...but your armour deflects the blow!"), the per-round
        // swings match neither MobHits (needs "for N damage") nor MobMisses (needs
        // "at you"), so before MobAttacksYou they never refreshed the activity
        // stamp — an active fight tripped the watchdog and the walker abandoned the
        // live hostile. The mob's swing must now register as combat activity.
        using Harness h = new();
        h.WireSender();
        h.AddMonster(1, "dark cultist");

        h.Feed("Also here: dark cultist.");
        Assert.True(h.CombatGateHeld);

        h.FakeNow = h.FakeNow.AddSeconds(5);
        h.Feed("The dark cultist slashes you, but your armour deflects the blow!");
        h.FakeNow = h.FakeNow.AddSeconds(2);               // 7s since gate, 2s since swing
        h.Tracker.OnCombatTick();

        Assert.Empty(h.SentRaw);                            // not stalled — no CR
        Assert.True(h.CombatGateHeld);                      // live fight left alone
    }

    [Fact]
    public void IdleStallWatchdog_NoGate_NeverFires()
    {
        // No gate held AND not in combat → nothing to resync. The watchdog must
        // stay silent even long after any activity (a player in the room is not a
        // fight, so InCombat never latched).
        using Harness h = new();
        h.WireSender();

        h.Feed("Also here: Bob.");                 // player only, no gate, no InCombat
        Assert.False(h.CombatGateHeld);
        Assert.False(h.State.InCombat);

        h.FakeNow = h.FakeNow.AddSeconds(60);
        h.Tracker.OnCombatTick();
        Assert.Empty(h.SentRaw);
    }

    [Fact]
    public void IdleStallWatchdog_AutoAttackOff_ClearsStuckInCombat()
    {
        // Report paradigm-20260812-150324 ("buffs not happening while combat is
        // off"): with auto-attack OFF, an incoming combat line latches InCombat
        // true, but the only path that clears InCombat (OnEntitiesObserved's
        // combat-off branch) runs on a room re-display that idle play never
        // produces — so InCombat hangs, and because bless refuses to fire in
        // combat, auto-bless silently stops. The watchdog must rescue this even
        // with the Combat gate never asserted: after the stall window it resyncs
        // and clears InCombat so bless resumes.
        using Harness h = new();
        h.WireSender();
        h.AutoAttackEnabled = false;

        h.Feed("The giant rat bites you for 3 damage!");   // InCombat true, gate NOT asserted
        Assert.True(h.State.InCombat);
        Assert.False(h.CombatGateHeld);

        // A tick before the threshold leaves the stuck state alone.
        h.FakeNow = h.FakeNow.AddSeconds(5);
        h.Tracker.OnCombatTick();
        Assert.Empty(h.SentRaw);
        Assert.True(h.State.InCombat);

        // Past the 6s threshold with no further activity → resync CR fires and the
        // stuck InCombat clears, even though no Combat gate was ever held.
        h.FakeNow = h.FakeNow.AddSeconds(2);               // 7s total
        h.Tracker.OnCombatTick();
        Assert.Contains("\r", h.SentRaw);
        Assert.False(h.State.InCombat);
        Assert.False(h.CombatGateHeld);
    }

    [Fact]
    public void IdleStallWatchdog_AutoAttackOff_FreshHitDefersClear()
    {
        // The auto-attack-off rescue is still gated on the idle window: a fight
        // that's live-but-slow (a mob swinging every few seconds) must NOT be
        // torn down. Any fresh combat line refreshes the activity stamp, so the
        // watchdog leaves an ongoing fight alone.
        using Harness h = new();
        h.WireSender();
        h.AutoAttackEnabled = false;

        h.Feed("The giant rat bites you for 3 damage!");   // InCombat true
        Assert.True(h.State.InCombat);

        h.FakeNow = h.FakeNow.AddSeconds(5);
        h.Feed("The giant rat bites you for 4 damage!");   // fresh activity
        h.FakeNow = h.FakeNow.AddSeconds(2);               // 7s since first, 2s since last
        h.Tracker.OnCombatTick();

        Assert.Empty(h.SentRaw);                           // not stalled — no CR
        Assert.True(h.State.InCombat);                     // live fight left alone
    }

    [Fact]
    public void IdleStallWatchdog_EmptyReDisplayNoRoster_ForceClearsGate()
    {
        // The deeper stuck-gate bug (report paradigm-20260716-011443): a fallback
        // death empties the room, but a stationary re-display of an empty room
        // carries NO "Also here:" line, so the classifier fires no observation and
        // the gate never clears — the walker hangs "fighting" nothing. Single-phase
        // watchdog: once the gate has been held past the threshold with no combat
        // activity, one tick sends the resync CR AND fully resets the tracker
        // (gate released, InCombat dropped) — no second escalation tick needed.
        using Harness h = new();
        h.WireSender();
        h.AddMonster(1, "roc hatchling");

        h.Feed("Also here: roc hatchling.");        // asserts gate, stamps activity
        h.Feed("*Combat Engaged*");                 // InCombat true
        Assert.True(h.CombatGateHeld);
        Assert.True(h.State.InCombat);

        // One stall tick past the threshold clears the whole stuck state.
        h.FakeNow = h.FakeNow.AddSeconds(7);
        h.Tracker.OnCombatTick();
        Assert.Contains("\r", h.SentRaw);           // resync probe went out
        Assert.False(h.CombatGateHeld);             // and the gate cleared
        Assert.False(h.State.InCombat);
    }

    [Fact]
    public void IdleStallWatchdog_SurvivorReDisplay_ReAssertsGate()
    {
        // The single-phase clear is optimistic — it releases the gate on the
        // assumption the room is empty. If a monster actually lingered (a laggy
        // >6s round), the resync CR's re-display still shows it, and that
        // Also-Here re-asserts the gate a beat later. The over-eager clear
        // self-heals rather than stranding a live fight.
        using Harness h = new();
        h.WireSender();
        h.AddMonster(1, "roc hatchling");

        h.Feed("Also here: roc hatchling.");
        Assert.True(h.CombatGateHeld);

        h.FakeNow = h.FakeNow.AddSeconds(7);
        h.Tracker.OnCombatTick();                   // watchdog: CR + optimistic clear
        Assert.Single(h.SentRaw);
        Assert.False(h.CombatGateHeld);

        // The resync CR's re-display still shows the mob → the gate re-asserts.
        h.Feed("Also here: roc hatchling.");
        Assert.True(h.CombatGateHeld);
    }

    // The see-hidden clear has a tick for each kind of company: a lone runner clears
    // what a see-hidden monster woke, a party may rather keep running.
    [Theory]
    [InlineData(true,  true,  true,  false, true)]    // solo, both ticked
    [InlineData(true,  true,  true,  true,  true)]    // partied, both ticked
    [InlineData(true,  true,  false, true,  false)]   // partied, "in a party" unticked: run on
    [InlineData(true,  true,  false, false, true)]    // solo is still cleared
    [InlineData(true,  false, true,  false, false)]   // solo unticked
    [InlineData(true,  false, true,  true,  true)]
    [InlineData(false, true,  true,  false, false)]   // the option itself is off
    [InlineData(false, true,  true,  true,  false)]
    public void SeeHiddenClearApplies_ByCompany(bool enabled, bool whileSolo, bool whileInParty, bool inParty, bool expected)
        => Assert.Equal(expected, CombatStateTracker.SeeHiddenClearApplies(enabled, whileSolo, whileInParty, inParty));

    // A saved profile from before the split has neither tick: both read as on, which
    // is how the option behaved then.
    [Fact]
    public void SeeHiddenClearTicks_DefaultOn_AndTravelWithACombatProfile()
    {
        CombatSettings fresh = new();
        Assert.True(fresh.SeenHiddenClearWhileSolo);
        Assert.True(fresh.SeenHiddenClearWhileInParty);

        CombatSettings old = System.Text.Json.JsonSerializer
            .Deserialize<CombatSettings>("""{ "ClearHostilesWhenSeenHidden": true }""")!;
        Assert.True(old.SeenHiddenClearWhileSolo);
        Assert.True(old.SeenHiddenClearWhileInParty);

        CombatSpellProfile from = new() { SeenHiddenClearWhileInParty = false };
        CombatSpellProfile to = new();
        CombatProfileGroupCopy.Copy(CombatProfileGroup.Backstab, from, to);
        Assert.False(to.SeenHiddenClearWhileInParty);
        Assert.True(to.SeenHiddenClearWhileSolo);
    }
}
