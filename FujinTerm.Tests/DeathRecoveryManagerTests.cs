using FujinTerm.Game.Combat;
using FujinTerm.Game.Map;
using FujinTerm.Game.Recovery;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.I — <see cref="DeathRecoveryManager"/> observable mirror of
/// the loaded profile's death history. (The <c>@comeback</c> party-
/// pickup flow is a separate concern owned by
/// <see cref="FujinTerm.Game.Remote.PartyComebackManager"/> — see
/// <c>PartyComebackManagerTests</c>.)
/// </summary>
public sealed class DeathRecoveryManagerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public ProfileService Profile { get; } = new();
        public DeathLineWatcher Watcher { get; }
        public DeathRecoveryManager Recovery { get; }

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Watcher = new DeathLineWatcher(Router, Log);
            // Empty graph (no set loaded) → CurrentRoom is null, which is
            // all these history-mirror tests need from the tracker.
            var tracker = new RoomTracker(new RoomGraphManager(new GameDataCache()));
            Recovery = new DeathRecoveryManager(Watcher, Profile, tracker, Log);
        }

        public void FeedSlain(string killer)
        {
            Router.Dispatch(new LineExtractor.EmittedLine(
                $"You have been slain by {killer}.",
                Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));
        }

        public void AppendDeath(int livesRemaining)
        {
            // Simulate DeathDetector → RoomTracker.NoteDeath path by
            // pushing a record directly onto the loaded profile.
            CharacterProfile? p = Profile.Current ?? Profile.LoadBlank();
            p.DeathHistory ??= new List<DeathRecord>();
            p.DeathHistory.Add(new DeathRecord(
                at: DateTimeOffset.UtcNow,
                room: null,
                livesRemaining: livesRemaining,
                messageText: $"You now have {livesRemaining} lives remaining."));
            Recovery.Refresh();
        }

        public void Dispose()
        {
            Recovery.Dispose();
            Watcher.Dispose();
        }
    }

    // ----- DeathLineWatcher → LastKiller ------------------------------

    [Fact]
    public void PlayerDied_PopulatesLastKillerAndTime()
    {
        using Harness h = new();
        Assert.Null(h.Recovery.LastKiller);

        h.FeedSlain("giant rat");

        Assert.Equal("giant rat", h.Recovery.LastKiller);
        Assert.NotNull(h.Recovery.LastDeathAt);
    }

    [Fact]
    public void PlayerDied_MultipleKills_LatestWins()
    {
        using Harness h = new();
        h.FeedSlain("giant rat");
        h.FeedSlain("orc warrior");

        Assert.Equal("orc warrior", h.Recovery.LastKiller);
    }

    // ----- Profile.DeathHistory → LivesRemaining + DeathCount --------

    [Fact]
    public void DeathHistory_ZeroEntries_LivesAndCountStayZero()
    {
        using Harness h = new();
        // No profile loaded → no DeathHistory → both observables stay at 0.
        Assert.Equal(0, h.Recovery.DeathCount);
        Assert.Equal(0, h.Recovery.LivesRemaining);
    }

    [Fact]
    public void AppendDeath_UpdatesLivesAndCount()
    {
        using Harness h = new();
        h.AppendDeath(2);

        Assert.Equal(2, h.Recovery.LivesRemaining);
        Assert.Equal(1, h.Recovery.DeathCount);
    }

    [Fact]
    public void AppendMultipleDeaths_MirrorsLatest()
    {
        using Harness h = new();
        h.AppendDeath(2);
        h.AppendDeath(1);

        Assert.Equal(1, h.Recovery.LivesRemaining);
        Assert.Equal(2, h.Recovery.DeathCount);
    }

    // ----- ProfileLoaded triggers re-sync -----------------------------

    [Fact]
    public void ProfileLoaded_SyncsObservables()
    {
        using Harness h = new();
        CharacterProfile p = h.Profile.LoadBlank();
        p.DeathHistory = new List<DeathRecord>
        {
            new(DateTimeOffset.UtcNow, room: null, livesRemaining: 3, messageText: "x"),
            new(DateTimeOffset.UtcNow, room: null, livesRemaining: 2, messageText: "y"),
        };
        // Trigger ProfileLoaded by re-loading blank — picks up the
        // updated DeathHistory via the resync.
        h.Recovery.Refresh();

        Assert.Equal(2, h.Recovery.LivesRemaining);
        Assert.Equal(2, h.Recovery.DeathCount);
    }
}
