using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Gmcp;
using MudPlay.Game.Quests;
using MudPlay.Models.Profile;
using MudPlay.Net;
using MudPlay.Services;
using MudPlay.ViewModels.CharacterWorkshop;
using Xunit;

namespace MudPlay.Tests;

// The realm's quest names (Anomaly.Quests.Names) from the wire into the quest store,
// and from there into every place a quest is named.
public sealed partial class GmcpIntegrationTests
{
    private const string QuestNames = "Anomaly.Quests.Names";

    // A quest store over the test's own seed and game-data set, bound to the session
    // as the client binds it.
    private sealed class QuestLayers : IDisposable
    {
        private readonly string _seedPath = Path.Combine(Path.GetTempPath(), "questseed-" + Path.GetRandomFileName() + ".json");
        private readonly string _set = "quest-names-" + Path.GetRandomFileName();
        public QuestStore Store { get; }
        public int Reloads { get; private set; }
        public string OverlayFile => AppPaths.QuestsFile(_set);

        public QuestLayers(Session session, QuestDefinition[]? seed = null, QuestDefinition[]? overlay = null)
        {
            ScratchRoot.Require();
            if (seed is not null) JsonStore.Save(_seedPath, seed.ToList());
            if (overlay is not null) JsonStore.Save(OverlayFile, overlay.ToList());
            Store = new QuestStore(seedPath: _seedPath);
            Store.OnActiveSetChanged(_set);
            Store.Reloaded += () => Reloads++;
            session.Gmcp.BindQuests(Store);
        }

        public void Dispose()
        {
            string folder = AppPaths.GameDataSetDir(_set);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            if (File.Exists(_seedPath)) File.Delete(_seedPath);
        }
    }

    [Fact]
    public void QuestNames_FromTheWire_NameEachStageByTheFlagValueItStartsAt()
    {
        Assert.Contains("Anomaly.Quests 1", GmcpProtocol.Supports);
        Assert.Contains(QuestNames, GmcpIntegration.Packages);
        using Session session = new();
        using QuestLayers layers = new(session);

        session.Replay("15-quest-names");

        QuestStore store = layers.Store;
        Assert.Equal(5, store.ServerNames.Count);
        // Two ranges on one flag: a band takes the name of the range its first value is in.
        Assert.Equal("1st Alignment Quest", store.DisplayName(126, 10, 1));
        Assert.Equal("1st Alignment Quest", store.DisplayName(126, 10, 6));
        Assert.Equal("2nd Alignment Quest", store.DisplayName(126, 20, 7));
        Assert.Equal("2nd Alignment Quest", store.DisplayName(126, 20, 12));
        Assert.Equal("", store.DisplayName(126, 30, 13));
        // A quest that is the whole flag takes its first range.
        Assert.Equal("1st Alignment Quest", store.DisplayName(126, 0, 0));
        // An open-ended range covers every value from its start.
        Assert.Equal("Ormund's Legacy", store.DisplayName(201, 0, 0));
        Assert.Equal("Ormund's Legacy", store.DisplayName(201, 40, 500));
        Assert.Equal("Kotar: The Price of an Open Gate", store.DisplayName(3200, 0, 0));
        Assert.Equal(1, layers.Reloads);
    }

    [Fact]
    public void QuestNames_RankBelowTheUsersAndAboveTheSeeds_AndAreNeverSavedAsEdits()
    {
        using Session session = new();
        using QuestLayers layers = new(session,
            seed: [new(3300, 0, "Paradigm Iron"), new(126, 10, "Seeded Good One"), new(9000, 0, "Seed Only")],
            // A rename, and an edit to a seeded quest's visibility that carries the seed's name with it.
            overlay: [new(201, 0, "My Ormund"), new(126, 10, "Seeded Good One", visible: false)]);
        QuestStore store = layers.Store;
        Assert.Equal("Paradigm Iron", store.DisplayName(3300, 0, 0));

        session.Replay("15-quest-names");

        Assert.Equal("My Ormund", store.DisplayName(201, 0, 0));
        Assert.Equal("The Price of Iron", store.DisplayName(3300, 0, 0));
        Assert.Equal("1st Alignment Quest", store.DisplayName(126, 10, 1));
        Assert.Equal("Seed Only", store.DisplayName(9000, 0, 0));
        Assert.Equal("Ability 4000", QuestTextFormatter.Title(store, new CrawledQuest(4000, 0, 0, [], [])));
        Assert.Equal("Kotar: The Price of an Open Gate", QuestTextFormatter.Title(store, new CrawledQuest(3200, 0, 0, [], [])));

        // @quest: names match and label by the same order; the alignment flags keep their curated label.
        Assert.Equal(3300, QuestNameResolver.Resolve("price of iron", store.NamedQuests()));
        Assert.Equal(3200, QuestNameResolver.Resolve("kotar", store.NamedQuests()));
        Assert.Equal("Kotar: The Price of an Open Gate", QuestNameResolver.Label(3200, store.FlagName(3200, [])));
        Assert.Equal("My Ormund", QuestNameResolver.Label(201, store.FlagName(201, [])));
        Assert.Equal("Good align", QuestNameResolver.Label(126, store.FlagName(126, [new QuestProgress { Flag = 126, Step = 10 }])));

        // The definitions the editor reads and saves are the user's and the seed's alone.
        Assert.Equal("", store.Resolve(3200, 0).Name);
        Assert.Equal("Paradigm Iron", store.Resolve(3300, 0).Name);
        store.Save([store.Resolve(3200, 0), store.Resolve(3300, 0), store.Resolve(201, 0)]);
        string saved = File.ReadAllText(layers.OverlayFile);
        Assert.DoesNotContain("Kotar", saved);
        Assert.DoesNotContain("Price of Iron", saved);
        Assert.Contains("My Ormund", saved);
    }

    // A seeded name the user cleared in the editor (saved blank) gives the flag's own
    // title back, and isn't offered to @quest; the realm's name still ranks above it.
    [Fact]
    public void QuestNames_ASeededNameTheUserCleared_StaysCleared()
    {
        using Session session = new();
        using QuestLayers layers = new(session,
            seed: [new(4000, 0, "Seed Only"), new(3300, 0, "Paradigm Iron")],
            overlay: [new(4000, 0, ""), new(3300, 0, "")]);
        QuestStore store = layers.Store;

        Assert.Equal("Ability 4000", QuestTextFormatter.Title(store, new CrawledQuest(4000, 0, 0, [], [])));
        Assert.Equal("", store.FlagName(4000, []));
        Assert.DoesNotContain(store.NamedQuests(), q => q.Name == "Seed Only");
        Assert.Equal("", store.DisplayName(3300, 0, 0));

        session.Replay("15-quest-names");

        Assert.Equal("The Price of Iron", store.DisplayName(3300, 0, 0));
        Assert.Equal("", store.DisplayName(4000, 0, 0));
    }

    // @quest names a flag by the range the character's flag value is in, its lowest
    // before a value is read; a whole-flag lookup takes the lowest range whatever
    // order the server listed them in.
    [Fact]
    public void QuestNames_AFlagIsNamedByTheRangeTheCharacterIsIn()
    {
        using Session session = new();
        using QuestLayers layers = new(session);
        QuestStore store = layers.Store;
        session.Replay("15-quest-names");

        Assert.Equal("1st Alignment Quest", store.FlagName(126, []));
        Assert.Equal("2nd Alignment Quest", store.FlagName(126, [new QuestProgress { Flag = 126, Step = 20, FlagValue = 9 }]));
        Assert.Equal("1st Alignment Quest", store.FlagName(126, [new QuestProgress { Flag = 126, Step = 30, FlagValue = 40 }]));

        session.Send(QuestNames, new
        {
            quests = new object[]
            {
                new { flag = 4100, from = 6, to = (int?)null, name = "The Smuggler's Return", level = (int?)null },
                new { flag = 4100, from = 1, to = (int?)5, name = "The Lost Shipment", level = (int?)null },
            },
        });
        Assert.Equal("The Lost Shipment", store.DisplayName(4100, 0, 0));
        Assert.Equal("The Smuggler's Return", store.FlagName(4100, [new QuestProgress { Flag = 4100, Step = 0, FlagValue = 8 }]));
    }

    [Fact]
    public void QuestNames_GoWithTheSession_ButNotWithARoomChange()
    {
        using Session session = new();
        using QuestLayers layers = new(session);
        QuestStore store = layers.Store;

        session.Replay("15-quest-names");
        session.Send("Anomaly.Session.Reset", new { scope = "room", room = "1,2" });
        Assert.Equal(5, store.ServerNames.Count);
        session.Send("Anomaly.Session.Reset", new { scope = "all", reason = "character" });
        Assert.Empty(store.ServerNames);
        Assert.Equal("", store.DisplayName(3200, 0, 0));

        session.Replay("15-quest-names");
        session.Send("Anomaly.Session.Reset", new { scope = "package", package = "Anomaly.Quests" });
        Assert.Empty(store.ServerNames);

        session.Replay("15-quest-names");
        session.Gmcp.Disconnected();
        Assert.Empty(store.ServerNames);

        // Another realm (a new boot) starts without them.
        session.Replay("15-quest-names");
        session.Send("Anomaly.Server", new { name = "Other", version = "1", contract = "1.2.0", boot = "0000000000000009", build = "test" });
        Assert.Empty(store.ServerNames);
        Assert.Equal(8, layers.Reloads);
    }

    [Fact]
    public void QuestNames_ARejectedSnapshotDropsTheNames()
    {
        using Session session = new();
        using QuestLayers layers = new(session);
        session.Replay("15-quest-names");
        JsonElement invalid = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Gmcp", "Examples", "invalid", "quest-name-without-range.json"))).RootElement.GetProperty("payload").Clone();

        session.Gmcp.Receive(new(QuestNames, invalid));

        Assert.Empty(layers.Store.ServerNames);
    }

    // No text path reads quest names, so Shadow applies them as Active does; Off ignores them.
    [Theory]
    [InlineData(GmcpMode.Off, 0)]
    [InlineData(GmcpMode.Shadow, 5)]
    [InlineData(GmcpMode.Active, 5)]
    public void QuestNames_ApplyUnlessThePackageIsOff(GmcpMode mode, int names)
    {
        using Session session = new(active: false);
        session.Gmcp.Configure(new Dictionary<string, GmcpMode> { [QuestNames] = mode });
        using QuestLayers layers = new(session, seed: [new(3300, 0, "Paradigm Iron")]);

        session.Replay("15-quest-names");

        Assert.Equal(names, layers.Store.ServerNames.Count);
        Assert.Equal(mode == GmcpMode.Off ? "Paradigm Iron" : "The Price of Iron", layers.Store.DisplayName(3300, 0, 0));
    }

    [Fact]
    public void QuestNames_NameTheAvailabilityAnnouncements_WithoutQuestQuest()
    {
        string root = Path.Combine(Path.GetTempPath(), "mudplay-questnames-" + Guid.NewGuid().ToString("N"));
        string set = Path.Combine(root, "alpha");
        Directory.CreateDirectory(set);
        try
        {
            File.WriteAllText(Path.Combine(set, "TBInfo.json"), JsonSerializer.Serialize(new[]
            {
                "minlevel 5:giveability 3300 1",
                "minlevel 5:giveability 200 1",
                "giveability 126 4:minlevel 10",
                "giveability 126 7:minlevel 20",
            }.Select(a => new Dictionary<string, object> { ["Action"] = a })));
            GameDataCache gameData = new(root);
            gameData.SwitchSet("alpha");
            using Session session = new();
            using QuestLayers layers = new(session);
            PlayerStats stats = new() { Level = 25 };
            using StatParser parser = new(stats);
            ProfileService profile = TestProfiles.Loaded();
            profile.Current!.QuestAlignGood = true;
            using QuestAvailabilityAnnouncer announcer = new(parser, profile, () => 25,
                level => QuestEligibility.Resolve(gameData, layers.Store, stats, profile,
                    (q, _) => QuestTextFormatter.Title(layers.Store, q), level));
            List<string> lines = new();
            announcer.QuestBecameAvailable += name => lines.Add(QuestAvailabilityAnnouncer.AvailableLine(name));

            announcer.AnnounceLoginAvailable();
            Assert.Equal(
                ["[Ability 3300 Quest is Now Available]", "[Good 1 Quest is Now Available]",
                    "[Good 2 Quest is Now Available]", "[Mandos Quest is Now Available]"],
                lines.Order(StringComparer.Ordinal));

            session.Replay("15-quest-names");
            lines.Clear();
            announcer.AnnounceLoginAvailable();
            Assert.Equal(
                ["[1st Alignment Quest is Now Available]", "[2nd Alignment Quest is Now Available]",
                    "[Mandos Quest is Now Available]", "[The Price of Iron Quest is Now Available]"],
                lines.Order(StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
