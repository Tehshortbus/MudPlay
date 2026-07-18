using System.Text.Json;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Phase-1 log-overhaul framework: the generation-gated Debug / Combat
/// channels, their guard flags, and the Char-tier persistence DTO. Info /
/// Warn / Error stay always-on; Debug / Combat no-op unless the matching
/// diagnostic toggle is set on the wired <see cref="LogDiagnosticState"/>.
/// </summary>
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
        Assert.Equal(LogSeverity.Debug, log.Latest!.Value.Severity);
    }

    [Fact]
    public void Combat_Records_WhenToggleOn()
    {
        LogDiagnosticState diag = new() { CombatDiagnostics = true };
        LogService log = new() { Diagnostics = diag };

        Assert.True(log.IsCombatEnabled);
        log.Combat("Combat", "decision", context: "raw wire line");

        Assert.Equal(1, log.Count);
        LogEntry e = log.Latest!.Value;
        Assert.Equal(LogSeverity.Combat, e.Severity);
        Assert.Equal("raw wire line", e.Context);
    }

    [Fact]
    public void DebugAndCombat_GateIndependently()
    {
        LogDiagnosticState diag = new() { DebugDiagnostics = true };
        LogService log = new() { Diagnostics = diag };

        log.Debug("Engine", "trace");   // on
        log.Combat("Combat", "skip");   // off
        Assert.Equal(1, log.Count);
        Assert.Equal(LogSeverity.Debug, log.Latest!.Value.Severity);
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
    public void LogDiagnosticsSettings_DefaultsOff()
    {
        LogDiagnosticsSettings dto = new();
        Assert.False(dto.Debug);
        Assert.False(dto.Combat);
        Assert.False(dto.AutoCollect);
        Assert.False(dto.HopTiming);
    }

    [Fact]
    public void LogDiagnosticsSettings_RoundTripsJson()
    {
        LogDiagnosticsSettings original = new() { Debug = true, Combat = true, AutoCollect = true, HopTiming = true };
        string json = JsonSerializer.Serialize(original);
        LogDiagnosticsSettings? round = JsonSerializer.Deserialize<LogDiagnosticsSettings>(json);

        Assert.NotNull(round);
        Assert.True(round!.Debug);
        Assert.True(round.Combat);
        Assert.True(round.AutoCollect);
        Assert.True(round.HopTiming);
    }
}
