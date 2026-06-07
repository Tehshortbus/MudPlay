using FujinTerm.Game.Combat;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.0d — <see cref="DeathLineWatcher"/> matches the canonical
/// "You have been slain by..." line and emits
/// <see cref="DeathLineWatcher.PlayerDied"/>.
/// </summary>
public sealed class DeathLineWatcherTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public DeathLineWatcher Watcher { get; }
        public List<PlayerDiedEvent> Events { get; } = new();
        public List<LogEntry> LogEntries { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Watcher = new DeathLineWatcher(Router, Log);
            Watcher.PlayerDied += Events.Add;
            Log.EntryAdded += LogEntries.Add;
        }

        public void Feed(string line)
        {
            LineExtractor.EmittedLine emitted = new(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false);
            Router.Dispatch(emitted);
        }

        public void Dispose() => Watcher.Dispose();
    }

    [Fact]
    public void CanonicalLine_FiresPlayerDied()
    {
        using Harness h = new();
        h.Feed("You have been slain by a giant rat.");
        Assert.Single(h.Events);
        Assert.Equal("a giant rat", h.Events[0].Killer);
    }

    [Fact]
    public void TrailingBang_AlsoMatches()
    {
        using Harness h = new();
        h.Feed("You have been slain by Necron the Lich!");
        Assert.Single(h.Events);
        Assert.Equal("Necron the Lich", h.Events[0].Killer);
    }

    [Fact]
    public void NonMatchingLines_NoFire()
    {
        using Harness h = new();
        h.Feed("Bob has been slain by a giant rat.");      // party member death — different pattern
        h.Feed("You have been slain.");                    // no killer — doesn't match
        h.Feed("The giant rat bites you for 99 damage!");  // mob hit, not death
        Assert.Empty(h.Events);
    }

    [Fact]
    public void EmitsWarnLogRowWithKiller()
    {
        using Harness h = new();
        h.Feed("You have been slain by a giant rat.");
        LogEntry warn = h.LogEntries.Find(e =>
            e.Source == DeathLineWatcher.LogCategory &&
            e.Severity == LogSeverity.Warn);
        Assert.NotEqual(default, warn);
        Assert.Contains("a giant rat", warn.Message);
    }

    [Fact]
    public void MultipleDeaths_FireSeparately()
    {
        // Reincarnate + die again — watcher just observes; no debouncing.
        using Harness h = new();
        h.Feed("You have been slain by a giant rat.");
        h.Feed("You have been slain by a goblin.");
        Assert.Equal(2, h.Events.Count);
        Assert.Equal("a giant rat", h.Events[0].Killer);
        Assert.Equal("a goblin",    h.Events[1].Killer);
    }

    [Fact]
    public void Dispose_StopsFurtherEmits()
    {
        Harness h = new();
        h.Feed("You have been slain by a giant rat.");
        Assert.Single(h.Events);
        h.Dispose();
        // After disposal the router is still live; feed another line
        // and verify no new event fires.
        LineExtractor.EmittedLine emitted = new(
            "You have been slain by a goblin.", Array.Empty<CellAttributes>(),
            DateTimeOffset.UtcNow, IsPromptLine: false);
        h.Router.Dispatch(emitted);
        Assert.Single(h.Events);
    }
}
