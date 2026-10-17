using MudPlay.Game;
using Xunit;

namespace MudPlay.Tests;

public sealed class RegenTrackerTests
{
    // Test-controllable clock that the tracker reads via the
    // RegenTracker
    // constructor's clock hook. Tests advance it manually so the
    // cycle's interval-vs-elapsed comparison sees realistic gaps.
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan delta) => Now += delta;
        public DateTimeOffset Read() => Now;
    }

    private static (PlayerState state, RegenTracker tracker, FakeClock clock) Setup()
    {
        PlayerState state = new();
        FakeClock clock = new();
        RegenTracker tracker = new(state, clock.Read);
        return (state, tracker, clock);
    }

    [Fact]
    public void FirstHpObservation_OnlySetsBaseline_NoSampleFired()
    {
        var (state, tracker, _) = Setup();
        int fires = 0;
        tracker.HpTickObserved += _ => fires++;

        state.Hp = 100;

        Assert.Equal(0, fires);
        Assert.False(tracker.HpNatural.IsActive);   // not anchored until first uptick.
        tracker.Dispose();
    }

    [Fact]
    public void HpUptick_AnchorsAndSamplesHpNatural()
    {
        var (state, tracker, clock) = Setup();
        state.Hp = 100;                            // baseline
        clock.Advance(TimeSpan.FromSeconds(30));   // a natural tick later…
        state.Hp = 105;                            // …+5 HP

        Assert.Equal(clock.Now, tracker.HpNatural.Anchor);   // the gain is its tick
        Assert.False(tracker.HpRest.IsActive);     // not resting → bonus cycle stays off.
        tracker.Dispose();
    }

    [Fact]
    public void HpUptick_WhileResting_ClaimsHpRest()
    {
        var (state, tracker, clock) = Setup();
        state.Hp = 100;
        state.Position = PlayerPosition.Resting;   // anchors HpRest at now.
        clock.Advance(TimeSpan.FromSeconds(21));   // one rest cycle later.
        state.Hp = 108;

        Assert.Equal(clock.Now, tracker.HpRest.Anchor);
        Assert.False(tracker.HpNatural.IsActive);
        tracker.Dispose();
    }

    [Fact]
    public void LeavingResting_StopsHpRestCycle()
    {
        var (state, tracker, _) = Setup();
        state.Position = PlayerPosition.Resting;
        Assert.True(tracker.HpRest.IsActive);

        state.Position = PlayerPosition.Standing;
        Assert.False(tracker.HpRest.IsActive);
        tracker.Dispose();
    }

    [Fact]
    public void StandingUpBeforeRestInterval_GrantsNoRestTick()
    {
        // Server rule: rest only credits a tick if the player stayed for the
        // full 21 s interval. Standing up at 15 s cancels outright; the next
        // HP uptick gets claimed by HpNatural instead.
        var (state, tracker, clock) = Setup();
        state.Hp = 100;                            // baseline.
        state.Position = PlayerPosition.Resting;   // anchors HpRest at T0.
        clock.Advance(TimeSpan.FromSeconds(15));
        state.Position = PlayerPosition.Standing;  // bailed before 21 s.
        clock.Advance(TimeSpan.FromSeconds(15));   // T0 + 30 s total.
        state.Hp = 102;                            // uptick lands.

        Assert.False(tracker.HpRest.IsActive);
        Assert.Equal(clock.Now, tracker.HpNatural.Anchor);   // natural claimed it.
        tracker.Dispose();
    }

    [Fact]
    public void HpDecrease_NotASample()
    {
        var (state, tracker, clock) = Setup();
        state.Hp = 100;
        clock.Advance(TimeSpan.FromSeconds(30));
        state.Hp = 80;                             // took damage.

        Assert.False(tracker.HpNatural.IsActive);
        tracker.Dispose();
    }

    [Fact]
    public void RecordArtifact_DropsNextHpIncreaseWithinGraceWindow()
    {
        var (state, tracker, clock) = Setup();
        state.Hp = 100;
        clock.Advance(TimeSpan.FromSeconds(30));
        tracker.RecordArtifact();
        clock.Advance(TimeSpan.FromSeconds(1));    // still inside the 3 s grace window.
        state.Hp = 130;                            // looks like a heal — should be dropped.

        Assert.False(tracker.HpNatural.IsActive);
        tracker.Dispose();
    }

    [Fact]
    public void RecordArtifact_AfterGraceWindowExpires_SampleAccepted()
    {
        var (state, tracker, clock) = Setup();
        state.Hp = 100;
        clock.Advance(TimeSpan.FromSeconds(30));
        tracker.RecordArtifact();
        clock.Advance(TimeSpan.FromSeconds(10));   // well past the 3 s window.
        state.Hp = 103;

        Assert.Equal(clock.Now, tracker.HpNatural.Anchor);
        tracker.Dispose();
    }

    [Fact]
    public void MaUptick_WhileMeditating_ClaimsMpMedi()
    {
        var (state, tracker, clock) = Setup();
        state.Ma = 50;
        state.Position = PlayerPosition.Meditating;
        clock.Advance(TimeSpan.FromSeconds(15));   // one medi cycle.
        state.Ma = 55;

        Assert.Equal(clock.Now, tracker.MpMedi.Anchor);
        tracker.Dispose();
    }

    [Fact]
    public void MaUptick_StandingIdling_ClaimsMpNatural()
    {
        var (state, tracker, clock) = Setup();
        state.Ma = 50;
        clock.Advance(TimeSpan.FromSeconds(30));
        state.Ma = 51;

        Assert.Equal(clock.Now, tracker.MpNatural.Anchor);
        Assert.False(tracker.MpMedi.IsActive);
        tracker.Dispose();
    }

    [Fact]
    public void TimeToNextHpNaturalTick_NullBeforeFirstObservation()
    {
        var (_, tracker, _) = Setup();
        Assert.Null(tracker.GetTimeToNextHpNaturalTick());
        tracker.Dispose();
    }

    [Fact]
    public void TimeToNextHpRestTick_NullWhenNotResting()
    {
        var (_, tracker, _) = Setup();
        Assert.Null(tracker.GetTimeToNextHpRestTick());
        tracker.Dispose();
    }

    [Fact]
    public void TimeToNextHpRestTick_NonNullOncePositionEntersResting()
    {
        var (state, tracker, _) = Setup();
        state.Position = PlayerPosition.Resting;
        Assert.NotNull(tracker.GetTimeToNextHpRestTick());
        tracker.Dispose();
    }

    [Fact]
    public void HpUptick_AnchorsMpNaturalToSameInstant()
    {
        // Natural HP + natural MA fire on the same server pulse — a max-MA
        // character still gets a live MA countdown from observed HP ticks.
        var (state, tracker, _) = Setup();
        state.Hp = 100;
        state.Hp = 105;

        Assert.True(tracker.HpNatural.IsActive);
        Assert.True(tracker.MpNatural.IsActive);
        Assert.Equal(tracker.HpNatural.Anchor, tracker.MpNatural.Anchor);
        tracker.Dispose();
    }

    [Fact]
    public void MaUptick_AnchorsHpNaturalToSameInstant()
    {
        // Symmetric: a max-HP character still gets a live HP countdown from
        // observed MA ticks.
        var (state, tracker, _) = Setup();
        state.Ma = 50;
        state.Ma = 51;

        Assert.True(tracker.MpNatural.IsActive);
        Assert.True(tracker.HpNatural.IsActive);
        Assert.Equal(tracker.MpNatural.Anchor, tracker.HpNatural.Anchor);
        tracker.Dispose();
    }

    [Fact]
    public void DefaultRealm_NaturalCadenceIsStockThirtySeconds()
    {
        var (state, tracker, _) = Setup();
        state.Hp = 100;                            // baseline
        state.Hp = 103;                            // uptick anchors HpNatural at T0.

        Assert.Equal(TimeSpan.FromSeconds(30), tracker.GetTimeToNextHpNaturalTick());
        tracker.Dispose();
    }

    [Fact]
    public void SetRealmParaMud_ReseedsNaturalCadenceToTenSecondGrid()
    {
        // Paradigm splits the natural cycle into thirds on a 10 s grid, so the
        // status-bar countdown must count down from 10 s, not the stock 30 s.
        var (state, tracker, _) = Setup();
        tracker.SetRealm(RealmType.ParaMud);
        state.Hp = 100;                            // baseline
        state.Hp = 103;                            // uptick anchors HpNatural at T0.

        Assert.Equal(TimeSpan.FromSeconds(10), tracker.GetTimeToNextHpNaturalTick());
        tracker.Dispose();
    }

    [Fact]
    public void SetRealmParaMud_RestGainsComeEveryFiveSeconds()
    {
        var (state, tracker, _) = Setup();
        tracker.SetRealm(RealmType.ParaMud);
        state.Position = PlayerPosition.Resting;   // anchors HpRest at T0.

        Assert.Equal(TimeSpan.FromSeconds(5), tracker.GetTimeToNextHpRestTick());
        tracker.Dispose();
    }

    [Fact]
    public void SetRealmParaMud_ManaComesEveryThirtySeconds_AndAnHpGainDoesNotMoveIt()
    {
        var (state, tracker, clock) = Setup();
        tracker.SetRealm(RealmType.ParaMud);
        state.Hp = 100;
        state.Ma = 50;
        state.Ma = 57;                              // the mana pass, at T0
        Assert.Equal(TimeSpan.FromSeconds(30), tracker.GetTimeToNextMpNaturalTick());

        clock.Advance(TimeSpan.FromSeconds(10));
        state.Hp = 101;                             // an HP gain a third of the way in

        Assert.Equal(TimeSpan.FromSeconds(20), tracker.GetTimeToNextMpNaturalTick());
        Assert.Equal(TimeSpan.FromSeconds(10), tracker.GetTimeToNextHpNaturalTick());
        tracker.Dispose();
    }

    // On Paradigm meditating rides the mana grid. A gain while meditating that lands
    // when the mana pass is due is the pass itself: the mana cycle takes it, and
    // with no HP gains of its own lately the HP countdown follows it.
    [Fact]
    public void ParaMud_MeditatingGain_WhenTheManaPassIsDue_IsThePass()
    {
        var (state, tracker, clock) = Setup();
        tracker.SetRealm(RealmType.ParaMud);
        state.Hp = 100;
        state.Ma = 50;
        state.Ma = 57;                              // the mana pass, at T0
        state.Position = PlayerPosition.Meditating;

        clock.Advance(TimeSpan.FromSeconds(30));
        state.Ma = 70;                              // the next pass, while meditating

        Assert.Equal(clock.Now, tracker.MpNatural.Anchor);
        Assert.Equal(clock.Now, tracker.HpNatural.Anchor);
        tracker.Dispose();
    }

    // On Paradigm a rest gain rides the round grid whenever the character lay down:
    // lying down 3.2 s after a round, the first gain is 1.8 s away, not 5.
    [Fact]
    public void ParaMud_RestCycleAnchorsOnTheRoundGrid_NotOnLyingDown()
    {
        var (state, tracker, clock) = Setup();
        tracker.SetRealm(RealmType.ParaMud);
        tracker.NoteRound(clock.Now);
        clock.Advance(TimeSpan.FromSeconds(13.2));  // two rounds and 3.2 s later
        state.Position = PlayerPosition.Resting;

        AssertSeconds(1.8, tracker.GetTimeToNextHpRestTick());
        tracker.Dispose();
    }

    // On Paradigm the standing gain comes two rounds after the last HP gain, a rest
    // gain included (live log 2026-10-04: last rest gain 02:53:41.5, standing gains
    // from 02:53:51.6, while the mana pass sat at :25.9 / :56.0). Three rest gains
    // move it a round off where it was before the rest.
    [Fact]
    public void ParaMud_StandingUp_PlacesTheStandingGainTenSecondsAfterTheLastRestGain()
    {
        var (state, tracker, clock) = Setup();
        tracker.SetRealm(RealmType.ParaMud);
        state.Hp = 100;
        state.Hp = 101;                             // a standing gain, at T0
        clock.Advance(TimeSpan.FromSeconds(2));
        state.Position = PlayerPosition.Resting;
        clock.Advance(TimeSpan.FromSeconds(3));
        state.Hp = 102;                             // rest gains at T0+5, +10, +15
        clock.Advance(TimeSpan.FromSeconds(5));
        state.Hp = 103;
        clock.Advance(TimeSpan.FromSeconds(5));
        state.Hp = 104;
        clock.Advance(TimeSpan.FromSeconds(2));
        state.Position = PlayerPosition.Standing;   // T0+17

        AssertSeconds(8, tracker.GetTimeToNextHpNaturalTick());   // T0+25, not T0+20
        tracker.Dispose();
    }

    // The same log: a mana pass 5 s after a standing HP gain. It is not an HP gain's
    // moment, and the HP countdown the gains have placed stays where it is.
    [Fact]
    public void ParaMud_ManaPassOffTheHpCycle_LeavesTheHpCountdownAlone()
    {
        var (state, tracker, clock) = Setup();
        tracker.SetRealm(RealmType.ParaMud);
        state.Hp = 100;
        state.Ma = 50;
        state.Hp = 101;                             // T0
        clock.Advance(TimeSpan.FromSeconds(10));
        state.Hp = 102;                             // T0+10
        clock.Advance(TimeSpan.FromSeconds(5));
        state.Ma = 54;                              // the mana pass, at T0+15

        AssertSeconds(5, tracker.GetTimeToNextHpNaturalTick());
        tracker.Dispose();
    }

    // At max HP no HP gain is seen, so the mana pass is the only sign of the grid and
    // still starts the HP countdown.
    [Fact]
    public void ParaMud_NoHpGainsSeen_ManaPassStartsTheHpCountdown()
    {
        var (state, tracker, clock) = Setup();
        tracker.SetRealm(RealmType.ParaMud);
        state.Hp = 100;
        state.Ma = 50;
        clock.Advance(TimeSpan.FromSeconds(40));
        state.Ma = 54;

        AssertSeconds(10, tracker.GetTimeToNextHpNaturalTick());
        tracker.Dispose();
    }

    // ----- telling regen from heals and heal-over-time buffs ----------------

    // The level 51 Priest of the 2026-10-04 live log: a standing third of 3, a whole
    // amount of 9, max HP 520.
    private static (PlayerState state, RegenTracker tracker, FakeClock clock, List<string> leftOut) PriestSetup()
    {
        var (state, tracker, clock) = Setup();
        tracker.SetRealm(RealmType.ParaMud);
        HpRegenExpectation expected = HpRegenExpectation.For(RealmType.ParaMud, 51, 67, 0)!;
        tracker.SetHpExpectation(() => expected);
        List<string> leftOut = new();
        tracker.HpGainLeftOut += (gain, why) => leftOut.Add($"+{gain} {why}");
        state.MaxHp = 520;
        state.Hp = 300;
        return (state, tracker, clock, leftOut);
    }

    // A between-round heal lands half a second after the round, inside the round
    // grid's reach: +134 at 14:50:57.350 in the log. Its size gives it away.
    [Fact]
    public void Heal_BiggerThanRegenPays_IsLeftOut()
    {
        var (state, tracker, clock, leftOut) = PriestSetup();
        state.Hp = 303;                             // regen, at T0
        clock.Advance(TimeSpan.FromSeconds(5.4));
        state.Hp = 437;                             // the heal

        AssertSeconds(4.6, tracker.GetTimeToNextHpNaturalTick());
        Assert.Single(leftOut);
        Assert.StartsWith("+134 more than regen pays", leftOut[0]);
        tracker.Dispose();
    }

    // The Priest's buffs tick +1 and +2 every spell round; one in five lands on a
    // combat round. While +3 keeps arriving, those aren't regen.
    [Fact]
    public void GainOfAnotherSize_IsLeftOut_WhileRegenSizedGainsKeepComing()
    {
        var (state, tracker, clock, leftOut) = PriestSetup();
        state.Hp = 303;                             // T0
        clock.Advance(TimeSpan.FromSeconds(5));
        state.Hp = 305;                             // a buff's +2 on the other round

        AssertSeconds(5, tracker.GetTimeToNextHpNaturalTick());
        Assert.Single(leftOut);
        tracker.Dispose();
    }

    // No regen-sized gain for 40 s while other sizes arrive: the expected amounts
    // are off (a stat not read, a buff's percent), and the gains count again.
    [Fact]
    public void GainOfAnotherSize_CountsAgain_OnceRegenSizedGainsStop()
    {
        var (state, tracker, clock, leftOut) = PriestSetup();
        state.Hp = 303;
        clock.Advance(TimeSpan.FromSeconds(40));
        state.Hp = 305;

        AssertSeconds(10, tracker.GetTimeToNextHpNaturalTick());
        Assert.Empty(leftOut);
        tracker.Dispose();
    }

    // The last gain before max HP is cut short: 519 -> 520 is regen whatever its size.
    [Fact]
    public void GainThatFillsHpToMax_IsRegenWhateverItsSize()
    {
        var (state, tracker, clock, leftOut) = PriestSetup();
        state.Hp = 303;
        clock.Advance(TimeSpan.FromSeconds(1));
        state.Hp = 519;                             // damage healed elsewhere; baseline moves
        leftOut.Clear();
        clock.Advance(TimeSpan.FromSeconds(9));
        state.Hp = 520;

        Assert.Empty(leftOut);
        AssertSeconds(10, tracker.GetTimeToNextHpNaturalTick());
        tracker.Dispose();
    }

    // The rolled buff's +3 is the size of the Priest's regen third. It falls between
    // two ticks of a cycle that has taken two gains in a row, and doesn't move it.
    [Fact]
    public void RegenSizedGain_BetweenTheTicksOfATrackedCycle_DoesNotMoveIt()
    {
        var (state, tracker, clock, leftOut) = PriestSetup();
        state.Hp = 303;                             // T0
        clock.Advance(TimeSpan.FromSeconds(10));
        state.Hp = 306;                             // T0+10: two in a row
        clock.Advance(TimeSpan.FromSeconds(5));
        state.Hp = 309;                             // the buff, on the other round

        AssertSeconds(5, tracker.GetTimeToNextHpNaturalTick());
        Assert.Single(leftOut);
        Assert.Contains("between two ticks", leftOut[0]);

        clock.Advance(TimeSpan.FromSeconds(5.07));  // the next regen gain, a moment after the countdown rolled over
        tracker.GetTimeToNextHpNaturalTick();
        state.Hp = 312;
        AssertSeconds(10, tracker.GetTimeToNextHpNaturalTick());
        tracker.Dispose();
    }

    // A lone first gain that was really the buff's tick: the gain 5 s later shows it
    // was misplaced, and the cycle moves at once.
    [Fact]
    public void LoneMisplacedGain_IsCorrectedByTheNextOne()
    {
        var (state, tracker, clock, leftOut) = PriestSetup();
        state.Hp = 303;                             // the buff's +3, taken as the first tick
        clock.Advance(TimeSpan.FromSeconds(5));
        state.Hp = 306;                             // the real regen gain

        AssertSeconds(10, tracker.GetTimeToNextHpNaturalTick());
        Assert.Empty(leftOut);
        tracker.Dispose();
    }

    // Without the amounts (no `stat` read yet) every gain is judged on timing alone.
    [Fact]
    public void NoExpectation_NothingIsLeftOutForItsSize()
    {
        var (state, tracker, clock) = Setup();
        tracker.SetRealm(RealmType.ParaMud);
        int ticks = 0;
        tracker.HpTickObserved += _ => ticks++;
        state.Hp = 300;
        state.Hp = 303;
        clock.Advance(TimeSpan.FromSeconds(10));
        state.Hp = 437;

        Assert.Equal(2, ticks);
        tracker.Dispose();
    }

    // On Paradigm a meditate gain comes every 15 s on a grid the 30 s mana pass sits
    // on (143 timed stretches: every gap between gains was 14-15 s, so the pass never
    // landed apart from a meditate gain). Meditating 22 s after a pass, the next gain
    // is 8 s away, at the pass.
    [Fact]
    public void ParaMud_MeditateCycleAnchorsOnTheManaGrid_NotOnTheCommand()
    {
        var (state, tracker, clock) = Setup();
        tracker.SetRealm(RealmType.ParaMud);
        state.Ma = 50;
        state.Ma = 167;                             // the mana pass, at T0
        clock.Advance(TimeSpan.FromSeconds(22));
        state.Position = PlayerPosition.Meditating;

        AssertSeconds(8, tracker.GetTimeToNextMpMediTick());

        clock.Advance(TimeSpan.FromSeconds(8));
        state.Ma = 313;                             // pass + meditate gain together
        AssertSeconds(15, tracker.GetTimeToNextMpMediTick());
        AssertSeconds(30, tracker.GetTimeToNextMpNaturalTick());

        clock.Advance(TimeSpan.FromSeconds(15));
        state.Ma = 342;                             // the meditate gain between passes
        AssertSeconds(15, tracker.GetTimeToNextMpMediTick());
        AssertSeconds(15, tracker.GetTimeToNextMpNaturalTick());
        tracker.Dispose();
    }

    // No mana gain seen before meditating: the cycle counts from the command until
    // the first gain shows where the grid is. That gain is a meditate grid point but
    // not known to be the pass, and it is the only mana anchor there is.
    [Fact]
    public void ParaMud_FirstMeditateGain_SetsTheMeditateGrid()
    {
        var (state, tracker, clock) = Setup();
        tracker.SetRealm(RealmType.ParaMud);
        state.Ma = 50;
        state.Position = PlayerPosition.Meditating;
        clock.Advance(TimeSpan.FromSeconds(6));
        state.Ma = 79;

        AssertSeconds(15, tracker.GetTimeToNextMpMediTick());
        Assert.True(tracker.MpNatural.IsActive);
        tracker.Dispose();
    }

    // A meditate gain the countdown didn't expect moves the meditate grid and leaves
    // a running mana countdown where the last pass put it.
    [Fact]
    public void ParaMud_OffCountdownMeditateGain_LeavesTheManaCycleAlone()
    {
        var (state, tracker, clock) = Setup();
        tracker.SetRealm(RealmType.ParaMud);
        state.Ma = 50;
        state.Ma = 167;                             // the mana pass, at T0
        clock.Advance(TimeSpan.FromSeconds(2));
        state.Position = PlayerPosition.Meditating;
        clock.Advance(TimeSpan.FromSeconds(8));     // 10 s after the pass
        state.Ma = 196;

        AssertSeconds(15, tracker.GetTimeToNextMpMediTick());
        AssertSeconds(20, tracker.GetTimeToNextMpNaturalTick());
        tracker.Dispose();
    }

    [Fact]
    public void Stock_MeditateCycleCountsFromTheCommand()
    {
        var (state, tracker, clock) = Setup();
        state.Ma = 50;
        state.Ma = 57;                              // the 30 s pass, at T0
        clock.Advance(TimeSpan.FromSeconds(22));
        state.Position = PlayerPosition.Meditating;

        AssertSeconds(15, tracker.GetTimeToNextMpMediTick());
        tracker.Dispose();
    }

    // Stock counts 21 whole game ticks from the command, so the first rest gain comes
    // 21 s after the tick the command landed in: 20.8 s after a command sent 0.2 s
    // into a tick (report stock-20261004-150847: rest seen 15:07:39.601, rounds
    // falling at .70, first rest gain 15:07:59.693).
    [Fact]
    public void Stock_RestCycleCountsFromTheGameTickTheCommandLandedIn()
    {
        var (state, tracker, clock) = Setup();
        tracker.NoteRound(clock.Now);
        clock.Advance(TimeSpan.FromSeconds(13.2));
        state.Position = PlayerPosition.Resting;

        AssertSeconds(20.8, tracker.GetTimeToNextHpRestTick());
        tracker.Dispose();
    }

    // With no round or pass seen lately there is no tick to count from: half a tick
    // back, the middle of where the command can have landed.
    [Fact]
    public void Stock_RestCycle_NoTickPhaseKnown_CountsFromHalfATickBack()
    {
        var (state, tracker, _) = Setup();
        state.Position = PlayerPosition.Resting;

        AssertSeconds(20.5, tracker.GetTimeToNextHpRestTick());
        tracker.Dispose();
    }

    // The same report: the 30 s pass at 15:07:33.716, rest from 15:07:39.601, the
    // rest gain at 15:07:59.693 and the next pass at 15:08:03.711. The rest gain is
    // the rest cycle's and leaves the pass countdown where it was; it used to fall
    // through to the pass and restart that countdown at 30 with 4 s to go.
    [Fact]
    public void Stock_RestGain_IsTheRestCyclesAndLeavesThePassCountdownAlone()
    {
        var (state, tracker, clock) = Setup();
        state.Hp = 24;
        state.Hp = 25;                              // the pass, at T0
        clock.Advance(TimeSpan.FromSeconds(5.885));
        state.Position = PlayerPosition.Resting;
        clock.Advance(TimeSpan.FromSeconds(20.092));

        AssertSeconds(0.023, tracker.GetTimeToNextHpRestTick());
        state.Hp = 28;                              // the rest gain, +3

        AssertSeconds(21, tracker.GetTimeToNextHpRestTick());
        AssertSeconds(4.023, tracker.GetTimeToNextHpNaturalTick());

        clock.Advance(TimeSpan.FromSeconds(4.018));
        state.Hp = 29;                              // the next pass, +1
        AssertSeconds(30, tracker.GetTimeToNextHpNaturalTick());
        AssertSeconds(16.982, tracker.GetTimeToNextHpRestTick());

        // A rest gain that comes just after the countdown rolled over is still the
        // rest cycle's (15:08:41.697 in the report).
        clock.Advance(TimeSpan.FromSeconds(16.99));
        tracker.GetTimeToNextHpRestTick();
        state.Hp = 32;
        AssertSeconds(21, tracker.GetTimeToNextHpRestTick());
        AssertSeconds(13.01, tracker.GetTimeToNextHpNaturalTick());
        tracker.Dispose();
    }

    // Meditate is counted the same way, 15 ticks from the command, and its gain
    // leaves the mana pass countdown alone.
    [Fact]
    public void Stock_MeditateGain_IsTheMeditateCyclesAndLeavesThePassCountdownAlone()
    {
        var (state, tracker, clock) = Setup();
        state.Ma = 10;
        state.Ma = 14;                              // the pass, at T0
        clock.Advance(TimeSpan.FromSeconds(5.9));
        state.Position = PlayerPosition.Meditating;
        clock.Advance(TimeSpan.FromSeconds(14.08));  // the 15th tick after the command
        state.Ma = 18;

        AssertSeconds(15, tracker.GetTimeToNextMpMediTick());
        AssertSeconds(10.02, tracker.GetTimeToNextMpNaturalTick());
        tracker.Dispose();
    }

    // A round seen on the wire slides a coasting cycle onto its grid; one that is
    // further off than an honest drift is left alone.
    [Fact]
    public void RoundSeen_SlidesACoastingCycleOntoTheGrid()
    {
        var (state, tracker, clock) = Setup();
        state.Hp = 100;
        state.Hp = 105;                             // natural anchor at T0
        clock.Advance(TimeSpan.FromSeconds(20.4));

        tracker.NoteRound(clock.Now);               // grid is 0.4 s later than we had it

        AssertSeconds(10, tracker.GetTimeToNextHpNaturalTick());
        tracker.Dispose();
    }

    [Fact]
    public void RoundSeen_FarOffTheCycle_LeavesItAlone()
    {
        var (state, tracker, clock) = Setup();
        state.Hp = 100;
        state.Hp = 105;
        clock.Advance(TimeSpan.FromSeconds(22.3));  // 2.3 s off any 5 s grid through T0

        tracker.NoteRound(clock.Now);

        AssertSeconds(7.7, tracker.GetTimeToNextHpNaturalTick());
        tracker.Dispose();
    }

    // The Grail card of a deck heals as it is dealt: +2 at 02:56:48.492, again
    // 0.19 s later and again 3 s after that, none of them on a round (live log,
    // 2026-10-04). Regen is paid on the round, so those aren't regen.
    [Fact]
    public void GainsBetweenRounds_AreNotRegen()
    {
        var (state, tracker, clock) = Setup();
        tracker.SetRealm(RealmType.ParaMud);
        int ticks = 0;
        tracker.HpTickObserved += _ => ticks++;
        state.Hp = 93;
        tracker.NoteRound(clock.Now);

        clock.Advance(TimeSpan.FromSeconds(2.19)); state.Hp = 95;
        clock.Advance(TimeSpan.FromSeconds(0.19)); state.Hp = 97;
        clock.Advance(TimeSpan.FromSeconds(3.03)); state.Hp = 99;   // 5.41 s after the round
        Assert.Equal(1, ticks);                                      // only the last sits on a round
        clock.Advance(TimeSpan.FromSeconds(4.6));  state.Hp = 100;  // the real gain, on the next round
        Assert.Equal(2, ticks);
        tracker.Dispose();
    }

    // The game makes up a whole second in one jump every couple of minutes. The
    // first gain after it looks a second early; the next one, a whole number of
    // rounds later, shows the grid itself moved.
    [Fact]
    public void GridMovingASecond_IsFollowedOnTheSecondGain()
    {
        var (state, tracker, clock) = Setup();
        tracker.SetRealm(RealmType.ParaMud);
        int ticks = 0;
        tracker.HpTickObserved += _ => ticks++;
        state.Hp = 100;
        tracker.NoteRound(clock.Now);

        clock.Advance(TimeSpan.FromSeconds(9.0));  state.Hp = 101;  // a second early
        Assert.Equal(0, ticks);
        clock.Advance(TimeSpan.FromSeconds(10.05)); state.Hp = 102; // two rounds after it
        Assert.Equal(1, ticks);
        clock.Advance(TimeSpan.FromSeconds(10.05)); state.Hp = 103;
        Assert.Equal(2, ticks);
        tracker.Dispose();
    }

    // The gains of a real Paradigm session (report paradigm-20261004-024314: five
    // minutes of standing, then resting, with a fight at each end), as milliseconds
    // from the first event: R a round seen, H / M an HP / mana change from-to, P a
    // posture (0 standing, 1 resting). Every gain must have been called within a
    // second and a bit by the countdown that was showing just before it.
    private static readonly (int Ms, char Kind, int A, int B)[] ParadigmCapture =
    {
        (0, 'M', 56, 63),
        (10043, 'R', 0, 0),
        (20110, 'R', 0, 0),
        (25143, 'R', 0, 0),
        (30176, 'M', 36, 43),
        (35210, 'R', 0, 0),
        (50335, 'R', 0, 0),
        (60405, 'M', 11, 18),
        (70457, 'H', 71, 72),
        (80514, 'H', 72, 73),
        (90590, 'H', 73, 74),
        (90590, 'M', 18, 25),
        (100674, 'H', 74, 75),
        (110755, 'H', 75, 76),
        (120821, 'H', 76, 77),
        (120821, 'M', 25, 32),
        (130895, 'H', 77, 78),
        (140930, 'H', 78, 79),
        (150002, 'H', 79, 80),
        (150002, 'M', 32, 39),
        (160077, 'H', 80, 81),
        (170173, 'H', 81, 82),
        (180223, 'H', 82, 83),
        (180223, 'M', 39, 46),
        (190307, 'H', 83, 84),
        (195018, 'P', 0, 1),
        (195370, 'H', 84, 85),
        (200383, 'H', 85, 86),
        (205410, 'H', 86, 87),
        (210457, 'H', 87, 91),
        (210457, 'M', 46, 53),
        (215525, 'H', 91, 95),
        (220554, 'H', 95, 99),
        (225591, 'H', 99, 100),
        (230644, 'H', 100, 101),
        (235679, 'H', 101, 102),
        (240741, 'H', 102, 106),
        (240741, 'M', 53, 60),
        (245768, 'H', 106, 110),
        (250789, 'H', 110, 114),
        (255815, 'H', 114, 115),
        (260859, 'H', 115, 116),
        (265897, 'H', 116, 117),
        (270906, 'H', 117, 121),
        (270906, 'M', 60, 67),
        (274957, 'H', 121, 125),
        (280024, 'H', 125, 129),
        (285021, 'H', 129, 130),
        (290071, 'H', 130, 131),
        (295105, 'H', 131, 132),
        (300132, 'H', 132, 136),
        (300132, 'M', 67, 74),
        (305158, 'H', 136, 140),
        (310173, 'H', 140, 144),
        (315203, 'H', 144, 145),
        (320231, 'H', 145, 146),
        (325257, 'H', 146, 147),
        (330309, 'P', 0, 0),
        (330310, 'R', 0, 0),
        (330315, 'H', 147, 148),
        (330315, 'M', 74, 79),
        (335322, 'R', 0, 0),
        (340358, 'R', 0, 0),
        (340362, 'H', 148, 149),
        (344869, 'P', 0, 1),
        (345417, 'H', 149, 150)
    };

    [Fact]
    public void ParadigmCapture_EveryGainLandsWhenTheCountdownSaidItWould()
    {
        var (state, tracker, clock) = Setup();
        tracker.SetRealm(RealmType.ParaMud);
        DateTimeOffset start = clock.Now;
        state.Hp = 71;
        state.Ma = 56;
        bool resting = false, manaSeen = false, hpSeen = false, restSeen = false;
        double worst = 0;

        foreach ((int ms, char kind, int a, int b) in ParadigmCapture)
        {
            clock.Now = start + TimeSpan.FromMilliseconds(ms);
            switch (kind)
            {
                case 'R': tracker.NoteRound(clock.Now); break;
                case 'P':
                    resting = b == 1;
                    state.Position = resting ? PlayerPosition.Resting : PlayerPosition.Standing;
                    restSeen = false;
                    break;
                case 'M':
                    if (manaSeen) worst = Math.Max(worst, Miss(tracker.GetTimeToNextMpNaturalTick(), tracker.MpNatural.Interval));
                    if (state.Ma != a) state.Ma = a;    // a cast spent mana since the last gain
                    state.Ma = b;
                    manaSeen = true;
                    break;
                case 'H':
                    if (resting && restSeen) worst = Math.Max(worst, Miss(tracker.GetTimeToNextHpRestTick(), tracker.HpRest.Interval));
                    else if (!resting && hpSeen) worst = Math.Max(worst, Miss(tracker.GetTimeToNextHpNaturalTick(), tracker.HpNatural.Interval));
                    if (state.Hp != a) state.Hp = a;
                    state.Hp = b;
                    hpSeen = true;
                    if (resting) restSeen = true;
                    break;
            }
        }

        Assert.True(worst <= 1.2, $"a gain came {worst:0.00}s from where the countdown had it");
        tracker.Dispose();
    }

    private static void AssertSeconds(double expected, TimeSpan? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected, actual!.Value.TotalSeconds, 3);
    }

    // How far a gain arriving now is from the tick the countdown was pointing at:
    // the countdown reads either "almost due" or "just passed" (a full interval).
    private static double Miss(TimeSpan? toNext, TimeSpan interval)
    {
        Assert.NotNull(toNext);
        double left = toNext!.Value.TotalSeconds;
        return Math.Min(left, interval.TotalSeconds - left);
    }

    [Fact]
    public void SetRealmBackToStock_RestoresStockCadence()
    {
        var (state, tracker, _) = Setup();
        tracker.SetRealm(RealmType.ParaMud);
        tracker.SetRealm(RealmType.Stock);
        state.Hp = 100;
        state.Hp = 103;

        Assert.Equal(TimeSpan.FromSeconds(30), tracker.GetTimeToNextHpNaturalTick());
        tracker.Dispose();
    }
}
