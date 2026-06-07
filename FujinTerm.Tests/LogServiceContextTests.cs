using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.0a sub-F — <see cref="LogEntry.Context"/> + the
/// <see cref="LogService.Log(LogSeverity, string, string, string?)"/>
/// overload's propagation path. Pins the field shape so a future
/// refactor can't silently drop the payload and break the LogPane's
/// double-click-to-copy + Phase 9 sub-G fix-dialog flow.
/// </summary>
public sealed class LogServiceContextTests
{
    [Fact]
    public void LogEntry_ContextDefaultsToNull()
    {
        LogEntry e = new(DateTimeOffset.Now, LogSeverity.Info, "src", "msg");
        Assert.Null(e.Context);
    }

    [Fact]
    public void LogEntry_ContextRoundTripsThroughCtor()
    {
        LogEntry e = new(DateTimeOffset.Now, LogSeverity.Warn,
            "RoomClassifier",
            "WARN unknown entity 'foozle'",
            "Also here: foozle, a giant rat, Bob");
        Assert.Equal("Also here: foozle, a giant rat, Bob", e.Context);
    }

    [Fact]
    public void LogService_LogWithContext_PropagatesToSubscriber()
    {
        LogService log = new();
        LogEntry? observed = null;
        log.EntryAdded += e => observed = e;

        log.Log(LogSeverity.Warn, "RoomClassifier",
            "unknown entity 'foozle'",
            context: "Also here: foozle, a giant rat, Bob");

        Assert.NotNull(observed);
        Assert.Equal("Also here: foozle, a giant rat, Bob", observed!.Value.Context);
        Assert.Equal(LogSeverity.Warn, observed.Value.Severity);
        Assert.Equal("RoomClassifier", observed.Value.Source);
    }

    [Fact]
    public void LogService_Warn_ContextOverload_Propagates()
    {
        // The Warn convenience overload is what Phase 9 RoomClassifier
        // will actually call. Pin it as a distinct path so the overload
        // resolution doesn't silently regress to the no-context Warn
        // (which would compile fine but drop the payload).
        LogService log = new();
        LogEntry? observed = null;
        log.EntryAdded += e => observed = e;

        log.Warn("RoomClassifier", "unknown 'foozle'", "raw line goes here");

        Assert.NotNull(observed);
        Assert.Equal("raw line goes here", observed!.Value.Context);
    }

    [Fact]
    public void LogService_Warn_NoContextOverload_LeavesContextNull()
    {
        LogService log = new();
        LogEntry? observed = null;
        log.EntryAdded += e => observed = e;

        log.Warn("Telnet", "connection refused");

        Assert.NotNull(observed);
        Assert.Null(observed!.Value.Context);
    }

    [Fact]
    public void RegisterDetailHandler_EntryAware_InvokesWithPayload()
    {
        LogService log = new();
        LogEntry? observed = null;
        log.RegisterDetailHandler("RoomClassifier",
            (LogEntry e) => observed = e);

        LogEntry trigger = new(DateTimeOffset.Now, LogSeverity.Warn,
            "RoomClassifier", "unknown 'foozle'", "Also here: foozle.");
        bool fired = log.TryInvokeDetailHandler(trigger);

        Assert.True(fired);
        Assert.NotNull(observed);
        Assert.Equal("Also here: foozle.", observed!.Value.Context);
        Assert.Equal("unknown 'foozle'", observed.Value.Message);
    }

    [Fact]
    public void EntryAware_And_NoArg_Handlers_CoexistOnSameSource()
    {
        // Two registration styles, same source. The LogPane fires both
        // — neither implementation should clobber the other's slot.
        LogService log = new();
        bool noArgFired = false;
        bool entryFired = false;
        log.RegisterDetailHandler("X", () => noArgFired = true);
        log.RegisterDetailHandler("X", (LogEntry _) => entryFired = true);

        LogEntry trigger = new(DateTimeOffset.Now, LogSeverity.Info, "X", "");
        log.TryInvokeDetailHandler("X");
        log.TryInvokeDetailHandler(trigger);

        Assert.True(noArgFired);
        Assert.True(entryFired);
    }

    [Fact]
    public void TryInvokeDetailHandler_Entry_NoHandlerRegistered_ReturnsFalse()
    {
        LogService log = new();
        LogEntry trigger = new(DateTimeOffset.Now, LogSeverity.Info, "Unknown", "");
        Assert.False(log.TryInvokeDetailHandler(trigger));
    }

    [Fact]
    public void LogService_Snapshot_PreservesContext()
    {
        LogService log = new();
        log.Warn("RoomClassifier", "unknown 'foozle'", "ctx-A");
        log.Info("Telnet", "connected");
        log.Warn("RoomClassifier", "unknown 'bar'", "ctx-B");

        LogEntry[] snap = log.Snapshot();
        Assert.Equal(3, snap.Length);
        Assert.Equal("ctx-A", snap[0].Context);
        Assert.Null(snap[1].Context);
        Assert.Equal("ctx-B", snap[2].Context);
    }
}
