using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.GameData;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Train;
using Xunit;

namespace MudPlay.Tests;

// The pure planning core behind party auto-train: the status wire codec, the
// member-count quorum, the party funding plan, the multi-trainer itinerary, and the
// coin selection a member uses to cover someone's shortfall.
public sealed class PartyTrainPlanningTests
{
    private static PartyTrainStatus Status(
        PartyTrainReadiness r, int level, int eta = -1, int cls = 1, int levels = 1) =>
        new(r, level, cls, levels, 0, 0, 0, 0, null, eta);

    private static PartyTrainParticipant P(string name, PartyTrainReadiness r, int level,
                                           int eta = -1, bool leader = false) =>
        new(name, leader, Status(r, level, eta));

    // ----- status codec -------------------------------------------------

    [Fact]
    public void Status_RoundTrips_IncludingABankNameWithSpaces()
    {
        PartyTrainStatus s = new(PartyTrainReadiness.Ready, 14, 5, 2, 12_345, 800, 300, 500_000,
                                 "Bank of Godfrey", 1_800, 4_120_331, 4_500_000);
        Assert.True(PartyTrainStatus.TryDecode(s.Encode(), out PartyTrainStatus back));
        Assert.Equal(s, back);
    }

    [Fact]
    public void Status_IgnoresUnknownKeys_ButRejectsAMissingLevel()
    {
        Assert.True(PartyTrainStatus.TryDecode("s=w l=9 future=42 eta=-1", out PartyTrainStatus s));
        Assert.Equal(PartyTrainReadiness.Waiting, s.Readiness);
        Assert.Equal(9, s.Level);
        Assert.False(PartyTrainStatus.TryDecode("s=r eta=10", out _));
        Assert.False(PartyTrainStatus.TryDecode("l=9", out _));
    }

    // ----- quorum -------------------------------------------------------

    [Fact]
    public void Quorum_EveryoneReady_Fires()
    {
        PartyTrainDecision d = PartyTrainQuorum.Decide(
            [P("Lead", PartyTrainReadiness.Ready, 20, leader: true), P("Ann", PartyTrainReadiness.Ready, 19)],
            levelGap: 5, minReady: 2);
        Assert.Equal(PartyTrainVerdict.Fire, d.Verdict);
        Assert.Equal(["Lead", "Ann"], d.Trainees);
    }

    [Fact]
    public void Quorum_EnoughMembersReady_GoesWithoutTheRest()
    {
        PartyTrainDecision d = PartyTrainQuorum.Decide(
            [P("Lead", PartyTrainReadiness.Ready, 20, leader: true),
             P("Ann", PartyTrainReadiness.Ready, 20),
             P("Bob", PartyTrainReadiness.Waiting, 20, eta: 60)],
            levelGap: 5, minReady: 2);
        Assert.Equal(PartyTrainVerdict.Fire, d.Verdict);
        Assert.Equal(["Lead", "Ann"], d.Trainees);
        Assert.Contains("Bob", d.Skipped);
    }

    [Fact]
    public void Quorum_FewerReadyThanTheSetting_Waits()
    {
        PartyTrainDecision d = PartyTrainQuorum.Decide(
            [P("Lead", PartyTrainReadiness.Ready, 20, leader: true),
             P("Ann", PartyTrainReadiness.Ready, 20),
             P("Bob", PartyTrainReadiness.Waiting, 20, eta: 60)],
            levelGap: 5, minReady: 3);
        Assert.Equal(PartyTrainVerdict.Wait, d.Verdict);
        Assert.Equal(["Bob"], d.WaitingOn);
    }

    // A party smaller than the setting would otherwise never go.
    [Fact]
    public void Quorum_EveryoneReady_FiresEvenBelowTheSetting()
    {
        PartyTrainDecision d = PartyTrainQuorum.Decide(
            [P("Lead", PartyTrainReadiness.Ready, 20, leader: true), P("Ann", PartyTrainReadiness.Ready, 20)],
            levelGap: 5, minReady: 4);
        Assert.Equal(PartyTrainVerdict.Fire, d.Verdict);
    }

    // ----- level-up announcements ----------------------------------------------

    [Theory]
    [InlineData("I can now train to level: 2", 2)]
    [InlineData("\"I can now train to level: 14\"", 14)]   // a say body, quotes kept
    [InlineData("i can now train to level: 7.", 7)]
    [InlineData("I can now train to level: 1", 0)]          // nothing below it to train from
    [InlineData("I can now train to level: soon", 0)]
    [InlineData("anyone want to train to level: 5", 0)]
    public void TrainableAnnounce_ReadsTheLevelUpAnnouncersLine(string message, int expected)
    {
        bool ok = PartyTrainCoordinator.TryParseTrainableAnnounce(message, out int level);
        Assert.Equal(expected > 0, ok);
        if (ok) Assert.Equal(expected, level);
    }

    // A reading with no stated level (an @exp reply) keeps the level already held;
    // its (L<n>) hint — the level the exp counts toward, 59 for a 58 with exp banked —
    // is used only when nothing else is known.
    [Theory]
    [InlineData(58, 0, 0, 59, 58)]    // earlier @level reply wins
    [InlineData(0, 58, 0, 59, 58)]    // else the member's @ptrain report
    [InlineData(0, 0, 58, 59, 58)]    // else the recorded level
    [InlineData(0, 0, 0, 59, 59)]     // nothing known: the hint
    [InlineData(0, 0, 0, 0, 0)]
    public void ResolveLevel_HintNeverOutranksAKnownLevel(int prior, int report, int recorded, int hint, int expected) =>
        Assert.Equal(expected, PartyTrainCoordinator.ResolveLevel(prior, report, recorded, hint));

    // The row names the level the reply's figure counts toward: its own "(L60)" tag when
    // it names one past the next level, else the next level.
    [Theory]
    [InlineData(58, 60, 216526237L, "216,526,237 to L60")]
    [InlineData(58, 0, 216526237L, "216,526,237 to L59")]
    [InlineData(58, 58, 216526237L, "216,526,237 to L59")]   // a tag not above their level is ignored
    [InlineData(58, 60, 0L, "L60 reached")]
    [InlineData(58, 0, 0L, "L59 reached")]
    public void LevelReplyText_NamesTheLevelTheFigureCountsToward(int level, int toLevel, long needed, string expected) =>
        Assert.Contains(expected, PartyTrainCoordinator.LevelReplyText(level, toLevel, needed, "?", 0, null));

    // ----- who can be asked -------------------------------------------------

    [Theory]
    [InlineData(null, true)]               // never probed — may be on MudPlay
    [InlineData("MudPlay 3.105.0", true)]
    [InlineData("MudPlay 3.110.2", true)]
    [InlineData("MudPlay 3.105.1+abc1234", true)]
    [InlineData("MudPlay 3.104.2", false)] // predates @ptrain
    [InlineData("MegaMud 1.03u", false)]   // another client
    [InlineData("MudPlay", false)]         // no parseable version
    public void SpeaksPartyTrain_ReadsTheRecordedVersionReply(string? version, bool expected) =>
        Assert.Equal(expected, PartyTrainCoordinator.SpeaksPartyTrain(version));

    // A waiting member that hasn't pushed by its projected ready time + 10 minutes gets
    // one "ready yet?" ask; ready / blocked / off members never do.
    [Fact]
    public void ReadyAsk_DueTenMinutesPastTheProjectedReadyTime()
    {
        DateTimeOffset at = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        PartyTrainStatus waiting = Status(PartyTrainReadiness.Waiting, 20, eta: 1_800);   // ready in 30m
        Assert.False(PartyTrainCoordinator.ReadyAskDue(waiting, at, 0, at.AddMinutes(39)));
        Assert.True(PartyTrainCoordinator.ReadyAskDue(waiting, at, 0, at.AddMinutes(40)));

        // No own estimate → its time to next level at our rate: 6,000 to go at 60,000/hr = 6m.
        PartyTrainStatus noEta = waiting with { EtaSeconds = -1, Exp = 4_000, NextExp = 10_000 };
        Assert.True(PartyTrainCoordinator.ReadyAskDue(noEta, at, 60_000, at.AddMinutes(16)));
        Assert.False(PartyTrainCoordinator.ReadyAskDue(noEta, at, 0, at.AddDays(1)));   // no projection

        Assert.False(PartyTrainCoordinator.ReadyAskDue(
            Status(PartyTrainReadiness.Ready, 20), at, 60_000, at.AddDays(1)));
    }

    // A stale older-MudPlay record (not confirmed today) may have updated since —
    // still worth the one ask. Today's confirmation, or another client, is trusted.
    [Fact]
    public void SpeaksPartyTrain_DistrustsAnOldMudPlayRecordFromAnotherDay()
    {
        DateTime now = new(2026, 9, 24, 18, 0, 0, DateTimeKind.Utc);
        Assert.True(PartyTrainCoordinator.SpeaksPartyTrain("MudPlay 3.74.0", now.AddDays(-2), now));
        Assert.True(PartyTrainCoordinator.SpeaksPartyTrain("MudPlay 3.74.0", null, now));
        Assert.False(PartyTrainCoordinator.SpeaksPartyTrain("MudPlay 3.74.0", now.AddMinutes(-5), now));
        Assert.False(PartyTrainCoordinator.SpeaksPartyTrain("MegaMud 1.03u", now.AddDays(-2), now));
    }

    // An other-client member is re-asked @level only once its projected level-up
    // (needed at OUR rate) plus the buffer has passed — never on a timer.
    [Fact]
    public void LevelReask_OnlyAfterTheProjectedLevelUpPlusBuffer()
    {
        DateTimeOffset read = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        // 60,000 needed at 60,000/hr → due at 1h + the 10m buffer.
        Assert.False(PartyTrainCoordinator.LevelReaskDue(60_000, read, 60_000, read.AddMinutes(69)));
        Assert.True(PartyTrainCoordinator.LevelReaskDue(60_000, read, 60_000, read.AddMinutes(70)));
        // No rate, no "needed", or already able to train → never re-asked.
        Assert.False(PartyTrainCoordinator.LevelReaskDue(60_000, read, 0, read.AddDays(1)));
        Assert.False(PartyTrainCoordinator.LevelReaskDue(null, read, 60_000, read.AddDays(1)));
        Assert.False(PartyTrainCoordinator.LevelReaskDue(0, read, 60_000, read.AddDays(1)));
    }

    // ----- party ceiling --------------------------------------------------

    [Theory]
    [InlineData(true, 0, 5, 10)]    // below 11 with the solo-11 rule: stop at 10
    [InlineData(true, 0, 10, 10)]   // at 10: nothing left to party-train
    [InlineData(true, 0, 11, 0)]    // past 11 the rule no longer applies
    [InlineData(true, 8, 5, 8)]     // a tighter own ceiling wins
    [InlineData(true, 30, 5, 10)]   // a looser own ceiling doesn't lift the rule
    [InlineData(false, 0, 5, 0)]    // rule off, no ceiling
    [InlineData(false, 25, 20, 25)] // rule off, own ceiling only
    public void PartyCeiling_CombinesTheSolo11RuleWithDoNotTrainAbove(
        bool skip11, int doNotTrainAbove, int level, int expected)
    {
        Models.Profile.AutoTrainerSettings s = new() { PartySkipLevel11 = skip11, DoNotTrainAbove = doNotTrainAbove };
        Assert.Equal(expected, Game.TrainerWalkManager.PartyCeiling(s, level));
    }

    // The power-leveler case: a much higher LEADER who isn't levelling is left out of
    // the count, so the ready member still trains and the leader escorts.
    [Fact]
    public void Quorum_PowerLevelingLeader_IsExcluded_AndTheMemberStillTrains()
    {
        PartyTrainDecision d = PartyTrainQuorum.Decide(
            [P("Lead", PartyTrainReadiness.Waiting, 50, eta: 36_000, leader: true),
             P("Ann", PartyTrainReadiness.Ready, 20)],
            levelGap: 5, minReady: 2);
        Assert.Equal(PartyTrainVerdict.Fire, d.Verdict);
        Assert.Equal(["Ann"], d.Trainees);
        Assert.Contains("Lead", d.Skipped);
    }

    [Fact]
    public void Quorum_OffMembersDontCount_AndTheOffStateRoundTrips()
    {
        PartyTrainDecision d = PartyTrainQuorum.Decide(
            [P("Lead", PartyTrainReadiness.Ready, 20, leader: true), P("Ann", PartyTrainReadiness.Off, 20)],
            levelGap: 5, minReady: 2);
        Assert.Equal(PartyTrainVerdict.Fire, d.Verdict);   // Ann opted out, so everyone left is ready
        Assert.True(PartyTrainStatus.TryDecode(Status(PartyTrainReadiness.Off, 20).Encode(), out PartyTrainStatus back));
        Assert.Equal(PartyTrainReadiness.Off, back.Readiness);
    }

    [Fact]
    public void Quorum_BlockedMembersDontCount()
    {
        PartyTrainDecision d = PartyTrainQuorum.Decide(
            [P("Lead", PartyTrainReadiness.Ready, 20, leader: true),
             P("Ann", PartyTrainReadiness.Blocked, 10)],
            levelGap: 5, minReady: 2);
        Assert.Equal(PartyTrainVerdict.Fire, d.Verdict);
        Assert.Equal(["Lead"], d.Trainees);
    }

    [Fact]
    public void Quorum_NobodyReady_IsIdle()
    {
        PartyTrainDecision d = PartyTrainQuorum.Decide(
            [P("Lead", PartyTrainReadiness.Waiting, 20, leader: true), P("Ann", PartyTrainReadiness.Waiting, 20)],
            levelGap: 5, minReady: 2);
        Assert.Equal(PartyTrainVerdict.Idle, d.Verdict);
    }

    // ----- funding ------------------------------------------------------

    private static PartyTrainFunder F(string name, bool trains, long cost, long cash, long spare,
                                      long bank = 0, string? bankName = null) =>
        new(name, trains, cost, cash, spare, bank, bankName);

    [Fact]
    public void Funding_EveryoneCoversThemselves_NeedsNothing()
    {
        PartyTrainFundingOutcome o = PartyTrainFundingPlanner.Plan(
            [F("Lead", true, 500, 900, 400), F("Ann", true, 500, 600, 100)]);
        Assert.Empty(o.Transfers);
        Assert.Empty(o.Withdrawals);
        Assert.Empty(o.Unfunded);
    }

    [Fact]
    public void Funding_PartySpareCoversEveryone_SharesWithoutABankStop()
    {
        PartyTrainFundingOutcome o = PartyTrainFundingPlanner.Plan(
            [F("Lead", true, 500, 2_000, 1_500), F("Ann", true, 500, 100, 0)]);
        Assert.Null(o.BankName);
        Assert.Equal([new PartyCoinTransfer("Lead", "Ann", 400)], o.Transfers);
        Assert.Empty(o.Unfunded);
    }

    [Fact]
    public void Funding_SpareFallsShort_StopsAtTheBankThatCoversTheMost()
    {
        PartyTrainFundingOutcome o = PartyTrainFundingPlanner.Plan(
            [F("Lead", true, 500, 600, 100),
             F("Ann", true, 1_000, 0, 0, bank: 5_000, bankName: "Bank of Godfrey"),
             F("Bob", true, 300, 0, 0, bank: 5_000, bankName: "Silvermere Bank")]);
        Assert.Equal("Bank of Godfrey", o.BankName);
        Assert.Equal([new PartyBankWithdrawal("Ann", 1_000)], o.Withdrawals);
        // Bob's money is at another branch — the leader's 100 spare is all he gets.
        Assert.Equal([new PartyCoinTransfer("Lead", "Bob", 100)], o.Transfers);
        Assert.Equal(["Bob"], o.Unfunded);
    }

    // ----- itinerary ----------------------------------------------------

    private static TrainerShop T(int number, int room, int min, int max, int cls = 0) =>
        new(number, $"Trainer {number}", 1, room, $"Room {room}", min, max, cls, 100);

    private static int? Dist(RoomKey a, RoomKey b) => Math.Abs(a.Room - b.Room);

    [Fact]
    public void Itinerary_OneBand_OneStop_WithTheLeaderTrainingThere()
    {
        TrainerShop low = T(1, 10, 1, 10);
        IReadOnlyList<PartyTrainStop> stops = PartyTrainItineraryPlanner.Plan(
            [low],
            [new PartyTrainee("Ann", 5, 1, 6), new PartyTrainee("Bob", 6, 1, 7)],
            new PartyTrainee("Lead", 7, 1, 8),
            [], new RoomKey(1, 1), Dist);
        PartyTrainStop stop = Assert.Single(stops);
        Assert.Equal(["Ann", "Bob"], stop.Members);
        Assert.True(stop.LeaderTrains);
    }

    [Fact]
    public void Itinerary_TwoBands_VisitsTheMostAccommodatingTrainerFirst()
    {
        TrainerShop low = T(1, 10, 1, 10);
        TrainerShop high = T(2, 20, 11, 20);
        IReadOnlyList<PartyTrainStop> stops = PartyTrainItineraryPlanner.Plan(
            [low, high],
            [new PartyTrainee("Ann", 12, 1, 13), new PartyTrainee("Bob", 14, 1, 15), new PartyTrainee("Cid", 5, 1, 6)],
            leader: null, [], new RoomKey(1, 1), Dist);
        Assert.Equal(2, stops.Count);
        Assert.Equal(high, stops[0].Trainer);
        Assert.Equal(["Ann", "Bob"], stops[0].Members);
        Assert.Equal(low, stops[1].Trainer);
        Assert.Equal(["Cid"], stops[1].Members);
    }

    [Fact]
    public void Itinerary_LeaderNotServedByTheLastStop_GetsItsOwnFinalStop()
    {
        TrainerShop low = T(1, 10, 1, 10);
        TrainerShop high = T(2, 20, 11, 20);
        IReadOnlyList<PartyTrainStop> stops = PartyTrainItineraryPlanner.Plan(
            [low, high],
            [new PartyTrainee("Ann", 5, 1, 6)],
            new PartyTrainee("Lead", 15, 1, 16),
            [], new RoomKey(1, 1), Dist);
        Assert.Equal(2, stops.Count);
        Assert.False(stops[0].LeaderTrains);
        Assert.Equal(high, stops[1].Trainer);
        Assert.True(stops[1].LeaderTrains);
    }

    [Fact]
    public void Itinerary_AMemberCrossingBands_ChainsAcrossTrainers()
    {
        TrainerShop low = T(1, 10, 1, 10);
        TrainerShop high = T(2, 20, 11, 20);
        IReadOnlyList<PartyTrainStop> stops = PartyTrainItineraryPlanner.Plan(
            [low, high],
            [new PartyTrainee("Ann", 9, 1, 12)],
            leader: null, [], new RoomKey(1, 1), Dist);
        Assert.Equal([low, high], stops.Select(s => s.Trainer));
    }

    [Fact]
    public void Itinerary_DisabledAndClassRestrictedTrainersAreSkipped()
    {
        TrainerShop disabled = T(1, 10, 1, 10);
        TrainerShop wrongClass = T(2, 11, 1, 10, cls: 9);
        TrainerShop ok = T(3, 30, 1, 10);
        IReadOnlyList<PartyTrainStop> stops = PartyTrainItineraryPlanner.Plan(
            [disabled, wrongClass, ok],
            [new PartyTrainee("Ann", 5, 1, 6)],
            leader: null, [disabled.RowKey], new RoomKey(1, 1), Dist);
        Assert.Equal(ok, Assert.Single(stops).Trainer);
    }

    // ----- coin cover ---------------------------------------------------

    [Fact]
    public void PlanCover_UsesTheFewestCoins_WhenTheyLineUp()
    {
        CurrencyHoldings h = new(Copper: 100, Silver: 0, Gold: 5, Platinum: 0, Runic: 0, TotalCopperValue: 600);
        (IReadOnlyList<(string Currency, long Count)> coins, long given) = h.PlanCover(250, capCopper: 600);
        Assert.Equal(250, given);
        Assert.Equal([("gold", 2L), ("copper", 50L)], coins);
    }

    [Fact]
    public void PlanCover_OverpaysWithOneBigCoin_WhenThatsAllThereIs()
    {
        CurrencyHoldings h = new(0, 0, 0, Platinum: 3, 0, TotalCopperValue: 30_000);
        (IReadOnlyList<(string Currency, long Count)> coins, long given) = h.PlanCover(50, capCopper: 20_000);
        Assert.Equal(10_000, given);
        Assert.Equal([("platinum", 1L)], coins);
    }

    [Fact]
    public void PlanCover_StaysInsideTheCap_EvenIfThatLeavesAShortfall()
    {
        CurrencyHoldings h = new(0, 0, 0, Platinum: 3, 0, TotalCopperValue: 30_000);
        (IReadOnlyList<(string Currency, long Count)> coins, long given) = h.PlanCover(50, capCopper: 5_000);
        Assert.Equal(0, given);
        Assert.Empty(coins);
    }
}
