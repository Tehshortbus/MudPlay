using System.IO;
using System.Text;
using FujinTerm.Game.Combat;
using FujinTerm.Game.Inventory;
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

    // ----- PR 10.5 recovery flow (graph-backed) -----------------------

    /// <summary>
    /// Two-room graph (1/1 Town Gates ↔ 1/3 North Square) wired to a real
    /// ProfileService + RoomTracker + DeathRecoveryManager, with the wire
    /// sender captured into <see cref="Sent"/>. Exercises the re-entry →
    /// grab → pickup → recovered flow end to end.
    /// </summary>
    private sealed class GraphHarness : IDisposable
    {
        private readonly string _root;
        public ProfileService Profile { get; } = new();
        public RoomTracker Tracker { get; }
        public DeathLineWatcher Watcher { get; }
        public DeathRecoveryManager Recovery { get; }
        public List<string> Sent { get; } = new();
        public InventorySnapshot Snapshot { get; set; } = InventorySnapshot.Empty;

        private const string GraphJson = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Town Gates",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "1/3", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "North Square",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "1/1", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;

        public GraphHarness()
        {
            _root = Path.Combine(Path.GetTempPath(), "fujinterm-deathrec-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_root, "alpha"));
            File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
            GameDataCache cache = new(_root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");

            MessageRouter router = new();
            DefaultPatterns.Seed(router);
            LogService log = new();
            Tracker = new RoomTracker(graph);
            Tracker.AttachInventorySnapshot(() => Snapshot);
            Watcher = new DeathLineWatcher(router, log);
            Recovery = new DeathRecoveryManager(Watcher, Profile, Tracker, log);
            Recovery.SetWireSender(b => Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
            Recovery.AttachInventorySnapshot(() => Snapshot);

            Profile.LoadBlank();
            Tracker.Hydrate(Profile.Current!);
        }

        private static RoomObservation Obs(string name, params Direction[] exits)
            => new(name, new HashSet<Direction>(exits));

        /// <summary>Confirm at Town Gates (1/1) — both initial landing and re-entry.</summary>
        public void EnterGates() => Tracker.NoteRoomObserved(Obs("Town Gates", Direction.N));

        public DeathRecord Latest => Profile.Current!.DeathHistory![^1];

        public void Dispose()
        {
            Recovery.Dispose();
            Watcher.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static InventorySnapshot SnapWith(EquippedItem[] worn, string[] carried)
        => new(CurrencyHoldings.Empty, EncumbranceReading.Empty, worn, carried, DateTimeOffset.UtcNow);

    [Fact]
    public void ReEntry_AutoRecover_GrabsPileAndGoesPartial_ThenPickupsRecover()
    {
        using GraphHarness h = new();
        h.EnterGates();
        h.Snapshot = SnapWith(
            new[] { new EquippedItem("rusty dagger", "Weapon Hand") },
            new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteDeath(2, "You now have 2 lives remaining.");   // tracker → PendingRespawn

        h.Sent.Clear();
        h.EnterGates();   // walk back into the death room

        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
        Assert.Contains("get rusty dagger", h.Sent);
        Assert.Contains("get torch", h.Sent);

        h.Recovery.FeedTestLine("You pick up rusty dagger.");
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);   // one item left
        h.Recovery.FeedTestLine("You pick up torch.");
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void ReEntry_NoAutoRecover_GoesPartialWithoutGrabbing_ObservedPickupsStillRecover()
    {
        using GraphHarness h = new();
        h.EnterGates();
        h.Snapshot = SnapWith(Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Tracker.NoteDeath(2, "You now have 2 lives remaining.");

        h.Sent.Clear();
        h.EnterGates();

        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
        Assert.Empty(h.Sent);   // no auto-grab without the toggle

        h.Recovery.FeedTestLine("You pick up torch.");
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void AutoEquip_ReequipsWornItemAsItIsRecovered()
    {
        using GraphHarness h = new();
        h.EnterGates();
        h.Snapshot = SnapWith(
            new[] { new EquippedItem("plate mail", "Torso") },
            Array.Empty<string>());
        h.Recovery.AutoRecover = true;
        h.Recovery.AutoEquip = true;
        h.Tracker.NoteDeath(2, "You now have 2 lives remaining.");

        h.Sent.Clear();
        h.EnterGates();
        Assert.Contains("get plate mail", h.Sent);

        h.Recovery.FeedTestLine("You pick up plate mail.");
        Assert.Contains("wear plate mail", h.Sent);   // armor → wear
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void ReEntry_KnownEmptyDeathpile_RecoveredImmediately()
    {
        using GraphHarness h = new();
        h.EnterGates();
        h.Snapshot = InventorySnapshot.Empty;   // nothing worn / carried
        h.Tracker.NoteDeath(2, "You now have 2 lives remaining.");

        h.EnterGates();

        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void RecoverNow_InRoom_GrabsEvenWithAutoRecoverOff()
    {
        using GraphHarness h = new();
        h.EnterGates();
        h.Snapshot = SnapWith(Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Tracker.NoteDeath(2, "You now have 2 lives remaining.");
        h.EnterGates();   // auto re-entry: Partial, no grab (AutoRecover off)

        h.Sent.Clear();
        Assert.True(h.Recovery.RecoverNow(h.Latest));

        Assert.Contains("get torch", h.Sent);
    }
}
