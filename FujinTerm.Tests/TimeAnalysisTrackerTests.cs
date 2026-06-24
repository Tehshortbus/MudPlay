using FujinTerm.Game;
using FujinTerm.Game.Combat;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Phase 11 — <see cref="TimeAnalysisTracker"/> activity partition + affliction
/// overlays. An injected clock makes the wall-clock arithmetic deterministic:
/// inputs are pushed via the <c>Note*</c> forwarders and time advanced by hand,
/// so each test pins exactly how a slice is attributed.
/// </summary>
public sealed class TimeAnalysisTrackerTests
{
    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);
    }

    private static (TimeAnalysisTracker, Clock) Make()
    {
        Clock c = new();
        return (new TimeAnalysisTracker(() => c.Now), c);
    }

    // Full-pool vitals so plain NotePlayerState calls don't accidentally read as
    // "resting for HP/MA" unless the test means them to.
    private static void Combat(TimeAnalysisTracker t, bool on) =>
        t.NotePlayerState(on, PlayerPosition.Standing, 100, 100, 100, 100);

    // ----- fresh / default ---------------------------------------------

    [Fact]
    public void Fresh_AllZero()
    {
        (TimeAnalysisTracker t, _) = Make();
        TimeAnalysisStats s = t.Snapshot();
        Assert.Equal(TimeSpan.Zero, s.TimeOn);
        Assert.Equal(TimeSpan.Zero, s.Waiting);
        Assert.Equal(TimeSpan.Zero, s.Attacking);
        Assert.Equal(TimeSpan.Zero, s.Blinded);
    }

    [Fact]
    public void IdleTime_AccruesToWaiting()
    {
        (TimeAnalysisTracker t, Clock c) = Make();
        c.Advance(10);
        TimeAnalysisStats s = t.Snapshot();
        Assert.Equal(TimeSpan.FromSeconds(10), s.Waiting);
        Assert.Equal(TimeSpan.FromSeconds(10), s.TimeOn);
        Assert.Equal(TimeSpan.Zero, s.Attacking);
    }

    // ----- activity partition ------------------------------------------

    [Fact]
    public void CombatTime_AccruesToAttacking_ThenWaitingWhenItEnds()
    {
        (TimeAnalysisTracker t, Clock c) = Make();
        Combat(t, true);
        c.Advance(10);
        Combat(t, false);
        c.Advance(5);

        TimeAnalysisStats s = t.Snapshot();
        Assert.Equal(TimeSpan.FromSeconds(10), s.Attacking);
        Assert.Equal(TimeSpan.FromSeconds(5), s.Waiting);
        Assert.Equal(TimeSpan.FromSeconds(15), s.TimeOn);
    }

    [Fact]
    public void Resting_WithHpGap_IsRestingHp()
    {
        (TimeAnalysisTracker t, Clock c) = Make();
        t.NotePlayerState(false, PlayerPosition.Resting, hp: 40, maxHp: 100, ma: 100, maxMa: 100);
        c.Advance(8);

        TimeAnalysisStats s = t.Snapshot();
        Assert.Equal(TimeSpan.FromSeconds(8), s.RestingHp);
        Assert.Equal(TimeSpan.Zero, s.RestingMa);
        Assert.Equal(TimeSpan.FromSeconds(8), s.Resting);
    }

    [Fact]
    public void Meditating_IsRestingMa()
    {
        (TimeAnalysisTracker t, Clock c) = Make();
        t.NotePlayerState(false, PlayerPosition.Meditating, hp: 100, maxHp: 100, ma: 10, maxMa: 100);
        c.Advance(6);

        TimeAnalysisStats s = t.Snapshot();
        Assert.Equal(TimeSpan.FromSeconds(6), s.RestingMa);
        Assert.Equal(TimeSpan.Zero, s.RestingHp);
    }

    [Fact]
    public void Resting_WithHpFullButManaLow_IsRestingMa()
    {
        (TimeAnalysisTracker t, Clock c) = Make();
        t.NotePlayerState(false, PlayerPosition.Resting, hp: 100, maxHp: 100, ma: 30, maxMa: 100);
        c.Advance(4);

        Assert.Equal(TimeSpan.FromSeconds(4), t.Snapshot().RestingMa);
    }

    // ----- movement window ---------------------------------------------

    [Fact]
    public void RoomChange_CountsAsMoving_WithinTheWindow()
    {
        (TimeAnalysisTracker t, Clock c) = Make();
        t.NoteRoomChanged();
        c.Advance(2); // < MovementWindow (3s)

        Assert.Equal(TimeSpan.FromSeconds(2), t.Snapshot().Moving);
    }

    [Fact]
    public void MovementTail_SettlesToWaiting_AfterTheWindow()
    {
        (TimeAnalysisTracker t, Clock c) = Make();
        t.NoteRoomChanged();
        c.Advance(10); // one hop, then a long idle

        TimeAnalysisStats s = t.Snapshot();
        Assert.Equal(TimeAnalysisTracker.MovementWindow, s.Moving); // capped at the window
        Assert.Equal(TimeSpan.FromSeconds(10) - TimeAnalysisTracker.MovementWindow, s.Waiting);
        Assert.Equal(TimeSpan.FromSeconds(10), s.TimeOn);
    }

    [Fact]
    public void SuccessiveHops_AccumulateMoving()
    {
        (TimeAnalysisTracker t, Clock c) = Make();
        t.NoteRoomChanged();        // t=0
        c.Advance(2); t.NoteRoomChanged(); // t=2, re-arms the window
        c.Advance(2); t.NoteRoomChanged(); // t=4, re-arms again
        c.Advance(6);               // t=10, then idle

        TimeAnalysisStats s = t.Snapshot();
        // 0→4 travelling, then 4+3 window tail = moving to t=7; 7→10 waiting.
        Assert.Equal(TimeSpan.FromSeconds(7), s.Moving);
        Assert.Equal(TimeSpan.FromSeconds(3), s.Waiting);
    }

    [Fact]
    public void Combat_OutranksMovement()
    {
        (TimeAnalysisTracker t, Clock c) = Make();
        t.NoteRoomChanged();
        t.NotePlayerState(true, PlayerPosition.Standing, 100, 100, 100, 100); // in combat
        c.Advance(2);

        TimeAnalysisStats s = t.Snapshot();
        Assert.Equal(TimeSpan.FromSeconds(2), s.Attacking);
        Assert.Equal(TimeSpan.Zero, s.Moving);
    }

    // ----- affliction overlays -----------------------------------------

    [Fact]
    public void Blinded_OverlapsAttacking_NotPartOfThePartition()
    {
        (TimeAnalysisTracker t, Clock c) = Make();
        Combat(t, true);
        t.NoteAfflictions(blinded: true, poisoned: false);
        c.Advance(10);

        TimeAnalysisStats s = t.Snapshot();
        Assert.Equal(TimeSpan.FromSeconds(10), s.Attacking); // overlay didn't steal combat time
        Assert.Equal(TimeSpan.FromSeconds(10), s.Blinded);   // concurrent
    }

    [Fact]
    public void Poison_AccruesOnlyWhileActive()
    {
        (TimeAnalysisTracker t, Clock c) = Make();
        t.NoteAfflictions(false, poisoned: true);
        c.Advance(5);
        t.NoteAfflictions(false, poisoned: false);
        c.Advance(5);

        TimeAnalysisStats s = t.Snapshot();
        Assert.Equal(TimeSpan.FromSeconds(5), s.Poisoned);
        Assert.Equal(TimeSpan.FromSeconds(10), s.TimeOn);
        Assert.Equal(TimeSpan.FromSeconds(10), s.Waiting); // poison is an overlay, not an activity
    }

    // ----- invariants ---------------------------------------------------

    [Fact]
    public void ActivityBuckets_AlwaysSumToTimeOn()
    {
        (TimeAnalysisTracker t, Clock c) = Make();
        c.Advance(3);                                   // waiting
        Combat(t, true);  c.Advance(7);                 // attacking
        Combat(t, false); t.NoteRoomChanged(); c.Advance(2); // moving
        t.NotePlayerState(false, PlayerPosition.Resting, 10, 100, 100, 100); c.Advance(9); // resting hp
        t.NoteAfflictions(true, true); c.Advance(4);    // afflicted, still resting hp underneath

        TimeAnalysisStats s = t.Snapshot();
        TimeSpan sum = s.Waiting + s.Moving + s.Attacking + s.RestingHp + s.RestingMa;
        Assert.Equal(s.TimeOn, sum);
    }

    [Fact]
    public void Percentages_AreFractionsOfTimeOn()
    {
        (TimeAnalysisTracker t, Clock c) = Make();
        Combat(t, true);
        c.Advance(5);
        Combat(t, false);
        c.Advance(5);

        TimeAnalysisStats s = t.Snapshot();
        Assert.Equal(50d, s.AttackingPercent, 3);
        Assert.Equal(50d, s.WaitingPercent, 3);
    }

    // ----- reset / change ----------------------------------------------

    [Fact]
    public void Reset_ZeroesEverything_AndRestartsTheClock()
    {
        (TimeAnalysisTracker t, Clock c) = Make();
        Combat(t, true);
        t.NoteAfflictions(true, true);
        c.Advance(20);

        t.Reset();
        c.Advance(5);

        TimeAnalysisStats s = t.Snapshot();
        Assert.Equal(TimeSpan.FromSeconds(5), s.TimeOn);
        Assert.Equal(TimeSpan.FromSeconds(5), s.Waiting); // reset cleared combat/affliction inputs
        Assert.Equal(TimeSpan.Zero, s.Attacking);
        Assert.Equal(TimeSpan.Zero, s.Blinded);
        Assert.Equal(TimeSpan.Zero, s.Poisoned);
    }

    [Fact]
    public void Changed_FiresOnInput()
    {
        (TimeAnalysisTracker t, _) = Make();
        int fired = 0;
        t.Changed += () => fired++;
        Combat(t, true);
        t.NoteRoomChanged();
        t.NoteAfflictions(true, false);
        Assert.True(fired >= 3);
    }
}
