using System;
using System.Collections.Generic;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Report paradigm-20261007-120006: a buff cast a second before a lair spent the
// round's one between-round cast, so the combat profile's debuff couldn't be cast on
// entry and the room spell went out ahead of it. A loop with the option on stands one
// step short of the lair until that cast is free.
public sealed class LairEntryDebuffHoldTests
{
    // The hold stands outside a lair waiting, as the bug report's pending list
    // tells it.
    private static bool Waiting(LairEntryDebuffHold hold) =>
        hold.DescribePending()?.StartsWith("waiting", StringComparison.Ordinal) == true;

    private sealed class Harness
    {
        public MovementCoordinator Coordinator { get; } = new();
        public bool SlotUsed;
        public bool CastDue;
        public DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        public List<Action> Scheduled { get; } = new();
        public LairEntryDebuffHold Hold { get; }
        // What the hold is told is due, by name: the buffs ticked priority, and the rest.
        public List<string> PriorityDue { get; } = new();
        public List<string> OrdinaryDue { get; } = new();
        public LogService Log { get; } = new();
        // The loop is still about to step into the lair it waits outside.
        public bool StillWanted = true;
        // What the debuff needs against the character's mana; null = nothing to wait for.
        public (string Spell, int Needed, int Mana, int MaxMana)? DebuffMana;
        // The need is the spell's own cost, not its slot's "min mana per cast".
        public bool NeedIsTheSpellsCost;
        // A fight is on, as the cast scheduler reads it.
        public bool InCombat;
        // The game calls mana full here, under the maximum the client believes in.
        public bool ManaIsFull;
        // The room a fight now is in, as the combat engine judges it; null = not known.
        public RoomKey? FightRoom;
        // The client fights in that room: false where the loop is set not to attack.
        public bool FightAnswered = true;
        // The delay each look was scheduled with, newest last.
        public List<TimeSpan> ScheduledDelays { get; } = new();

        public Harness() =>
            Hold = new LairEntryDebuffHold(Coordinator, () => SlotUsed, () => CastDue,
                (delay, action) => { ScheduledDelays.Add(delay); Scheduled.Add(action); }, () => Now, Log,
                priority => priority ? PriorityDue : OrdinaryDue,
                stillWanted: () => StillWanted,
                debuffMana: () => DebuffMana is { } need
                    ? (need.Spell, need.Needed, need.Mana, need.MaxMana, NeedIsTheSpellsCost)
                    : null,
                inCombat: () => InCombat,
                manaIsFull: () => ManaIsFull,
                fightRoom: () => FightRoom,
                fightAnswered: () => FightAnswered);

        public bool Held => Coordinator.IsGateAsserted(MovementCoordinator.LairDebuffGate);
        public bool HeldForMana => Coordinator.IsGateAsserted(MovementCoordinator.LairDebuffManaGate);

        public void SetMana(int mana) => DebuffMana = DebuffMana!.Value with { Mana = mana };

        public IEnumerable<string> LoopLog => Log.Snapshot().Where(e => e.Source == "Loop").Select(e => e.Message);

        // Let the hold's timer look again.
        public void Poll()
        {
            Action[] due = Scheduled.ToArray();
            Scheduled.Clear();
            foreach (Action a in due) a();
        }
    }

    [Fact]
    public void Off_NeverHolds()
    {
        Harness h = new() { SlotUsed = true, CastDue = true };

        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.Off));
        Assert.False(h.Held);
        Assert.False(h.Hold.BlockingBuffs);
    }

    [Fact]
    public void FreeCast_NothingDue_GoesStraightIn_WithBuffsHeldForTheStep()
    {
        Harness h = new();

        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells));

        Assert.False(h.Held);
        Assert.True(h.Hold.BlockingBuffs);          // nothing takes the cast on the way in
        h.Now = h.Now.AddSeconds(6);
        Assert.False(h.Hold.BlockingBuffs);         // and the hold on buffs ends by itself
    }

    [Fact]
    public void BlockSpells_WaitsOutTheSpentCast_HoldingBuffs_ThenEnters()
    {
        Harness h = new() { SlotUsed = true, CastDue = true };

        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells));
        Assert.True(h.Held);
        Assert.True(h.Hold.BlockingBuffs);

        h.SlotUsed = false;                         // the round turned over
        h.Poll();

        Assert.False(h.Held);                       // gate cleared: the loop re-drives the step
        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells));
    }

    [Fact]
    public void WaitForSpells_LetsDueSpellsCast_ThenEntersOnAFreeCast()
    {
        Harness h = new() { SlotUsed = true, CastDue = true };

        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells));
        Assert.False(h.Hold.BlockingBuffs);         // buffs may cast while we wait

        h.SlotUsed = false;                         // free, but another buff is due:
        h.Poll();
        Assert.True(h.Held);                        // it gets its turn

        h.SlotUsed = true;                          // it cast
        h.Poll();
        h.SlotUsed = false;                         // round turned over, nothing left due
        h.CastDue = false;
        h.Poll();

        Assert.False(h.Held);
        Assert.True(h.Hold.BlockingBuffs);          // held for the step in
    }

    // A long queue must not park the loop: after a few rounds the rest is held back.
    [Fact]
    public void WaitForSpells_AfterAFewRounds_BlocksTheRestAndEnters()
    {
        Harness h = new() { SlotUsed = true, CastDue = true };
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells));

        for (int round = 1; round < LairEntryDebuffHold.MaxRoundsWaited; round++)
        {
            h.SlotUsed = false; h.Poll();           // free, more due: wait
            h.SlotUsed = true; h.Poll();            // the next one cast
        }
        Assert.True(h.Held);
        Assert.True(h.Hold.BlockingBuffs);          // the limit is reached: no more casts

        h.SlotUsed = false;                         // the cast comes free with spells still due
        h.Poll();

        Assert.False(h.Held);
    }

    // Something due that never casts (a gate the hold can't see) can't park the loop.
    [Fact]
    public void WaitForSpells_ADueSpellThatNeverCasts_EntersOnceTheWaitRunsOut()
    {
        Harness h = new() { CastDue = true };
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells));

        h.Now = h.Now.AddSeconds(20);
        h.Poll();

        Assert.False(h.Held);
    }

    // The loop turned away from the lair, or the option was switched off while it stood.
    [Fact]
    public void TurningAway_ReleasesTheHold()
    {
        Harness h = new() { SlotUsed = true };
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells));

        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.Off));

        Assert.False(h.Held);
        Assert.False(h.Hold.BlockingBuffs);
        h.Poll();                                    // a late timer does nothing
        Assert.False(h.Held);
    }

    // ----- A wait the loop never comes back to -----------------------

    // A stopped loop never asks again. The wait must not hold its gate and the buffs
    // on by its own timer: a walk started next would sit on "Waiting to Debuff".
    [Theory]
    [InlineData(LairEntryDebuffMode.WaitForSpells)]
    [InlineData(LairEntryDebuffMode.BlockSpells)]
    [InlineData(LairEntryDebuffMode.WaitForPriorityBuffs)]
    public void LoopStoppedMidWait_ReleasesTheGateAndTheBuffs_AtTheNextLook(LairEntryDebuffMode mode)
    {
        Harness h = new() { SlotUsed = true, CastDue = true };
        Assert.False(h.Hold.ReadyToEnter(mode));
        Assert.True(h.Held);

        h.StillWanted = false;                      // Stop pressed, Auto-Combat off, the route changed
        h.Poll();

        Assert.False(h.Held);
        Assert.False(h.Hold.BlockingBuffs);         // not even for the step in: there is none
        Assert.False(h.Hold.BlockingOrdinaryBuffs);
        Assert.Empty(h.Scheduled);                  // and it stops looking
        Assert.Contains(h.LoopLog, m => m.StartsWith("the wait outside the lair is over, buffs released"));
    }

    [Fact]
    public void PausedByHandMidWait_ReleasesTheHold()
    {
        Harness h = new() { SlotUsed = true, CastDue = true };
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs));

        h.Coordinator.AssertGate(MovementCoordinator.UserGate);
        h.Poll();

        Assert.False(h.Held);
        Assert.False(h.Hold.BlockingOrdinaryBuffs);
        Assert.EndsWith("paused by hand", h.Hold.Describe());
    }

    [Fact]
    public void MasterSwitchOffMidWait_ReleasesTheHold()
    {
        Harness h = new() { SlotUsed = true, CastDue = true };
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells));

        h.Coordinator.AssertGate(MovementCoordinator.AutoAllGate);
        h.Coordinator.ParkHoldsForMasterSwitch();
        h.Poll();

        Assert.False(Waiting(h.Hold));
        Assert.False(h.Hold.BlockingBuffs);
        Assert.DoesNotContain(MovementCoordinator.LairDebuffGate, h.Coordinator.ParkedGates);
    }

    // ----- When the buffs are let through again ---------------------

    // The buffs are held for the debuff. Once it has gone out they are let through,
    // whatever the way of waiting: nothing is kept back for the rest of the fight.
    [Theory]
    [InlineData(LairEntryDebuffMode.WaitForSpells)]
    [InlineData(LairEntryDebuffMode.BlockSpells)]
    [InlineData(LairEntryDebuffMode.WaitForPriorityBuffs)]
    public void DebuffSentInTheLair_ReleasesTheBuffs_AndTheLogSaysSo(LairEntryDebuffMode mode)
    {
        Harness h = new();
        Assert.True(h.Hold.ReadyToEnter(mode, new RoomKey(1, 100)));
        h.InCombat = true;                          // the step landed on the lair's monsters
        h.Hold.Advance();                           // the scheduler's pass as the fight starts
        h.Now = h.Now.AddSeconds(4);
        Assert.True(h.Hold.BlockingBuffs);
        Assert.True(h.Hold.BlockingOrdinaryBuffs);
        Assert.StartsWith("has the go-ahead, buffs held until the debuff is sent, the fight ends, or it is 11s old (4s now)",
            h.Hold.Describe());

        h.Hold.NoteDebuffSent("dbuf");

        Assert.False(h.Hold.BlockingBuffs);
        Assert.False(h.Hold.BlockingOrdinaryBuffs);
        Assert.Equal(new[] { "debuff dbuf sent: buffs released" }, h.LoopLog);
        Assert.StartsWith($"not waiting, no buff held; buffs last released: {h.Now.ToLocalTime():HH:mm:ss} debuff dbuf sent",
            h.Hold.Describe());
    }

    // No debuff goes out and the fight is over inside its first round: the hold has
    // nothing left to keep the cast for.
    [Fact]
    public void NoDebuffSent_TheBuffsAreReleasedWhenTheFightEnds()
    {
        Harness h = new();
        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 100)));
        h.InCombat = true;
        h.Hold.Advance();
        h.Now = h.Now.AddSeconds(3);
        Assert.True(h.Hold.BlockingBuffs);          // still fighting: still held

        h.InCombat = false;
        h.Hold.Advance();                           // the scheduler's pass at the kill

        Assert.False(h.Hold.BlockingBuffs);
        Assert.Equal(new[] { "buffs released: the fight ended with no debuff sent" }, h.LoopLog);
    }

    // A debuff the engine put off (behind a backstab, or a cast spent as the fight
    // began) sends no signal and is still coming. The hold waits two rounds of the
    // fight for it and no longer: a buff the character fights with, a shield cast
    // "during combat", must not wait the whole fight. The clock runs from the engage.
    [Theory]
    [InlineData(LairEntryDebuffMode.WaitForSpells)]
    [InlineData(LairEntryDebuffMode.BlockSpells)]
    [InlineData(LairEntryDebuffMode.WaitForPriorityBuffs)]
    public void NoDebuffSent_TheHoldLastsTwoRoundsOfTheFight_AndNoLonger(LairEntryDebuffMode mode)
    {
        Harness h = new();
        Assert.True(h.Hold.ReadyToEnter(mode, new RoomKey(1, 100)));
        h.Coordinator.AssertGate(MovementCoordinator.CombatGate);   // the lair's roster is read
        h.Now = h.Now.AddSeconds(2);
        h.Hold.Advance();
        h.InCombat = true;                          // the engage, two seconds on: the clock starts here
        h.Hold.Advance();
        h.Now = h.Now.AddSeconds(10.5);             // two rounds, and the pass that waits for HP to settle
        h.Hold.Advance();
        Assert.True(h.Hold.BlockingBuffs);

        h.Now = h.Now.AddSeconds(0.5);
        Assert.False(h.Hold.BlockingBuffs);         // by the clock alone
        h.Hold.Advance();

        Assert.Equal(new[] { "buffs released: the fight is two rounds old with no debuff sent" }, h.LoopLog);
        h.Now = h.Now.AddMinutes(2);                // the rest of a long fight
        Assert.False(h.Hold.BlockingBuffs);
    }

    // The combat engine opens the lair's fight with no debuff to cast (none allowed on
    // this target, its mana or cap not met): it has decided, and the buffs go at once.
    [Theory]
    [InlineData(LairEntryDebuffMode.WaitForSpells)]
    [InlineData(LairEntryDebuffMode.BlockSpells)]
    [InlineData(LairEntryDebuffMode.WaitForPriorityBuffs)]
    public void EngineOpensTheLairsFightWithNoDebuffToCast_TheBuffsAreReleasedAtOnce(LairEntryDebuffMode mode)
    {
        RoomKey lair = new(1, 100);
        Harness h = new() { FightRoom = new RoomKey(1, 99) };
        Assert.True(h.Hold.ReadyToEnter(mode, lair));
        h.FightRoom = lair;                         // the step is in flight, or has landed
        h.InCombat = true;
        h.Hold.Advance();
        Assert.True(h.Hold.BlockingBuffs);

        h.Hold.NoteNoDebuffThisFight();

        Assert.False(h.Hold.BlockingBuffs);
        Assert.Equal(new[] { "buffs released: the attack went out with no debuff to cast first" }, h.LoopLog);
    }

    // The same signal while the loop still stands at the door: the engine engaged
    // something there with no debuff to cast. That says nothing about the lair. Acting
    // on it handed a held buff the round's cast as the loop stepped in, and the lair's
    // debuff was put off a round.
    [Theory]
    [InlineData(LairEntryDebuffMode.WaitForSpells)]
    [InlineData(LairEntryDebuffMode.BlockSpells)]
    [InlineData(LairEntryDebuffMode.WaitForPriorityBuffs)]
    public void EngineSignalAtTheDoor_DoesNotReleaseTheBuffs(LairEntryDebuffMode mode)
    {
        RoomKey lair = new(1, 100);
        Harness atTheDoor = new() { FightRoom = new RoomKey(1, 99) };
        Assert.True(atTheDoor.Hold.ReadyToEnter(mode, lair));
        atTheDoor.Hold.NoteNoDebuffThisFight();
        Assert.True(atTheDoor.Hold.BlockingBuffs);
        Assert.Empty(atTheDoor.LoopLog);

        Harness whoKnowsWhere = new();              // the room isn't known: not taken for the lair
        Assert.True(whoKnowsWhere.Hold.ReadyToEnter(mode, lair));
        whoKnowsWhere.Hold.NoteNoDebuffThisFight();
        Assert.True(whoKnowsWhere.Hold.BlockingBuffs);
    }

    // A monster comes to where the loop stands after the go-ahead, before the step in.
    // It is a fight at the door like any other: buffs free in it, and the loop asks
    // again when it is over, so the cast is free again as it does step in.
    [Fact]
    public void FightAtTheDoorAfterTheGoAhead_HoldsNoBuff_AndTheLoopAsksAgainAfterIt()
    {
        RoomKey lair = new(1, 100);
        Harness h = new() { FightRoom = new RoomKey(1, 99) };
        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, lair));
        Assert.True(h.Hold.BlockingBuffs);

        h.InCombat = true;
        Assert.False(h.Hold.BlockingBuffs);         // read off the game: the fight is not the lair's
        h.Hold.Advance();
        Assert.Equal(new[] { "buffs released: a fight at the door before the step in" }, h.LoopLog);

        h.InCombat = false;
        h.SlotUsed = true;                          // the fight spent this round's cast
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, lair));
        Assert.True(h.Hold.BlockingBuffs);          // held again until the cast is free
    }

    // An empty lair, or one nothing in is fought: no fight starts, and the hold ends
    // with the entry window as it always did.
    [Fact]
    public void NoFightStarts_TheBuffsAreReleasedWhenTheEntryWindowEnds()
    {
        Harness h = new();
        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs, new RoomKey(1, 100)));
        h.Now = h.Now.AddSeconds(4);
        h.Hold.Advance();
        Assert.True(h.Hold.BlockingBuffs);

        h.Now = h.Now.AddSeconds(2);

        Assert.False(h.Hold.BlockingBuffs);
        h.Hold.Advance();
        Assert.Equal(new[] { "buffs released: no fight started within 5 seconds of the go-ahead" }, h.LoopLog);
    }

    // What is asked of the hold only reads. A bug report captured in the moment between
    // two kills, when "in combat" reads false, must not take the hold down: the next
    // buff would go out ahead of the debuff.
    [Fact]
    public void AskingTheHold_ChangesNothing_EvenBetweenTwoKills()
    {
        Harness h = new();
        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 100)));
        h.InCombat = true;
        h.Hold.Advance();

        h.InCombat = false;                         // between kills; no pass of the scheduler
        string report = h.Hold.Describe();
        bool held = h.Hold.BlockingBuffs && h.Hold.BlockingOrdinaryBuffs;
        h.InCombat = true;

        Assert.StartsWith("has the go-ahead, buffs held", report);
        Assert.True(held);
        Assert.True(h.Hold.BlockingBuffs);          // still held in the same fight
        Assert.Empty(h.LoopLog);
    }

    // A monster comes out to where the loop stands waiting. The debuff is for the
    // lair: nothing is held in that fight, and the wait is taken up again after it.
    [Theory]
    [InlineData(LairEntryDebuffMode.WaitForSpells)]
    [InlineData(LairEntryDebuffMode.BlockSpells)]
    [InlineData(LairEntryDebuffMode.WaitForPriorityBuffs)]
    public void FightWhereTheLoopStandsWaiting_HoldsNoBuff_AndTheWaitGoesOnAfterIt(LairEntryDebuffMode mode)
    {
        Harness h = new() { SlotUsed = true, DebuffMana = ("dbuf", 60, 35, 100) };
        Assert.False(h.Hold.ReadyToEnter(mode, new RoomKey(1, 100)));
        h.SlotUsed = false;
        h.Poll();
        Assert.True(h.HeldForMana);
        Assert.True(h.Hold.BlockingBuffs);

        h.InCombat = true;                          // or the combat gate: either reads as a fight
        Assert.False(h.Hold.BlockingBuffs);
        Assert.False(h.Hold.BlockingOrdinaryBuffs);
        h.SetMana(80);                              // mana is there, but no step is taken mid-fight
        h.Poll();
        Assert.True(Waiting(h.Hold));
        Assert.StartsWith("waiting (" + LairEntryDebuffModes.Label(mode) + "; a fight where it stands", h.Hold.Describe());

        h.InCombat = false;
        h.Poll();
        Assert.False(Waiting(h.Hold));             // the fight is over: mana is there, in it goes

        Harness gate = new() { SlotUsed = true };
        Assert.False(gate.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells));
        gate.Coordinator.AssertGate(MovementCoordinator.CombatGate);
        Assert.False(gate.Hold.BlockingBuffs);
    }

    // Report paradigm-20261012-080902: a loop set to attack only in lair rooms stood at
    // a lair's doorstep for seven minutes while a monster that had followed it swung
    // at the character. "In combat" was up on the swings alone, the loop fights
    // nothing there, and the wait stood for a fight nothing would end.
    [Theory]
    [InlineData(LairEntryDebuffMode.WaitForSpells)]
    [InlineData(LairEntryDebuffMode.BlockSpells)]
    [InlineData(LairEntryDebuffMode.WaitForPriorityBuffs)]
    public void AttackedWhereTheLoopDoesNotFight_IsNotWaitedOut_TheLoopEntersOnAFreeCast(LairEntryDebuffMode mode)
    {
        Harness h = new() { SlotUsed = true, InCombat = true, FightAnswered = false };

        Assert.False(h.Hold.ReadyToEnter(mode, new RoomKey(1, 100)));
        Assert.True(h.Held);
        // What the mode holds is held: this is no fight at the door.
        Assert.Equal(mode == LairEntryDebuffMode.BlockSpells, h.Hold.BlockingBuffs);
        Assert.Contains("a monster is attacking where the loop stands", h.Hold.Describe());

        h.SlotUsed = false;                         // the round turned over, the monster still swinging
        h.Poll();

        Assert.False(h.Held);
        Assert.False(Waiting(h.Hold));
        Assert.True(h.Hold.ReadyToEnter(mode, new RoomKey(1, 100)));
        Assert.Single(h.LoopLog, m => m.StartsWith("a monster is attacking at the door and the loop doesn't fight"));
        Assert.DoesNotContain("a fight at the door: buffs are free, the wait resumes after", h.LoopLog);
    }

    // User, 2026-10-12: "if we get held for having not enough mana to debuff, we should
    // fight.. if we're just waiting on the open cast round to be free to debuff, we
    // should not fight". The wait for mana says so once as it starts, so the engage
    // gates are asked again, and stands as a wait for mana through the fight.
    [Fact]
    public void StandingForMana_TheRoomIsFoughtIn_FromTheMomentTheWaitIsForMana()
    {
        Harness h = new()
        {
            SlotUsed = true, InCombat = true, FightAnswered = false, DebuffMana = ("dbuf", 60, 35, 100),
        };
        int gatesAsked = 0;
        h.Hold.ManaWaitStarted += () => gatesAsked++;

        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 100)));
        Assert.False(h.Hold.WaitingForMana);        // for the cast: the attack goes unanswered
        Assert.Equal(0, gatesAsked);

        h.SlotUsed = false;                         // the cast is free, and mana is short
        h.Poll();
        Assert.True(h.Hold.WaitingForMana);
        Assert.True(h.HeldForMana);
        Assert.Equal(1, gatesAsked);
        Assert.Contains(h.LoopLog, m => m.EndsWith("a monster in this room is fought"));

        h.FightAnswered = true;                     // the room is fought in now
        h.Poll();
        Assert.True(h.Hold.WaitingForMana);         // through the fight, so the room stays fought in
        Assert.True(h.HeldForMana);
        Assert.False(h.Hold.BlockingBuffs);
        Assert.Equal(1, gatesAsked);

        h.InCombat = false;                         // the kill
        h.SetMana(80);
        h.Poll();
        Assert.False(h.Hold.WaitingForMana);
        Assert.False(Waiting(h.Hold));
    }

    // The same monster once something does fight it there (a rest that has to clear
    // the room first): a fight at the door like any other, waited out.
    [Fact]
    public void AttackedWhereTheLoopDoesNotFight_ButTheClientFightsItAnyway_IsAFightAtTheDoor()
    {
        Harness h = new() { SlotUsed = true, InCombat = true, FightAnswered = false };
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 100)));
        Assert.True(h.Hold.BlockingBuffs);

        h.FightAnswered = true;
        h.SlotUsed = false;
        h.Poll();

        Assert.True(Waiting(h.Hold));              // no step mid-fight
        Assert.False(h.Hold.BlockingBuffs);
        Assert.Contains("a fight at the door: buffs are free, the wait resumes after", h.LoopLog);
    }

    // With the go-ahead given and the same monster still swinging at the door, the
    // buffs stay held for the step: a fight at the door would have let them go, and
    // one of them would take the cast the debuff is waiting for.
    [Fact]
    public void AttackedWhereTheLoopDoesNotFight_AfterTheGoAhead_TheBuffsStayHeldForTheStep()
    {
        RoomKey lair = new(1, 100);
        Harness h = new() { FightRoom = new RoomKey(1, 99), InCombat = true, FightAnswered = false };

        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, lair));
        h.Hold.Advance();
        Assert.True(h.Hold.BlockingBuffs);

        h.FightRoom = lair;                         // the step is in flight: the lair is fought in
        h.FightAnswered = true;
        h.Now = h.Now.AddSeconds(1);
        h.Hold.Advance();

        Assert.True(h.Hold.BlockingBuffs);          // held into the lair's fight, for the debuff
        Assert.DoesNotContain(h.LoopLog, m => m.StartsWith("buffs released"));
    }

    // The loop leaves the lair with the hold still up (it asks for its next step
    // before anything else lifted it): leaving lifts it.
    [Fact]
    public void LoopLeavesTheLair_TheBuffsAreReleased()
    {
        Harness h = new();
        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 100)));
        h.InCombat = true;
        Assert.True(h.Hold.BlockingBuffs);

        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.Off));   // the next step is no lair

        Assert.False(h.Hold.BlockingBuffs);
        Assert.Equal(new[] { "buffs released: no longer entering a lair" }, h.LoopLog);
    }

    // Released in one lair, held again at the next doorstep ask and not before: being
    // next to the next lair while still in this one holds nothing.
    [Theory]
    [InlineData(LairEntryDebuffMode.BlockSpells)]
    [InlineData(LairEntryDebuffMode.WaitForPriorityBuffs)]
    public void TheHoldReturnsAtTheNextDoorstepAsk_NotBefore(LairEntryDebuffMode mode)
    {
        Harness h = new();
        Assert.True(h.Hold.ReadyToEnter(mode, new RoomKey(1, 100)));
        h.InCombat = true;
        h.Hold.NoteDebuffSent("dbuf");
        h.Hold.NoteOrdinaryBuffCast("plai");              // a buff went out mid-fight: nothing is owed
        h.Now = h.Now.AddSeconds(20);               // the rest of the fight, next door to the next lair
        Assert.False(h.Hold.BlockingOrdinaryBuffs);

        h.InCombat = false;
        h.SlotUsed = true;                          // the loop is about to step into the next lair
        Assert.False(h.Hold.ReadyToEnter(mode, new RoomKey(1, 101)));

        Assert.True(h.Hold.BlockingOrdinaryBuffs);
    }

    // Buffs held at one lair that have had no cast by the next lair's doorstep get one
    // cast there before they are held again: nothing is held until a buff goes out.
    [Theory]
    [InlineData(LairEntryDebuffMode.BlockSpells)]
    [InlineData(LairEntryDebuffMode.WaitForPriorityBuffs)]
    public void BuffsHeldAtTheLastLair_WithNoCastSince_GetOneCastAtTheNextDoorstep(LairEntryDebuffMode mode)
    {
        Harness h = new();
        h.OrdinaryDue.Add("plai");
        Assert.True(h.Hold.ReadyToEnter(mode, new RoomKey(1, 100)));   // plai is due, and held from here
        h.InCombat = true;
        h.Hold.Advance();
        Assert.True(h.Hold.BlockingBuffs);
        h.InCombat = false;                         // a one-round kill; no pass of the scheduler since

        h.CastDue = true;                           // plai, now that nothing holds it
        Assert.False(h.Hold.ReadyToEnter(mode, new RoomKey(1, 101)));
        Assert.True(h.Held);
        Assert.False(h.Hold.BlockingOrdinaryBuffs); // let through, at this doorstep
        Assert.Contains("buffs an earlier lair held have had no cast since — one goes out here before they are "
            + "held again (due: plai)", h.LoopLog);
        Assert.DoesNotContain(h.LoopLog, m => m.StartsWith("waiting outside the lair"));   // nothing is held yet
        Assert.StartsWith("waiting (" + LairEntryDebuffModes.Label(mode) + "; giving the one cast owed to buffs an "
            + "earlier lair held, 8s left of 8s; nothing held; due: plai)", h.Hold.Describe());

        h.SlotUsed = true;                          // it went out
        h.Hold.NoteOrdinaryBuffCast("plai");
        h.OrdinaryDue.Clear();
        h.OrdinaryDue.Add("pla2");                  // another is due, and is not let through as well
        h.Poll();
        Assert.True(h.Hold.BlockingOrdinaryBuffs);  // one cast: they are held again
        Assert.Contains(h.LoopLog, m => m.StartsWith("waiting outside the lair until the debuff can be cast"));

        h.SlotUsed = false;
        h.CastDue = false;
        h.Poll();
        Assert.False(h.Held);
    }

    // The owed cast is one fixed window. A buff of any kind going out in it ends it,
    // and its eight seconds are not started again by one: a priority buff that is
    // always due used to restart them every round, and the loop never left the door.
    [Fact]
    public void TheOwedCast_IsOneWindow_EndedByTheFirstBuffOfAnyKind_AndNeverRestarted()
    {
        Harness h = new();
        h.OrdinaryDue.Add("plai");
        h.PriorityDue.Add("star");
        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs, new RoomKey(1, 100)));   // plai held

        h.CastDue = true;                           // star, and plai once nothing holds it
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs, new RoomKey(1, 101)));
        Assert.False(h.Hold.BlockingOrdinaryBuffs);

        h.SlotUsed = true;                          // star takes the cast: a buff went out
        h.Hold.NotePriorityBuffCast("star");
        h.Poll();

        Assert.True(h.Hold.BlockingOrdinaryBuffs);  // the owed cast is over; the wait proper holds plai again
        Assert.Contains("priority buff star cast while waiting outside the lair", h.LoopLog);
        Assert.DoesNotContain("giving the one cast owed", h.Hold.Describe());
    }

    // Inside the owed cast the wait's own rules stand: a priority buff sent three times
    // is given up on and held, and the loop still gets in.
    [Fact]
    public void PriorityBuffAlwaysDue_AtASecondLair_IsGivenUpOnThere_AndTheLoopEnters()
    {
        Harness h = new() { CastDue = true };
        h.OrdinaryDue.Add("plai");
        h.PriorityDue.Add("star");
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs, new RoomKey(1, 100)));
        for (int send = 1; send <= 3; send++) CastPriorityBuff(h, "star");
        h.SlotUsed = false;
        h.PriorityDue.Clear();                      // star is held now: nothing the wait waits for
        h.CastDue = false;
        h.Poll();
        Assert.False(h.Held);

        h.PriorityDue.Add("star");                  // it is due again at the next lair
        h.CastDue = true;
        h.Now = h.Now.AddSeconds(10);
        DateTimeOffset atTheDoor = h.Now;
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs, new RoomKey(1, 101)));
        int sends = 0;
        while (h.Held && sends < 50)
        {
            CastPriorityBuff(h, "star");
            sends++;
            if (h.Hold.HasGivenUpOn("star")) h.CastDue = false;   // held: no longer due to the wait
            h.SlotUsed = false;
            h.Poll();
        }

        Assert.False(h.Held);
        Assert.Equal(3, sends);                     // three sends at this door, as at any door
        Assert.True(h.Now - atTheDoor <= TimeSpan.FromSeconds(20));
        // Given up on once at each lair, said once each time: not every round.
        Assert.Equal(2, h.LoopLog.Count(m => m.StartsWith("priority buff star went out 3 times outside this lair")));
    }

    // The owed cast is bounded: nothing due ends it at once, and something due that
    // never goes out ends it after eight seconds. A window that ran out that way is
    // not owed again at the next lair while the same buffs are what is due: standing
    // eight seconds at every door for a buff that can't be cast buys nothing.
    [Fact]
    public void TheOwedCast_RunsOutAfterEightSeconds_AndIsNotOwedAgainForTheSameBuffs()
    {
        Harness nothingDue = new();
        Assert.True(nothingDue.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 100)));
        Assert.True(nothingDue.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 101)));

        Harness stuck = new();
        stuck.OrdinaryDue.Add("#wand");             // an item buff whose item can't be used: due for ever
        Assert.True(stuck.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 100)));
        stuck.CastDue = true;
        Assert.False(stuck.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 101)));
        stuck.Now = stuck.Now.AddSeconds(7);
        stuck.Poll();
        Assert.True(stuck.Held);

        stuck.Now = stuck.Now.AddSeconds(2);
        stuck.Poll();
        Assert.False(stuck.Held);                   // Block spells: in on the free cast, the rest held

        Assert.True(stuck.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 102)));   // no eight seconds here
        Assert.True(stuck.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 103)));
        Assert.Single(stuck.LoopLog, m => m.StartsWith("buffs an earlier lair held"));

        stuck.OrdinaryDue.Add("new");               // what is due has changed: owed again
        Assert.False(stuck.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 104)));
    }

    // An ordinary room between two lairs can be one quick step, with no pass of the
    // cast scheduler in it: what the first lair held is still owed its cast at the
    // second. A buff that did go out in between has paid it.
    [Fact]
    public void WhatALairHeld_IsStillOwedAfterAnOrdinaryRoom_UnlessABuffWentOutThere()
    {
        Harness h = new();
        h.OrdinaryDue.Add("plai");
        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 100)));
        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.Off));     // the step into the room between
        h.CastDue = true;
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 101)));
        Assert.False(h.Hold.BlockingBuffs);         // the owed cast, at the second lair's door

        Harness paid = new();
        paid.OrdinaryDue.Add("plai");
        Assert.True(paid.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 100)));
        Assert.True(paid.Hold.ReadyToEnter(LairEntryDebuffMode.Off));
        paid.Hold.NoteOrdinaryBuffCast("plai");           // it went out in the room between
        paid.OrdinaryDue.Clear();
        Assert.True(paid.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 101)));

        Harness stopped = new();
        stopped.OrdinaryDue.Add("plai");
        Assert.True(stopped.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 100)));
        stopped.Hold.Reset("the loop stopped");     // a stop forgets what was owed
        stopped.CastDue = true;
        Assert.True(stopped.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 101)));
    }

    // A buff that went out after the release has had its cast: nothing is owed, and
    // the same lair asked for again owes nothing either.
    [Fact]
    public void NoCastIsOwed_AfterABuffWentOut_NorOnAReAskForTheSameLair()
    {
        RoomKey lair = new(1, 100);
        Harness h = new();
        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, lair));
        h.CastDue = true;
        h.SlotUsed = true;
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, lair));   // the step didn't land
        Assert.True(h.Hold.BlockingBuffs);          // held, as before: no owed cast at the lair that held them

        Harness paid = new();
        Assert.True(paid.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, lair));
        paid.Hold.NoteDebuffSent("dbuf");
        paid.Hold.NoteOrdinaryBuffCast("plai");
        paid.CastDue = true;
        Assert.True(paid.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 101)));
    }

    // Wait for spells holds nothing at a doorstep, so it owes nothing at the next.
    [Fact]
    public void WaitForSpells_OwesNoCast_ItsWaitIsAsItWas()
    {
        Harness h = new();
        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells, new RoomKey(1, 100)));
        h.Now = h.Now.AddSeconds(6);
        h.CastDue = true;

        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells, new RoomKey(1, 101)));

        Assert.StartsWith("waiting (Wait for spells; 0/3 rounds, 0/18s; nothing held;", h.Hold.Describe());
        Assert.DoesNotContain(h.LoopLog, m => m.StartsWith("buffs an earlier lair held"));
    }

    // ----- Mana for the debuff ---------------------------------------

    // The cast is free and nothing is due, but mana is under the debuff's own "min
    // mana per cast": the combat engine wouldn't cast it, so walking in now would be
    // the entry without a debuff this hold is for. Every mode stays until mana is back.
    [Theory]
    [InlineData(LairEntryDebuffMode.WaitForSpells)]
    [InlineData(LairEntryDebuffMode.BlockSpells)]
    [InlineData(LairEntryDebuffMode.WaitForPriorityBuffs)]
    public void ManaUnderTheDebuffsMinimum_HoldsAndSaysWhy_ThenEntersWhenManaIsBack(LairEntryDebuffMode mode)
    {
        Harness h = new() { DebuffMana = ("dbuf", 60, 35, 100) };

        Assert.False(h.Hold.ReadyToEnter(mode));

        Assert.True(h.HeldForMana);
        Assert.False(h.Held);                       // its own gate, so the window says "for mana"
        Assert.True(h.Hold.BlockingBuffs);          // a buff now would spend what it waits for
        const string Why = "waiting outside the lair for mana: 35 of the 60 the debuff dbuf needs "
            + "(its min mana per cast) — buffs held back meanwhile, bar the ones that bring mana back; a monster in this room is fought";
        Assert.True(h.Hold.HoldingForMana);
        Assert.Equal(new[] { Why }, h.LoopLog);
        Assert.StartsWith("waiting for mana (", h.Hold.Describe());
        Assert.Contains("mana 35 of the 60 the debuff dbuf needs, its min mana per cast", h.Hold.Describe());
        Assert.Equal(TimeSpan.FromSeconds(1), h.ScheduledDelays[^1]);

        h.SetMana(50);
        h.Now = h.Now.AddSeconds(30);
        h.Poll();
        Assert.True(h.HeldForMana);
        Assert.Single(h.LoopLog);                   // not said again within the minute

        h.Now = h.Now.AddSeconds(31);
        h.Poll();
        Assert.Equal(2, h.LoopLog.Count());         // once a minute while it stands
        Assert.StartsWith("waiting outside the lair for mana: 50 of the 60", h.LoopLog.Last());

        h.SetMana(60);
        h.Poll();

        Assert.False(h.HeldForMana);
        Assert.False(h.Held);
        Assert.True(h.Hold.BlockingBuffs);          // held for the step in, as ever
        Assert.Equal("mana is back at 60, and the debuff dbuf needs 60 — entering the lair", h.LoopLog.Last());
    }

    // With the slot's minimum at 0 the debuff still needs what the spell costs: the
    // log says that figure is the cost, so nobody goes looking for a minimum that
    // isn't set.
    [Fact]
    public void ManaUnderTheDebuffsCost_TheLogSaysTheFigureIsItsCost()
    {
        Harness h = new() { DebuffMana = ("dbuf", 12, 5, 100), NeedIsTheSpellsCost = true };

        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells));

        Assert.True(h.HeldForMana);
        Assert.Equal(new[]
        {
            "waiting outside the lair for mana: 5 of the 12 the debuff dbuf needs "
                + "(its mana cost) — buffs held back meanwhile, bar the ones that bring mana back; a monster in this room is fought",
        }, h.LoopLog);
        Assert.Contains("mana 5 of the 12 the debuff dbuf needs, its mana cost", h.Hold.Describe());
    }

    [Theory]
    [InlineData(LairEntryDebuffMode.WaitForSpells)]
    [InlineData(LairEntryDebuffMode.BlockSpells)]
    [InlineData(LairEntryDebuffMode.WaitForPriorityBuffs)]
    public void MinimumAboveMaximumMana_CanNeverBeMet_IsNotWaitedFor_AndSaidOnce(LairEntryDebuffMode mode)
    {
        RoomKey lair = new(1, 100);
        Harness h = new() { DebuffMana = ("dbuf", 150, 80, 100) };

        Assert.True(h.Hold.ReadyToEnter(mode, lair));
        Assert.True(h.Hold.ReadyToEnter(mode, lair));   // the step is asked for again

        Assert.False(h.HeldForMana);
        Assert.Equal(new[]
        {
            "the debuff dbuf asks for 150 mana (its min mana per cast) and maximum mana is 100: that can never "
                + "be met, so the lair is entered without waiting for it",
        }, h.LoopLog);
    }

    // The maximum the client believes in can overshoot the game's (the game calls 423
    // full while the client reads 423/448): mana then never reaches a minimum set
    // between the two, and the wait had no way out. At what the game calls full, mana
    // is not waited for, from the start or once it gets there.
    [Theory]
    [InlineData(LairEntryDebuffMode.WaitForSpells)]
    [InlineData(LairEntryDebuffMode.BlockSpells)]
    [InlineData(LairEntryDebuffMode.WaitForPriorityBuffs)]
    public void ManaAsFullAsTheGameTakesIt_AndStillUnderTheMinimum_IsNotWaitedFor_AndSaidOnce(LairEntryDebuffMode mode)
    {
        RoomKey lair = new(1, 100);
        Harness full = new() { DebuffMana = ("dbuf", 440, 423, 448), ManaIsFull = true };
        Assert.True(full.Hold.ReadyToEnter(mode, lair));
        Assert.True(full.Hold.ReadyToEnter(mode, lair));
        Assert.Equal(new[]
        {
            "the debuff dbuf asks for 440 mana (its min mana per cast) and mana is full at 423: it will come back "
                + "no higher, so the lair is entered without waiting for it",
        }, full.LoopLog);

        Harness filling = new() { DebuffMana = ("dbuf", 440, 300, 448) };
        Assert.False(filling.Hold.ReadyToEnter(mode, lair));
        filling.Now = filling.Now.AddHours(1);
        filling.Poll();
        Assert.True(filling.HeldForMana);           // not full yet: it stays, however long

        filling.SetMana(423);
        filling.ManaIsFull = true;                  // the game says so
        filling.Poll();
        Assert.False(filling.HeldForMana);
        Assert.False(Waiting(filling.Hold));
    }

    [Theory]
    [InlineData(LairEntryDebuffMode.WaitForSpells)]
    [InlineData(LairEntryDebuffMode.BlockSpells)]
    [InlineData(LairEntryDebuffMode.WaitForPriorityBuffs)]
    public void ManaAtTheMinimum_OrNoMinimumSet_NothingIsWaitedFor(LairEntryDebuffMode mode)
    {
        Harness atMinimum = new() { DebuffMana = ("dbuf", 60, 60, 100) };
        Assert.True(atMinimum.Hold.ReadyToEnter(mode));
        Assert.Empty(atMinimum.LoopLog);

        Harness noMinimum = new();
        Assert.True(noMinimum.Hold.ReadyToEnter(mode));
        Assert.Empty(noMinimum.LoopLog);
    }

    // The casts come first, then the mana they left: and once the wait stands for
    // mana, a buff that comes due is held with the rest.
    [Fact]
    public void ManaWait_FollowsTheCasts_AndHoldsABuffThatComesDueMeanwhile()
    {
        Harness h = new() { CastDue = true, DebuffMana = ("dbuf", 60, 35, 100) };

        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells));
        Assert.True(h.Held);                        // for the cast, first
        Assert.False(h.HeldForMana);
        Assert.False(h.Hold.BlockingBuffs);

        h.CastDue = false;                          // what was due has gone out
        h.Poll();
        Assert.True(h.HeldForMana);
        Assert.False(h.Held);
        Assert.True(h.Hold.BlockingBuffs);

        h.CastDue = true;                           // another buff comes due
        h.Poll();
        Assert.True(h.Hold.BlockingBuffs);          // held: it isn't let through to spend the mana

        h.SetMana(70);
        h.Poll();
        Assert.False(h.HeldForMana);
    }

    // Nothing but the loop going away, or the minimum being taken off, ends a wait for
    // mana before the mana is there (or is as full as it gets): no time limit.
    [Fact]
    public void ManaWait_HasNoTimeLimit_ButEndsWhenTheLoopStops_OrTheMinimumIsTakenOff()
    {
        Harness h = new() { DebuffMana = ("dbuf", 60, 35, 100) };
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs));
        h.Now = h.Now.AddMinutes(10);
        h.Poll();
        Assert.True(h.HeldForMana);

        h.StillWanted = false;                      // Stop
        h.Poll();
        Assert.False(h.HeldForMana);
        Assert.False(h.Hold.BlockingBuffs);

        Harness edited = new() { DebuffMana = ("dbuf", 60, 35, 100) };
        Assert.False(edited.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells));
        edited.DebuffMana = null;                   // the player cleared the debuff's minimum in Settings
        edited.Poll();
        Assert.False(edited.HeldForMana);
        Assert.StartsWith("mana for the debuff is no longer waited for", edited.LoopLog.Last());
    }

    [Fact]
    public void TheNavigationWindow_SaysItIsManaTheLoopWaitsFor()
    {
        Assert.Equal(("Waiting — Waiting for Mana to Debuff", MudPlay.ViewModels.Navigation.NavActivityKind.Waiting),
            MudPlay.ViewModels.Navigation.NavActivity.Describe(
                new[] { MovementCoordinator.LairDebuffManaGate }, isPaused: true, isMovementPrevented: false));
        Assert.Contains(("Waiting for Mana to Debuff", MudPlay.ViewModels.Navigation.NavChipTone.Wait),
            MudPlay.ViewModels.Navigation.NavActivity.ActiveHolds(
                new[] { MovementCoordinator.LairDebuffManaGate }, isMovementPrevented: false));
    }

    // ----- Wait for priority buff spells -----------------------------

    // While it waits, only the buffs not ticked priority are kept back: the priority
    // ones may take the cast. For the step in, every buff waits, as in the other modes.
    [Fact]
    public void WaitForPriorityBuffs_HoldsOnlyOrdinaryBuffsWhileItWaits_ThenEveryBuffForTheStep()
    {
        Harness h = new() { SlotUsed = true, CastDue = true };

        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs));
        Assert.True(h.Held);
        Assert.False(h.Hold.BlockingBuffs);
        Assert.True(h.Hold.BlockingOrdinaryBuffs);

        h.SlotUsed = false;                         // free, a priority buff still due:
        h.Poll();
        Assert.True(h.Held);                        // it gets its turn

        h.SlotUsed = true;                          // it cast
        h.Poll();
        h.SlotUsed = false;                         // round turned over, none left due
        h.CastDue = false;
        h.Poll();

        Assert.False(h.Held);
        Assert.True(h.Hold.BlockingBuffs);
        Assert.True(h.Hold.BlockingOrdinaryBuffs);
    }

    [Fact]
    public void WaitForPriorityBuffs_NothingDue_EntersOnAFreeCast()
    {
        Harness h = new();

        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs));

        Assert.False(h.Held);
        Assert.True(h.Hold.BlockingBuffs);
    }

    // A round a buff: stand here, the cast free with a priority buff due, then the
    // buff going out and its round passing.
    private static void CastPriorityBuff(Harness h, string buff, int roundSeconds = 5)
    {
        h.SlotUsed = false; h.Poll();
        h.SlotUsed = true; h.Hold.NotePriorityBuffCast(buff); h.Poll();
        h.Now = h.Now.AddSeconds(roundSeconds);
    }

    // No limit on rounds: every due priority buff gets its round, well past the three
    // rounds (and the 18 seconds in all) that Wait for spells stops at.
    [Fact]
    public void WaitForPriorityBuffs_KeepsWaitingWhilePriorityBuffsGoOut_HoweverMany()
    {
        Harness h = new() { SlotUsed = true, CastDue = true };
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs));

        for (int buff = 1; buff <= 8; buff++) CastPriorityBuff(h, $"sta{buff}");

        Assert.True(h.Held);                        // 40 seconds and eight rounds in
        Assert.False(h.Hold.BlockingBuffs);         // and a ninth would still be let through

        h.SlotUsed = false;                         // none left due
        h.CastDue = false;
        h.Poll();

        Assert.False(h.Held);
        Assert.Contains("no priority buff is due and the between-round cast is free — entering the lair", h.LoopLog);
    }

    // A priority buff that is due and never goes out can't park the loop.
    [Fact]
    public void WaitForPriorityBuffs_NoneGoingOut_EntersAfterEighteenSeconds_AndSaysWhy()
    {
        Harness h = new() { CastDue = true };
        h.PriorityDue.Add("star");
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs));

        h.Now = h.Now.AddSeconds(17);
        h.Poll();
        Assert.True(h.Held);

        h.Now = h.Now.AddSeconds(3);
        h.Poll();

        Assert.False(h.Held);
        Assert.Contains(h.LoopLog, m => m.StartsWith(
            "no priority buff went out for 18 seconds (priority buffs still due: star)"));
    }

    // The 18 seconds run from the last priority buff that went out, not from the start.
    [Fact]
    public void WaitForPriorityBuffs_EachBuffGoingOutRestartsTheEighteenSeconds()
    {
        Harness h = new() { CastDue = true };
        h.PriorityDue.Add("stuck");
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs));

        h.Now = h.Now.AddSeconds(15);
        CastPriorityBuff(h, "sta1");                // at 15 s; its round ends at 20 s
        h.SlotUsed = false;
        h.Now = h.Now.AddSeconds(12);               // 32 s in, 17 s since sta1
        h.Poll();
        Assert.True(h.Held);
        Assert.False(h.Hold.BlockingBuffs);

        h.Now = h.Now.AddSeconds(2);                // 19 s since sta1
        h.Poll();

        Assert.False(h.Held);
        Assert.Contains(h.LoopLog, m => m.StartsWith(
            "no priority buff went out for 18 seconds (priority buffs still due: stuck)"));
    }

    // One buff sent again and again is not taking (refused, or rerolling without end).
    // Its third send is its last here: that buff alone is given up on and held back
    // like the buffs not ticked priority, and the wait goes on for the others.
    [Fact]
    public void WaitForPriorityBuffs_OneBuffSentThreeTimes_IsGivenUpOn_TheWaitGoesOnForTheRest()
    {
        Harness h = new() { SlotUsed = true, CastDue = true };
        h.PriorityDue.Add("star");
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs));

        CastPriorityBuff(h, "star");
        CastPriorityBuff(h, "other");               // a different buff doesn't count against it
        CastPriorityBuff(h, "star");
        Assert.False(h.Hold.HasGivenUpOn("star"));   // twice: still let through

        Assert.Equal(3, LairEntryDebuffHold.MaxSendsOfOnePriorityBuff);
        CastPriorityBuff(h, "star");
        Assert.True(h.Hold.HasGivenUpOn("star"));    // three times: held back from here on
        Assert.False(h.Hold.HasGivenUpOn("other"));
        Assert.False(h.Hold.BlockingBuffs);         // the other priority buffs still get their turn
        Assert.Contains(h.LoopLog, m => m.StartsWith(
            "priority buff star went out 3 times outside this lair, the most one may"));
        Assert.Contains("priority buff star cast again while waiting outside the lair (sent 2 times)", h.LoopLog);

        CastPriorityBuff(h, "late");                // one of the others, a round later
        Assert.True(h.Held);

        h.SlotUsed = false;                         // nothing but star left due, and star is held
        h.CastDue = false;
        h.Poll();

        Assert.False(h.Held);
        Assert.Contains("no priority buff is due (held back after 3 sends: star) and the between-round cast "
            + "is free — entering the lair", h.LoopLog);
    }

    // A step that doesn't land is asked for again, which starts a new wait. What the
    // last one learned outside the same lair still stands: the buff given up on stays
    // held, and its sends aren't counted from nothing. Another lair starts clean.
    [Fact]
    public void WaitForPriorityBuffs_ReAskForTheSameLair_KeepsWhatWasGivenUpOn_AnotherLairForgetsIt()
    {
        RoomKey lair = new(1, 100), nextLair = new(1, 101);
        Harness h = new() { SlotUsed = true, CastDue = true };
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs, lair));
        for (int send = 1; send <= 3; send++) CastPriorityBuff(h, "star");
        h.SlotUsed = false; h.CastDue = false;
        h.Poll();
        Assert.False(h.Held);

        h.Now = h.Now.AddSeconds(6);                // the step never landed
        h.SlotUsed = true;
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs, lair));
        Assert.True(h.Hold.HasGivenUpOn("star"));
        Assert.Contains("given up on: star", h.Hold.Describe());

        h.SlotUsed = false; h.Poll();               // in, and on to the next lair
        h.Hold.NoteOrdinaryBuffCast("plai");              // a buff went out in there: none is owed a cast
        h.SlotUsed = true;
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs, nextLair));
        Assert.False(h.Hold.HasGivenUpOn("star"));
        Assert.Contains("sent: none; given up on: none", h.Hold.Describe());
    }

    // The 18 seconds are waited out once outside a lair, not again at each re-ask.
    [Fact]
    public void WaitForPriorityBuffs_TimeRanOutOnce_IsNotWaitedOutAgainForTheSameLair()
    {
        RoomKey lair = new(1, 100);
        Harness h = new() { CastDue = true };
        h.PriorityDue.Add("stuck");
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs, lair));
        h.Now = h.Now.AddSeconds(19);
        h.Poll();
        Assert.False(h.Held);

        h.Now = h.Now.AddSeconds(6);                // the step never landed; stuck is still due
        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs, lair));
        Assert.False(h.Held);

        h.Now = h.Now.AddSeconds(6);                // another lair: its own 18 seconds
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs, new RoomKey(1, 101)));
        Assert.True(h.Held);
    }

    // A priority buff cast with no priority-buff wait on is nothing to this hold, so the
    // other modes' limits stay as they were.
    [Fact]
    public void PriorityBuffCasts_OutsideAPriorityBuffWait_ChangeNothing()
    {
        Harness idle = new();
        idle.Hold.NotePriorityBuffCast("star");
        Assert.Empty(idle.LoopLog);

        Harness h = new() { CastDue = true };
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells));
        h.Now = h.Now.AddSeconds(17);
        for (int i = 0; i < 5; i++) h.Hold.NotePriorityBuffCast("star");
        h.Poll();
        Assert.True(h.Held);                        // five sends of one buff: no limit of this mode's
        Assert.False(h.Hold.BlockingBuffs);

        h.Now = h.Now.AddSeconds(3);                // and its 18 seconds still run from the start
        h.Poll();
        Assert.False(h.Held);
    }

    [Fact]
    public void WaitForPriorityBuffs_LogsWhatItWaitsForAndHolds_AndWhyItWentIn()
    {
        Harness h = new() { CastDue = true };
        h.PriorityDue.Add("star");
        h.OrdinaryDue.Add("plai");
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs));

        Assert.Contains("waiting outside the lair until the debuff can be cast — casting priority buffs first "
            + "(due: star); other buffs held back (due: plai)", h.LoopLog);
        Assert.StartsWith("waiting (Wait for priority buff spells; 0s, 0/18s since a priority buff went out; "
            + "sent: none; given up on: none; buffs not ticked priority held; "
            + "priority buffs due: star; other buffs due: plai)",
            h.Hold.Describe());

        h.Now = h.Now.AddSeconds(4);
        h.Hold.NotePriorityBuffCast("star");
        h.Hold.NotePriorityBuffCast("star");
        h.Now = h.Now.AddSeconds(3);
        Assert.StartsWith("waiting (Wait for priority buff spells; 7s, 3/18s since a priority buff went out; "
            + "sent: star x2;", h.Hold.Describe());
        Assert.Contains("priority buff star cast while waiting outside the lair", h.LoopLog);

        h.PriorityDue.Clear();
        h.CastDue = false;
        h.Poll();

        Assert.Contains("no priority buff is due and the between-round cast is free — entering the lair", h.LoopLog);
        // Stamped in local time, as the program log beside it in a bug report is.
        Assert.StartsWith("has the go-ahead, buffs held until the debuff is sent, or 5s pass with no fight; "
            + $"last wait: {h.Now.ToLocalTime():HH:mm:ss} no priority buff is due",
            h.Hold.Describe());
    }

    // The other two modes keep what they held: nothing while Wait for spells lets
    // casts through, every buff in Block spells.
    [Fact]
    public void OtherModes_HoldOrdinaryBuffsExactlyWhenTheyHoldEveryBuff()
    {
        Harness wait = new() { SlotUsed = true, CastDue = true };
        Assert.False(wait.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells));
        Assert.False(wait.Hold.BlockingOrdinaryBuffs);

        Harness block = new() { SlotUsed = true, CastDue = true };
        Assert.False(block.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells));
        Assert.True(block.Hold.BlockingOrdinaryBuffs);
    }

    [Fact]
    public void TurningAway_FromAPriorityBuffWait_ReleasesTheOrdinaryBuffs()
    {
        Harness h = new() { SlotUsed = true, CastDue = true };
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs));

        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.Off));

        Assert.False(h.Held);
        Assert.False(h.Hold.BlockingOrdinaryBuffs);
        Assert.Equal($"not waiting, no buff held; last wait: {h.Now.ToLocalTime():HH:mm:ss} no longer entering a lair",
            h.Hold.Describe());
    }

    [Fact]
    public void PriorityBuffMode_MapsToTheThirdChoice_AndIsNamedAsTheListNamesIt()
    {
        Assert.Equal(LairEntryDebuffMode.WaitForPriorityBuffs, LairEntryDebuffModes.From(on: true, choice: 2));
        Assert.Equal(LairEntryDebuffMode.Off, LairEntryDebuffModes.From(on: false, choice: 2));
        Assert.Equal(2, LairEntryDebuffModes.ChoiceOf(LairEntryDebuffMode.WaitForPriorityBuffs));
        Assert.True(LairEntryDebuffModes.IsOn(LairEntryDebuffMode.WaitForPriorityBuffs));
        Assert.Equal("Wait for priority buff spells", LairEntryDebuffModes.Label(LairEntryDebuffMode.WaitForPriorityBuffs));
        Assert.Equal(3, (int)LairEntryDebuffMode.WaitForPriorityBuffs);   // stored and shared: never renumbered
    }

    [Theory]
    [InlineData(false, 0, LairEntryDebuffMode.Off)]
    [InlineData(false, 1, LairEntryDebuffMode.Off)]
    [InlineData(true, 0, LairEntryDebuffMode.WaitForSpells)]
    [InlineData(true, 1, LairEntryDebuffMode.BlockSpells)]
    public void Modes_MapToTheTickBoxAndChoice(bool on, int choice, LairEntryDebuffMode mode)
    {
        Assert.Equal(mode, LairEntryDebuffModes.From(on, choice));
        Assert.Equal(on, LairEntryDebuffModes.IsOn(mode));
        if (on) Assert.Equal(choice, LairEntryDebuffModes.ChoiceOf(mode));
    }
}
