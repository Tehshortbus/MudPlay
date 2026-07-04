using System.Collections.Generic;
using System.Linq;
using FujinTerm.Game;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Issue #11 — the <see cref="RegenDiagnosticsRecorder"/> Debug-channel trace
/// used to capture a realm's real per-tick regen amount / cadence from a live
/// session (Paradigm splits some cycles into uneven thirds, which a single
/// editable interval can't model).
/// </summary>
public sealed class RegenDiagnosticsRecorderTests
{
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan delta) => Now += delta;
        public DateTimeOffset Read() => Now;
    }

    private static (PlayerState state, RegenTracker tracker, FakeClock clock, LogService log)
        Setup(bool debugOn)
    {
        PlayerState state = new();
        FakeClock clock = new();
        RegenTracker tracker = new(state, clock.Read);
        LogService log = new();
        log.Diagnostics = new LogDiagnosticState { DebugDiagnostics = debugOn };
        // Recorder subscribes in its ctor; kept alive by the returned tuple's
        // tracker holding the delegate. Not disposed — the test is short-lived.
        _ = new RegenDiagnosticsRecorder(tracker, state, log);
        return (state, tracker, clock, log);
    }

    private static List<LogEntry> Capture(LogService log)
    {
        List<LogEntry> entries = new();
        log.EntryAdded += entries.Add;
        return entries;
    }

    [Fact]
    public void DebugOff_EmitsNothing()
    {
        var (state, _, clock, log) = Setup(debugOn: false);
        List<LogEntry> entries = Capture(log);

        state.Hp = 100;                           // baseline (no uptick).
        clock.Advance(TimeSpan.FromSeconds(30));
        state.Hp = 104;                           // a real uptick.

        Assert.Empty(entries);
    }

    [Fact]
    public void DebugOn_LogsHpUptick_WithAmountPositionAndTotal()
    {
        var (state, _, clock, log) = Setup(debugOn: true);
        state.MaxHp = 140;
        List<LogEntry> entries = Capture(log);

        state.Hp = 100;                           // baseline (no uptick fired).
        clock.Advance(TimeSpan.FromSeconds(30));
        state.Hp = 104;                           // +4 natural tick.

        LogEntry row = Assert.Single(entries);
        Assert.Equal(LogSeverity.Debug, row.Severity);
        Assert.Equal("Regen", row.Source);
        Assert.Contains("HP +4", row.Message);
        Assert.Contains("[standing]", row.Message);
        Assert.Contains("100→104/140", row.Message);   // prev→cur/max
        Assert.Contains("first", row.Message);               // no prior HP tick yet.
    }

    [Fact]
    public void SecondUptick_ReportsIntervalSinceLastTick()
    {
        var (state, _, clock, log) = Setup(debugOn: true);
        List<LogEntry> entries = Capture(log);

        state.Hp = 100;                           // baseline.
        clock.Advance(TimeSpan.FromSeconds(30));
        state.Hp = 104;                           // first observed tick → "first".
        clock.Advance(TimeSpan.FromSeconds(30));
        state.Hp = 108;                           // second → gap since the first.

        Assert.Equal(2, entries.Count);
        Assert.Contains("first", entries[0].Message);
        Assert.Contains("after 30.0s", entries[1].Message);
    }

    [Fact]
    public void RestingUptick_TaggedResting_SoTheCycleIsIdentifiable()
    {
        var (state, _, clock, log) = Setup(debugOn: true);
        List<LogEntry> entries = Capture(log);

        state.Hp = 100;
        state.Position = PlayerPosition.Resting;
        clock.Advance(TimeSpan.FromSeconds(20));
        state.Hp = 112;                           // a rest tick.

        LogEntry row = Assert.Single(entries);
        Assert.Contains("HP +12", row.Message);
        Assert.Contains("[resting]", row.Message);
    }

    [Fact]
    public void MeditatingManaUptick_TaggedMeditating()
    {
        var (state, _, clock, log) = Setup(debugOn: true);
        List<LogEntry> entries = Capture(log);

        state.Ma = 40;
        state.Position = PlayerPosition.Meditating;
        clock.Advance(TimeSpan.FromSeconds(10));
        state.Ma = 45;

        LogEntry row = Assert.Single(entries);
        Assert.Contains("MP +5", row.Message);
        Assert.Contains("[meditating]", row.Message);
    }

    [Fact]
    public void UnevenAmounts_AreRecordedVerbatim_SoThirdsPatternIsVisible()
    {
        // The whole point of the instrument: a realm that pays 1/1/3 across a
        // 3-tick cycle must show up as the raw deltas, not smoothed away.
        var (state, _, clock, log) = Setup(debugOn: true);
        List<LogEntry> entries = Capture(log);

        state.Hp = 100;                           // baseline.
        clock.Advance(TimeSpan.FromSeconds(30));
        state.Hp = 101;                           // +1
        clock.Advance(TimeSpan.FromSeconds(30));
        state.Hp = 102;                           // +1
        clock.Advance(TimeSpan.FromSeconds(30));
        state.Hp = 105;                           // +3

        int[] deltas = entries
            .Select(e => e.Message)
            .Select(m => m.Contains("+1") ? 1 : m.Contains("+3") ? 3 : 0)
            .ToArray();
        Assert.Equal(new[] { 1, 1, 3 }, deltas);
    }
}
