using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Phase-1 log-overhaul framework: the generation-gated Debug / Combat
// channels, their guard flags, and the Global-tier persistence DTO. Info /
// Warn / Error stay always-on; Debug / Combat no-op unless the matching
// diagnostic toggle is set on the wired LogDiagnosticState.
public sealed class LogDiagnosticsTests
{
    [Fact]
    public void Debug_NoOp_WhenNoDiagnosticsWired()
    {
        LogService log = new();
        log.Debug("Engine", "trace");
        Assert.Equal(0, log.Count);
        Assert.False(log.IsDebugEnabled);
    }

    [Fact]
    public void Combat_NoOp_WhenNoDiagnosticsWired()
    {
        LogService log = new();
        log.Combat("Combat", "decision");
        Assert.Equal(0, log.Count);
        Assert.False(log.IsCombatEnabled);
    }

    [Fact]
    public void Debug_NoOp_WhenToggleOff()
    {
        LogService log = new() { Diagnostics = new LogDiagnosticState() };
        log.Debug("Engine", "trace");
        Assert.Equal(0, log.Count);
    }

    [Fact]
    public void Debug_Records_WhenToggleOn()
    {
        LogDiagnosticState diag = new() { DebugDiagnostics = true };
        LogService log = new() { Diagnostics = diag };

        Assert.True(log.IsDebugEnabled);
        log.Debug("Engine", "trace");

        Assert.Equal(1, log.Count);
        Assert.Equal(LogSeverity.Debug, log.Snapshot()[^1].Severity);
    }

    [Fact]
    public void Combat_Records_WhenToggleOn()
    {
        LogDiagnosticState diag = new() { CombatDiagnostics = true };
        LogService log = new() { Diagnostics = diag };

        Assert.True(log.IsCombatEnabled);
        log.Combat("Combat", "decision");

        Assert.Equal(1, log.Count);
        Assert.Equal(LogSeverity.Combat, log.Snapshot()[^1].Severity);
    }

    [Fact]
    public void DebugAndCombat_GateIndependently()
    {
        LogDiagnosticState diag = new() { DebugDiagnostics = true };
        LogService log = new() { Diagnostics = diag };

        log.Debug("Engine", "trace");   // on
        log.Combat("Combat", "skip");   // off
        Assert.Equal(1, log.Count);
        Assert.Equal(LogSeverity.Debug, log.Snapshot()[^1].Severity);
    }

    [Fact]
    public void GuardFlags_TrackLiveToggle()
    {
        LogDiagnosticState diag = new();
        LogService log = new() { Diagnostics = diag };

        Assert.False(log.IsDebugEnabled);
        diag.DebugDiagnostics = true;
        Assert.True(log.IsDebugEnabled);
        diag.DebugDiagnostics = false;
        Assert.False(log.IsDebugEnabled);
    }

    [Fact]
    public void InfoWarnError_AlwaysRecord_RegardlessOfDiagnostics()
    {
        LogService log = new() { Diagnostics = new LogDiagnosticState() };
        log.Info("A", "i");
        log.Warn("B", "w");
        log.Error("C", "e");
        Assert.Equal(3, log.Count);
    }

    [Fact]
    public void LogDiagnosticState_FiresChanged_OnEachFlag()
    {
        LogDiagnosticState diag = new();
        int fired = 0;
        diag.Changed += () => fired++;

        diag.DebugDiagnostics = true;
        diag.CombatDiagnostics = true;
        diag.DebugDiagnostics = true;   // no-op: same value

        Assert.Equal(2, fired);
    }

    [Fact]
    public void AutoCollectLogs_DefaultsOff_AndFiresChanged()
    {
        LogDiagnosticState diag = new();
        Assert.False(diag.AutoCollectLogs);

        int fired = 0;
        diag.Changed += () => fired++;

        diag.AutoCollectLogs = true;
        diag.AutoCollectLogs = true;    // no-op: same value
        diag.AutoCollectLogs = false;

        Assert.Equal(2, fired);
    }

    [Fact]
    public void HopTiming_DefaultsOff_AndFiresChanged()
    {
        LogDiagnosticState diag = new();
        Assert.False(diag.HopTiming);

        int fired = 0;
        diag.Changed += () => fired++;

        diag.HopTiming = true;
        diag.HopTiming = true;    // no-op: same value
        diag.HopTiming = false;

        Assert.Equal(2, fired);
    }

    [Fact]
    public void LogDiagnosticsSettings_Defaults()
    {
        // Debug + Combat default ON so a fresh Program Log already
        // carries the decision trail a bug report needs; the heavier on-disk /
        // hop-timing traces stay off.
        LogDiagnosticsSettings dto = new();
        Assert.True(dto.Debug);
        Assert.True(dto.Combat);
        Assert.False(dto.AutoCollect);
        Assert.False(dto.HopTiming);
    }
}
