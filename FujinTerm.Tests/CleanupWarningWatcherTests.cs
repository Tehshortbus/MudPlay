using System.Text;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class CleanupWarningWatcherTests
{
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    [Fact]
    public void Matches_StandardWarning()
    {
        CleanupWarningWatcher w = new();
        int hits = 0;
        w.WarningObserved += _ => hits++;
        w.Append(Ascii("Sorry to interrupt here, but the server will be shutting down in 2 minutes for the nightly 'auto-cleanup' process. Please finish up and log off... thank you!"));

        Assert.Equal(1, hits);
        Assert.NotNull(w.Latest);
        Assert.Equal(2, w.Latest!.Value.MinutesRemaining);
    }

    [Fact]
    public void Matches_WhenLineWrappedBetweenShuttingAndDown()
    {
        // The BBS hard-wraps the warning at its column width, so the wire
        // stream splits the phrase: "...will be shutting\r\ndown in 19
        // minutes...". A literal-space regex misses this.
        CleanupWarningWatcher w = new();
        int hits = 0;
        w.WarningObserved += _ => hits++;
        w.Append(Ascii(
            "Sorry to interrupt here, but the server will be shutting\r\n" +
            "down in 19 minutes for the nightly \"auto-cleanup\"\r\n" +
            "process.  Please finish up and log off... thank you!"));

        Assert.Equal(1, hits);
        Assert.NotNull(w.Latest);
        Assert.Equal(19, w.Latest!.Value.MinutesRemaining);
    }

    [Fact]
    public void Matches_OneMinuteSingular()
    {
        CleanupWarningWatcher w = new();
        w.Append(Ascii("server will be shutting down in 1 minute"));

        Assert.NotNull(w.Latest);
        Assert.Equal(1, w.Latest!.Value.MinutesRemaining);
    }

    [Fact]
    public void CaseInsensitive_Matches()
    {
        CleanupWarningWatcher w = new();
        w.Append(Ascii("SHUTTING DOWN IN 5 MINUTES"));
        Assert.NotNull(w.Latest);
        Assert.Equal(5, w.Latest!.Value.MinutesRemaining);
    }

    [Fact]
    public void LastWarningWins_Override()
    {
        CleanupWarningWatcher w = new();
        w.Append(Ascii("shutting down in 5 minutes"));
        Assert.Equal(5, w.Latest!.Value.MinutesRemaining);

        w.Append(Ascii("\r\nshutting down in 2 minutes"));
        Assert.Equal(2, w.Latest!.Value.MinutesRemaining);

        w.Append(Ascii("\r\nshutting down in 1 minute"));
        Assert.Equal(1, w.Latest!.Value.MinutesRemaining);
    }

    [Fact]
    public void Reset_ClearsState()
    {
        CleanupWarningWatcher w = new();
        w.Append(Ascii("shutting down in 3 minutes"));
        Assert.NotNull(w.Latest);
        w.Reset();
        Assert.Null(w.Latest);
    }

    [Fact]
    public void CsiSequences_StrippedBeforeMatching()
    {
        CleanupWarningWatcher w = new();
        w.Append(Ascii("\x1b[1;33mserver will be shutting down in 4 minutes\x1b[0m"));
        Assert.NotNull(w.Latest);
        Assert.Equal(4, w.Latest!.Value.MinutesRemaining);
    }

    [Fact]
    public void UnrelatedText_NoMatch()
    {
        CleanupWarningWatcher w = new();
        w.Append(Ascii("You hit the goblin for 10 damage. The room shuts down in your face."));
        Assert.Null(w.Latest);
    }

    [Fact]
    public void EstimatedShutdownAt_AddsMinutes()
    {
        CleanupWarningWatcher w = new();
        DateTimeOffset before = DateTimeOffset.Now;
        w.Append(Ascii("shutting down in 2 minutes"));
        DateTimeOffset after = DateTimeOffset.Now;

        CleanupWarning warning = w.Latest!.Value;
        Assert.InRange(warning.ObservedAt, before, after);
        Assert.Equal(warning.ObservedAt.AddMinutes(2), warning.EstimatedShutdownAt);
    }

    [Fact]
    public void ChunkedFeed_StillMatches()
    {
        CleanupWarningWatcher w = new();
        // Split the matching substring across two Append calls.
        w.Append(Ascii("server will be shutti"));
        w.Append(Ascii("ng down in 7 minutes\r\n"));

        Assert.NotNull(w.Latest);
        Assert.Equal(7, w.Latest!.Value.MinutesRemaining);
    }
}
