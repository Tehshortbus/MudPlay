using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Requester-side @roomba sync: decodes each self-contained "@roombadata <blob>"
// chat line on its own and merges its sightings into GhItemLocationStore, gated
// on a REQUEST WINDOW opened by our own outbound `@roomba sync` (NoteSyncRequested).
// The reply only arrives because the sender granted us "Query Roomba", so having
// asked is the whole gate — a line outside the window is ignored. Because every
// line stands alone (no reassembly), a line the game drops costs only its rooms —
// the rest still merge. Ingest, the receiver's ChatRouter handler, is called
// directly except for the Dispose test, which needs the real MessageRouter →
// ChatRouter → EntryClassified path to prove unsubscription.
public sealed class RoombaSyncReceiverTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _scratchBbs = "roomba-sync-recv-test-" + Path.GetRandomFileName();
    private readonly string _gameDataRoot = Path.Combine(
        Path.GetTempPath(), "mudplay-roomba-sync-recv-gamedata-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try
        {
            string folder = AppPaths.BbsFolder(_scratchBbs);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch { /* best-effort */ }
        try { Directory.Delete(_gameDataRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    private (MessageRouter router, ChatRouter chat, GhItemLocationStore locations, GhRoomLabelStore labels) Setup()
    {
        Directory.CreateDirectory(Path.Combine(_gameDataRoot, "alpha"));
        File.WriteAllText(Path.Combine(_gameDataRoot, "alpha", "Items.json"), """
            [ { "Number": 1, "Name": "torch", "ItemType": 0, "Encum": 1 } ]
            """);
        GameDataCache cache = new(_gameDataRoot);
        cache.SwitchSet("alpha");
        ItemNameStore itemNames = new(cache);
        itemNames.OnActiveSetChanged("alpha");

        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);

        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhRoomLabelStore labels = new(profile);
        labels.OnRealmChanged(AppPaths.BbsFolder(_scratchBbs));

        GhItemLocationStore locations = new(itemNames);
        locations.OnRealmChanged(AppPaths.BbsFolder(_scratchBbs));

        return (router, chat, locations, labels);
    }

    private static ChatLine Gangpath(string sender, string msg) => new(ChatChannel.Gangpath, sender, msg);

    // A line on a channel as the game shows it; on telepath-out, our own telepath to them.
    private static void Heard(MessageRouter router, ChatRouter chat, ChatChannel channel, string sender, string msg)
    {
        if (channel == ChatChannel.TelepathOutgoing) ChatLines.Telepath(router, chat, sender, msg);
        else ChatLines.Hear(router, channel, sender, msg);
    }

    private static string SyncLine(string blob) =>
        $"{{{RoombaQueryHandler.SyncResponseToken} {blob}}}";

    private static string OneLine() => GhItemSyncCodec.EncodeLines(new[]
    {
        new GhItemSyncRecord(1, 100, 1, 4, DateTimeOffset.Now),
    }, 120)[0];

    // Two torch sightings in two rooms as two genuinely independent lines (each
    // room encoded on its own), so the tests exercise per-line merge / drop
    // without depending on the packer's line-splitting threshold.
    private static IReadOnlyList<string> TwoRoomLines() =>
        GhItemSyncCodec.EncodeLines(new[] { new GhItemSyncRecord(1, 100, 1, 4, DateTimeOffset.Now) }, 200)
            .Concat(GhItemSyncCodec.EncodeLines(new[] { new GhItemSyncRecord(1, 200, 1, 2, DateTimeOffset.Now) }, 200))
            .ToList();

    [Fact]
    public void Ingest_SingleLine_MergesIntoLocations()
    {
        var (router, chat, locations, labels) = Setup();
        RoombaSyncReceiver receiver = new(chat, locations, labels);
        receiver.NoteSyncRequested();   // we asked → replies are adopted

        ChatLines.Hear(router, Gangpath("Buddy", SyncLine(OneLine())));

        GhItemSighting sighting = Assert.Single(locations.FindSightings("torch"));
        Assert.Equal(100, sighting.Room);
        Assert.Equal(4, sighting.Quantity);
    }

    // The log is taken by telepath or gangpath, the two channels a sync goes by.
    [Theory]
    [InlineData(ChatChannel.TelepathIncoming)]
    [InlineData(ChatChannel.Gangpath)]
    public void Ingest_ByTelepathOrGangpath_Merges(ChatChannel channel)
    {
        var (router, chat, locations, labels) = Setup();
        RoombaSyncReceiver receiver = new(chat, locations, labels);
        receiver.NoteSyncRequested();

        Heard(router, chat, channel, "Buddy", SyncLine(OneLine()));

        Assert.Single(locations.FindSightings("torch"));
    }

    // Said aloud or on the broadcast channel it is not taken, window open or not:
    // an older client still answers a said request that way, and anyone in the room
    // could say such a line.
    [Theory]
    [InlineData(ChatChannel.Local)]
    [InlineData(ChatChannel.Broadcast)]
    [InlineData(ChatChannel.Gossip)]
    [InlineData(ChatChannel.TelepathOutgoing)]
    public void Ingest_OnAnyOtherChannel_IsPassedOver(ChatChannel channel)
    {
        var (router, chat, locations, labels) = Setup();
        RoombaSyncReceiver receiver = new(chat, locations, labels);
        receiver.NoteSyncRequested();

        Heard(router, chat, channel, "Buddy", SyncLine(OneLine()));

        Assert.Empty(locations.FindSightings("torch"));
    }

    [Fact]
    public void Ingest_EachLineMergesIndependently_NoWaiting()
    {
        var (router, chat, locations, labels) = Setup();
        RoombaSyncReceiver receiver = new(chat, locations, labels);
        receiver.NoteSyncRequested();   // we asked → replies are adopted
        IReadOnlyList<string> lines = TwoRoomLines();
        Assert.True(lines.Count >= 2);

        ChatLines.Hear(router, Gangpath("Buddy", SyncLine(lines[0])));
        Assert.Single(locations.FindSightings("torch"));      // first line merged on its own — no waiting

        foreach (string line in lines.Skip(1))
            ChatLines.Hear(router, Gangpath("Buddy", SyncLine(line)));
        Assert.Equal(2, locations.FindSightings("torch").Count);
    }

    [Fact]
    public void Ingest_DroppedLine_SurvivingLineStillMerges()
    {
        var (router, chat, locations, labels) = Setup();
        RoombaSyncReceiver receiver = new(chat, locations, labels);
        receiver.NoteSyncRequested();   // we asked → replies are adopted
        IReadOnlyList<string> lines = TwoRoomLines();
        Assert.True(lines.Count >= 2);

        // Simulate the game's flood-control dropping every line but the last.
        ChatLines.Hear(router, Gangpath("Buddy", SyncLine(lines[^1])));

        // The surviving line still merged its room, rather than being discarded
        // for want of the dropped ones.
        Assert.Single(locations.FindSightings("torch"));
    }

    [Fact]
    public void Ingest_NotRequested_NeverMerges()
    {
        var (router, chat, locations, labels) = Setup();
        // We never sent `@roomba sync` (no NoteSyncRequested), so a stray
        // @roombadata line — even a well-formed one — must not be adopted.
        RoombaSyncReceiver receiver = new(chat, locations, labels);

        ChatLines.Hear(router, Gangpath("Buddy", SyncLine(OneLine())));

        Assert.Empty(locations.FindSightings("torch"));
    }

    [Fact]
    public void Ingest_MalformedPayload_IsDiscardedWithoutThrowing()
    {
        var (router, chat, locations, labels) = Setup();
        RoombaSyncReceiver receiver = new(chat, locations, labels);
        receiver.NoteSyncRequested();

        Exception? ex = Record.Exception(() =>
            ChatLines.Hear(router, Gangpath("Buddy", SyncLine("!!!not-valid-base64url!!!"))));

        Assert.Null(ex);
        Assert.Empty(locations.FindSightings("torch"));
        // The bad line cost only itself: a good one after it still merges.
        ChatLines.Hear(router, Gangpath("Buddy", SyncLine(OneLine())));
        Assert.Single(locations.FindSightings("torch"));
    }

    [Fact]
    public void Ingest_NonMatchingLine_IsIgnored()
    {
        var (router, chat, locations, labels) = Setup();
        RoombaSyncReceiver receiver = new(chat, locations, labels);
        receiver.NoteSyncRequested();

        ChatLines.Hear(router, Gangpath("Buddy", "just chatting about the weather"));

        Assert.Empty(locations.FindSightings("torch"));
    }

    [Fact]
    public void Dispose_UnsubscribesFromChat_RealDispatchPath()
    {
        var (router, chat, locations, labels) = Setup();
        RoombaSyncReceiver receiver = new(chat, locations, labels);
        receiver.NoteSyncRequested();
        IReadOnlyList<string> lines = TwoRoomLines();
        Assert.True(lines.Count >= 2);

        // Drive through the real MessageRouter → ChatRouter path on the SAME
        // router/chat the receiver subscribed to: the first room merges while
        // subscribed, the second doesn't once Dispose has taken the handler off.
        Dispatch(router, $"Buddy gangpaths: {SyncLine(lines[0])}");
        Assert.Single(locations.FindSightings("torch"));

        receiver.Dispose();
        Dispatch(router, $"Buddy gangpaths: {SyncLine(lines[1])}");

        Assert.Single(locations.FindSightings("torch"));
    }

    private static void Dispatch(MessageRouter router, string wire) =>
        router.Dispatch(new LineExtractor.EmittedLine(
            wire, new CellAttributes[wire.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false));
}
