using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins <see cref="CrashReporter.Build"/> — the deterministic Markdown half of
/// the crash reporter. The Desktop-write path and the CLR channel hooks are
/// side-effecting and smoke-tested via <c>dotnet run</c>; these exercise the
/// document formatting, which is where the fiddly logic (inner-exception walk,
/// null-fault fallback) lives.
/// </summary>
public sealed class CrashReporterTests
{
    [Fact]
    public void Build_IncludesFaultHeaderSourceAndException()
    {
        string md = CrashReporter.Build(new InvalidOperationException("boom"), "UI run loop");

        Assert.Contains("# FujinTerm crash report", md);
        Assert.Contains("- **Source**: UI run loop", md);
        Assert.Contains("System.InvalidOperationException", md);
        Assert.Contains("boom", md);
    }

    [Fact]
    public void Build_WalksInnerExceptionChain()
    {
        Exception ex = new InvalidOperationException(
            "outer", new ArgumentNullException("param", "inner cause"));

        string md = CrashReporter.Build(ex, "AppDomain.UnhandledException");

        Assert.Contains("System.InvalidOperationException", md);
        Assert.Contains("System.ArgumentNullException", md);
        Assert.Contains("Caused by:", md);

        // Outer fault is documented before its cause.
        Assert.True(md.IndexOf("outer", StringComparison.Ordinal)
                  < md.IndexOf("inner cause", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_NullException_StillProducesReport()
    {
        string md = CrashReporter.Build(null, "TaskScheduler.UnobservedTaskException");

        Assert.Contains("# FujinTerm crash report", md);
        Assert.Contains("no exception object", md);
    }

    [Fact]
    public void Build_NoStateProvider_NotesStateUnavailable()
    {
        // No provider registered in the test host, so the state dump is absent
        // and the report says so rather than emitting an empty section.
        string md = CrashReporter.Build(new Exception("x"), "UI run loop");
        Assert.Contains("client state unavailable", md);
    }
}
