using System;
using System.Collections.Generic;
using System.IO;
using MudPlay.Game.Calculators;
using MudPlay.Game.Combat;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Program Log → "Log session statistics": the Session Statistics window's figures
// go to their own file every few minutes while in the game, and once on leaving.
public sealed class SessionStatsLogTests
{
    private sealed class Harness
    {
        public LogDiagnosticState Diagnostics { get; } = new();
        public List<string> Blocks { get; } = new();
        public List<string> Reasons { get; } = new();
        public bool InGame { get; set; } = true;
        public int Opened { get; private set; }
        public int Closed { get; private set; }
        public bool FailWrites { get; set; }
        public SessionStatsLog Log { get; }
        // The log's interval timer.
        public ManualTimers Timers { get; } = new();

        public Harness()
        {
            Log = new SessionStatsLog(Diagnostics, () => InGame,
                (_, why) => { Reasons.Add(why); return "block: " + why; },
                log: null, schedule: Timers.Arm,
                open: () =>
                {
                    Opened++;
                    return (line =>
                    {
                        if (FailWrites) throw new IOException("disk full");
                        Blocks.Add(line);
                    }, () => Closed++, "/logs/x-session-stats.log");
                });
        }
    }

    [Fact]
    public void Off_WritesNothing_AndOpensNoFile()
    {
        Harness h = new();
        h.Timers.RunAll();
        h.Log.NoteInGameChanged(false);

        Assert.Empty(h.Blocks);
        Assert.Equal(0, h.Opened);
        Assert.Equal("off", h.Log.Summary);
    }

    [Fact]
    public void On_WritesABlockEachInterval_ToOneFile()
    {
        Harness h = new();
        h.Diagnostics.LogSessionStatistics = true;
        h.Timers.RunAll();
        h.Timers.RunAll();

        // One when switched on mid-session, then one per interval.
        Assert.Equal(new[] { "logging switched on", "every 5 min", "every 5 min" }, h.Reasons);
        Assert.Equal(1, h.Opened);
        Assert.Contains("x-session-stats.log", h.Log.Summary);
        Assert.DoesNotContain("/logs/", h.Log.Summary);     // the folder names the user
    }

    // A client at a login screen or the board's menu has nothing new to record.
    [Fact]
    public void OutOfTheGame_NothingIsWritten()
    {
        Harness h = new() { InGame = false };
        h.Diagnostics.LogSessionStatistics = true;
        h.Timers.RunAll();

        Assert.Empty(h.Blocks);
        Assert.Equal(0, h.Opened);                           // no empty file left behind
    }

    // The stretch since the last block isn't lost when the session ends.
    [Fact]
    public void LeavingTheGame_WritesOneLastBlock()
    {
        Harness h = new();
        h.Diagnostics.LogSessionStatistics = true;
        h.InGame = false;
        h.Log.NoteInGameChanged(false);

        Assert.Equal("left the game", h.Reasons[^1]);
        int written = h.Blocks.Count;
        h.Timers.RunAll();
        Assert.Equal(written, h.Blocks.Count);
    }

    [Fact]
    public void TheInterval_IsNamedInTheBlock_AndHeldToItsRange()
    {
        Harness h = new();
        h.Diagnostics.SessionStatisticsMinutes = 30;
        h.Diagnostics.LogSessionStatistics = true;
        h.Timers.RunAll();
        Assert.Equal("every 30 min", h.Reasons[^1]);

        h.Diagnostics.SessionStatisticsMinutes = 0;
        Assert.Equal(LogDiagnosticState.MinSessionStatisticsMinutes, h.Diagnostics.SessionStatisticsMinutes);
        h.Diagnostics.SessionStatisticsMinutes = 100000;
        Assert.Equal(LogDiagnosticState.MaxSessionStatisticsMinutes, h.Diagnostics.SessionStatisticsMinutes);
    }

    [Fact]
    public void SwitchingOff_ClosesTheFile_AndOnAgainStartsANewOne()
    {
        Harness h = new();
        h.Diagnostics.LogSessionStatistics = true;
        h.Diagnostics.LogSessionStatistics = false;
        Assert.Equal(1, h.Closed);
        h.Timers.RunAll();
        Assert.Single(h.Blocks);

        h.Diagnostics.LogSessionStatistics = true;
        Assert.Equal(2, h.Opened);
    }

    // A write that fails stops the log for the session instead of retrying forever.
    [Fact]
    public void AFailedWrite_StopsTheLog()
    {
        Harness h = new() { FailWrites = true };
        h.Diagnostics.LogSessionStatistics = true;
        h.FailWrites = false;
        h.Timers.RunAll();

        Assert.Empty(h.Blocks);
        Assert.Contains("couldn't be written", h.Log.Summary);
    }

    // ----- The block itself -------------------------------------------

    private static string Block(CombatSessionStats? combat = null)
    {
        CombatSessionStats c = combat ?? new CombatSessionStats(
            Hits: 30, Crits: 10, Backstabs: 2, Misses: 10, BackstabFails: 2,
            HitMinDamage: 10, HitMaxDamage: 45, HitTotalDamage: 900,
            CritMinDamage: 60, CritMaxDamage: 120, CritTotalDamage: 900,
            BackstabMinDamage: 200, BackstabMaxDamage: 300, BackstabTotalDamage: 500,
            MobHits: 5, MobMisses: 10, Dodges: 5,
            HitTakenMinDamage: 3, HitTakenMaxDamage: 22, HitTakenTotalDamage: 60,
            RoundsWithDamage: 12, RoundMinDamage: 40, RoundMaxDamage: 310, RoundTotalDamage: 1800,
            ProcHits: 0, ProcMinDamage: 0, ProcMaxDamage: 0, ProcTotalDamage: 0,
            SpellHits: 3, SpellMinDamage: 20, SpellMaxDamage: 40, SpellTotalDamage: 90,
            Spells: new[] { new SpellCombatStat("lightning bolt", Landed: 3, Misses: 1, MinDamage: 20, MaxDamage: 40, TotalDamage: 90) });
        var time = new TimeAnalysisStats(
            TimeOn: TimeSpan.FromMinutes(90), Waiting: TimeSpan.FromMinutes(5), Moving: TimeSpan.FromMinutes(40),
            Attacking: TimeSpan.FromMinutes(30), RestingHp: TimeSpan.FromMinutes(10), RestingMa: TimeSpan.FromMinutes(5),
            Blinded: TimeSpan.Zero, Poisoned: TimeSpan.FromSeconds(75), Diseased: TimeSpan.Zero,
            Confused: TimeSpan.Zero, Held: TimeSpan.Zero);
        var activity = new SessionActivityStats(
            TimeOnline: TimeSpan.FromHours(1.5), MonstersKilled: 150, ExperienceEarned: 1_500_000,
            CurrencyCollected: 12_345, CoinsCollected: 400, CurrencyStashed: 0, CoinsStashed: 0,
            RateKills: 150, RateExperience: 1_500_000, RateCurrency: 12_345, RateCoins: 400,
            CurrencyDeposited: 5_000, ItemsCollected: 7, ItemsSold: 2, ItemsStashed: 1,
            SneakEntries: 20, SneakHeld: 15, DisarmAttempts: 0, TrapsDisarmed: 0,
            Steps: 200, StepTime: TimeSpan.FromSeconds(300));
        return SessionStatsLogFormatter.Format(
            new DateTimeOffset(2026, 10, 8, 21, 35, 0, TimeSpan.Zero), "Cleric — Board:Realm", "every 5 min",
            c, time, activity, (default(TimeToLevelEstimator.Result), null), level: 0, runicName: null,
            new SessionStatsLogFormatter.Laps("circuit", Running: true, Completed: 4,
                Last: TimeSpan.FromSeconds(605), Average: TimeSpan.FromSeconds(590), Current: TimeSpan.FromSeconds(42)));
    }

    [Fact]
    public void TheBlock_IsHeadedByWhenWhoAndWhy()
    {
        string first = Block().Split('\n')[0].TrimEnd();
        Assert.Equal("===== Session statistics · 2026-10-08 21:35:00 · Cleric — Board:Realm · every 5 min =====", first);
    }

    // The window's three sections, under the names the window uses.
    [Fact]
    public void TheBlock_CarriesTheWindowsThreeSections()
    {
        string block = Block();
        Assert.Contains("  Player Statistics", block);
        Assert.Contains("  Time Analysis", block);
        Assert.Contains("  Session Statistics", block);

        Assert.Matches(@"Hit\s+×30\s+10–45\s+30\s+80%", block);          // 40 of 50 swings landed
        Assert.Matches(@"Miss\s+×10\s+20%", block);
        Assert.Matches(@"Crit\s+×10\s+60–120\s+90\s+25%", block);
        Assert.Matches(@"lightning bolt\s+×4\s+20–40\s+30\s+75%", block);
        Assert.Matches(@"Hit by\s+×5\s+3–22\s+12\s+25%", block);
        Assert.DoesNotContain("Proc", block);                             // none fired

        Assert.Matches(@"Duration\s+01:30:00", block);
        Assert.Matches(@"Poisoned\s+00:01:15", block);
        Assert.Matches(@"Sneak\s+75% \(15 of 20 rooms\)", block);
        Assert.Matches(@"Disarm Trap\s+—", block);
        Assert.Matches(@"Walk Latency\s+1\.50s over 200 steps", block);
        Assert.Matches(@"Loop laps\s+circuit", block);
        Assert.Matches(@"Last lap\s+00:10:05", block);

        Assert.Matches(@"Kills\s+150", block);
        Assert.Matches(@"Kills / hour\s+100\.0", block);
        Assert.Matches(@"Experience\s+1,500,000", block);
        Assert.Matches(@"Exp / hour\s+1,000,000", block);
        Assert.Matches(@"Exp needed\s+—", block);                         // level not read yet
        Assert.Matches(@"Items collected\s+7", block);
    }

    // Nothing swung yet: counts read zero and the averages and rates stay blank.
    [Fact]
    public void AnEmptySession_ShowsZeroCounts_AndNoMadeUpRates()
    {
        string block = Block(new CombatSessionStats(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<SpellCombatStat>()));
        Assert.Matches(@"Hit\s+×0\s+—\r?\n", block);
        Assert.DoesNotContain("NaN", block);
    }
}
