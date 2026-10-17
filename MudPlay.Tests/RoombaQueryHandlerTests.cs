using System;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Pins the read-only @roomba handler: replies with ONE consolidated line —
// total quantity + every room GhItemLocationStore currently tracks for a named
// item — gated solely on the per-player QueryItemLocation ("Query Roomba") grant.
public sealed class RoombaQueryHandlerTests : IDisposable
{
    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    // What the remote-command engine put on the wire, through the sender the client binds.
    private readonly List<byte[]> _replies = new();

    private static readonly DateTime Now = new(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _scratchBbs = "roomba-query-test-" + Path.GetRandomFileName();
    private readonly string _gameDataRoot = Path.Combine(
        Path.GetTempPath(), "mudplay-roomba-query-gamedata-" + Path.GetRandomFileName());

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

    private (RemoteCommandManager engine, PlayerDatabase players, GhRoomLabelStore labels, GhItemLocationStore locations)
        Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        RemoteCommandManager engine = new(chat, party, players);
        _routers[engine] = router;
        engine.SetWireSender(_replies.Add);

        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhRoomLabelStore labels = new(profile);
        labels.OnRealmChanged(AppPaths.BbsFolder(_scratchBbs));

        ItemNameStore itemNames = new(new GameDataCache());
        GhItemLocationStore locations = new(itemNames);
        locations.OnRealmChanged(AppPaths.BbsFolder(_scratchBbs));

        _ = new RoombaQueryHandler(engine, locations, labels);

        return (engine, players, labels, locations);
    }

    private static ChatLine Gangpath(string sender, string msg) =>
        new(ChatChannel.Gangpath, sender, msg);

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    private IReadOnlyList<string> Replies() =>
        _replies.Select(b => Encoding.Latin1.GetString(b)).ToList();

    // Decode every "bg {@roombadata <blob>}" gangpath reply line on its own and
    // flatten — mirrors RoombaSyncReceiver, which merges each self-contained line
    // as it arrives (no cross-line reassembly).
    private IReadOnlyList<GhItemSyncRecord> DecodeSyncReplies()
        => Replies()
            .Where(r => r.Contains(RoombaQueryHandler.SyncResponseToken))
            .Select(r =>
            {
                int open = r.IndexOf('{');
                int close = r.LastIndexOf('}');
                return r[(open + 1)..close].Split(' ', 2)[1];
            })
            // Drop the trailing "Sync Complete" sentinel and any label lines — this
            // helper decodes only the item-sighting lines.
            .Where(blob => !blob.Equals(RoombaQueryHandler.SyncCompleteMarker, StringComparison.Ordinal)
                        && !GhItemSyncCodec.IsLabelLine(blob))
            .SelectMany(GhItemSyncCodec.DecodeLine)
            .ToList();

    private (RemoteCommandManager engine, PlayerDatabase players, GhItemLocationStore locations)
        SetupWithItems()
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
        PartyState party = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        RemoteCommandManager engine = new(chat, party, players);
        _routers[engine] = router;
        engine.SetWireSender(_replies.Add);

        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        GhRoomLabelStore labels = new(profile);
        labels.OnRealmChanged(AppPaths.BbsFolder(_scratchBbs));

        GhItemLocationStore locations = new(itemNames);
        locations.OnRealmChanged(AppPaths.BbsFolder(_scratchBbs));

        _ = new RoombaQueryHandler(engine, locations, labels);

        return (engine, players, locations);
    }

    [Fact]
    public void Roomba_Granted_ReportsTotalAndRoom()
    {
        var (engine, players, _, locations) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "long sword" });

        ChatLines.Hear(_routers[engine], Gangpath("Friend", "@roomba long sword"));

        Assert.Contains(Replies(), r => r.Contains("long sword") && r.Contains("1/100"));
    }

    // A gang house can stock the same item in more than one room — the reply
    // is ONE consolidated line (not one line per room, which used to flood
    // the channel — report 20260825-172400): summed quantity + every room.
    [Fact]
    public void Roomba_ItemSeenInMultipleRooms_ReportsOneConsolidatedLine()
    {
        var (engine, players, _, locations) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "4 long sword" });
        locations.RecordRoom(new RoomKey(1, 200), new[] { "2 long sword" });

        ChatLines.Hear(_routers[engine], Gangpath("Friend", "@roomba long sword"));

        string reply = Assert.Single(Replies());
        Assert.Contains("6x long sword", reply);   // 4 + 2 summed
        Assert.Contains("1/100", reply);
        Assert.Contains("1/200", reply);
    }

    // Report 20260827: a gang recon reported implausibly high totals for
    // hidden items with no way to tell whether that was several genuinely
    // separate stashes or one room's count gone wrong. Each room locator now
    // carries its own quantity, not just a bare room list, so the reply
    // itself is the diagnostic.
    [Fact]
    public void Roomba_ItemSeenInMultipleRooms_ShowsPerRoomQuantity()
    {
        var (engine, players, _, locations) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "4 long sword" });
        locations.RecordRoom(new RoomKey(1, 200), new[] { "2 long sword" });

        ChatLines.Hear(_routers[engine], Gangpath("Friend", "@roomba long sword"));

        string reply = Assert.Single(Replies());
        Assert.Contains("6x long sword", reply);
        Assert.Contains("1/100 (4)", reply);
        Assert.Contains("1/200 (2)", reply);
    }

    // A gang member asking @roomba needs to know how fresh the location is —
    // a room swept minutes ago is a much stronger signal than one last swept
    // weeks back. RecordRoom stamps SeenAt off the wall clock, so the reply's
    // formatted minute matches "now" at test time.
    [Fact]
    public void Roomba_Reply_IncludesLastScannedTimestamp()
    {
        var (engine, players, _, locations) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "long sword" });
        // The stamp the sighting took, read back so a minute rolling over can't split it.
        DateTimeOffset seen = Assert.Single(locations.FindSightings("long sword")).SeenAt;

        ChatLines.Hear(_routers[engine], Gangpath("Friend", "@roomba long sword"));

        string reply = Assert.Single(Replies());
        Assert.Contains(
            $"last scanned {seen:yyyy-MM-dd HH:mm} {TimeZoneAbbreviation.For(seen)}", reply);
    }

    // Beyond MaxRoomsShown, the room list folds into a "+N more" tail instead
    // of further lines — the whole point of consolidating is staying at one
    // line even for an item scattered across many rooms.
    [Fact]
    public void Roomba_ItemInManyRooms_CapsRoomListWithOverflowTail()
    {
        var (engine, players, _, locations) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        for (int room = 1; room <= 12; room++)
            locations.RecordRoom(new RoomKey(1, room), new[] { "long sword" });

        ChatLines.Hear(_routers[engine], Gangpath("Friend", "@roomba long sword"));

        string reply = Assert.Single(Replies());
        Assert.Contains("12x long sword", reply);
        Assert.Contains("+2 more", reply);
    }

    // A loose query matching a whole family of similarly-named items (report
    // 20260825-174300: "@roomba severed" / "@roomba head" returned nothing for
    // "severed head of goru-nezar" / "severed head of darksong") must report
    // EVERY matching item, one line each — not silently nothing just because
    // more than one name matched.
    [Fact]
    public void Roomba_QueryMatchesMultipleDistinctItems_ReportsOneLinePerItem()
    {
        var (engine, players, _, locations) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "severed head of goru-nezar" });
        locations.RecordRoom(new RoomKey(1, 200), new[] { "severed head of darksong" });

        ChatLines.Hear(_routers[engine], Gangpath("Friend", "@roomba severed head"));

        var replies = Replies();
        Assert.Equal(2, replies.Count);
        Assert.Contains(replies, r => r.Contains("severed head of goru-nezar") && r.Contains("1/100"));
        Assert.Contains(replies, r => r.Contains("severed head of darksong") && r.Contains("1/200"));
    }

    // Beyond MaxItemsShown distinct items, the overflow folds into one final
    // line rather than flooding the channel with a line per item.
    [Fact]
    public void Roomba_QueryMatchesManyDistinctItems_CapsWithOverflowTail()
    {
        var (engine, players, _, locations) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        for (int i = 1; i <= 7; i++)
            locations.RecordRoom(new RoomKey(1, i), new[] { $"severed head of npc{i}" });

        ChatLines.Hear(_routers[engine], Gangpath("Friend", "@roomba severed"));

        var replies = Replies();
        Assert.Equal(6, replies.Count);   // 5 item lines + one overflow line
        Assert.Contains(replies, r => r.Contains("2 more matching item"));
    }

    [Fact]
    public void Roomba_UnknownItem_ReportsNoRecord()
    {
        var (engine, players, _, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);

        ChatLines.Hear(_routers[engine], Gangpath("Friend", "@roomba nonexistent item"));

        Assert.Contains(Replies(), r => r.Contains("no record"));
    }

    [Fact]
    public void Roomba_WithoutGrant_StaysDenied()
    {
        var (engine, players, _, locations) = Setup();
        SeedPlayer(players, "Stranger", PlayerRemoteControls.None);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "long sword" });

        ChatLines.Hear(_routers[engine], Gangpath("Stranger", "@roomba long sword"));

        Assert.DoesNotContain(Replies(), r => r.Contains("long sword"));
    }

    [Fact]
    public void RoombaSync_RepliesWithDecodableEncodingOfSightings()
    {
        var (engine, players, locations) = SetupWithItems();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "3 torch" });

        ChatLines.Hear(_routers[engine], Gangpath("Friend", "@roomba sync"));

        IReadOnlyList<GhItemSyncRecord> decoded = DecodeSyncReplies();
        GhItemSyncRecord r = Assert.Single(decoded);
        Assert.Equal(1, r.Map);
        Assert.Equal(100, r.Room);
        Assert.Equal(1, r.ItemNumber);
        Assert.Equal(3, r.Quantity);
    }

    [Fact]
    public void RoombaSync_NoSightings_RepliesWithDecodableEmptySet()
    {
        var (engine, players, _) = SetupWithItems();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);

        ChatLines.Hear(_routers[engine], Gangpath("Friend", "@roomba sync"));

        Assert.Empty(DecodeSyncReplies());
    }

    [Fact]
    public void RoombaSync_EndsWithSyncCompleteSentinel()
    {
        var (engine, players, locations) = SetupWithItems();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "torch" });

        ChatLines.Hear(_routers[engine], Gangpath("Friend", "@roomba sync"));

        string last = Replies().Last(r => r.Contains(RoombaQueryHandler.SyncResponseToken));
        Assert.Contains(RoombaQueryHandler.SyncCompleteMarker, last);
    }

    // ----- the channels a sync may go by --------------------------------------

    private static ChatLine Heard(ChatChannel channel, string sender, string msg) =>
        new(channel, sender, msg);

    // The gang house's log goes to one player or to the gang.
    [Theory]
    [InlineData(ChatChannel.TelepathIncoming, "/Friend {")]
    [InlineData(ChatChannel.Gangpath, "bg {")]
    public void RoombaSync_ByTelepathOrGangpath_IsAnsweredThere(ChatChannel channel, string opening)
    {
        var (engine, players, locations) = SetupWithItems();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "3 torch" });

        ChatLines.Hear(_routers[engine], Heard(channel, "Friend", "@roomba sync"));

        Assert.Single(DecodeSyncReplies());
        Assert.All(Replies(), r => Assert.StartsWith(opening + RoombaQueryHandler.SyncResponseToken, r));
    }

    // Said aloud or on the broadcast channel it is refused, with where it does work,
    // and nothing of the log is sent.
    [Theory]
    [InlineData(ChatChannel.Local, ">Friend {@roomba sync works by telepath or gangpath only}\r")]
    [InlineData(ChatChannel.Broadcast, "br {@roomba sync works by telepath or gangpath only}\r")]
    public void RoombaSync_BySayOrBroadcast_IsRefusedWithWhereItWorks(ChatChannel channel, string expected)
    {
        var (engine, players, locations) = SetupWithItems();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "3 torch" });
        engine.DisableBroadcastChannel = false;   // the broadcast channel opened on the Talk tab

        ChatLines.Hear(_routers[engine], Heard(channel, "Friend", "@roomba sync"));

        Assert.Equal(expected, Replies().Single());
    }

    [Fact]
    public void RoombaSync_RefusedForItsChannel_FollowsTheWarnSetting_AndGoesByTelepathWhileSneaking()
    {
        var (quiet, quietPlayers, _) = SetupWithItems();
        SeedPlayer(quietPlayers, "Friend", PlayerRemoteControls.QueryItemLocation);
        quiet.WarnOnDenial = false;
        ChatLines.Hear(_routers[quiet], Heard(ChatChannel.Local, "Friend", "@roomba sync"));
        Assert.Empty(Replies());

        var (sneaking, sneakingPlayers, _) = SetupWithItems();
        SeedPlayer(sneakingPlayers, "Friend", PlayerRemoteControls.QueryItemLocation);
        sneaking.StealthedProvider = () => true;
        ChatLines.Hear(_routers[sneaking], Heard(ChatChannel.Local, "Friend", "@roomba sync"));
        Assert.Equal("/Friend {@roomba sync works by telepath or gangpath only}\r", Replies().Single());
    }

    // Only the sync is held to those channels: asking where an item is still works
    // when said.
    [Fact]
    public void RoombaItemQuery_BySay_StillAnswers()
    {
        var (engine, players, locations) = SetupWithItems();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "3 torch" });

        ChatLines.Hear(_routers[engine], Heard(ChatChannel.Local, "Friend", "@roomba torch"));

        Assert.Contains("total: 3x torch", Replies().Single());
    }

    // A channel switched off on the Talk tab stays off though the sync may use it.
    [Fact]
    public void RoombaSync_OnGangpathSwitchedOffOnTheTalkTab_IsNotAnswered()
    {
        var (engine, players, locations) = SetupWithItems();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryItemLocation);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "3 torch" });
        engine.DisableGangpathChannel = true;

        ChatLines.Hear(_routers[engine], Gangpath("Friend", "@roomba sync"));

        Assert.Empty(Replies());
    }

    [Fact]
    public void RoombaSync_WithoutGrant_StaysDenied()
    {
        var (engine, players, locations) = SetupWithItems();
        SeedPlayer(players, "Stranger", PlayerRemoteControls.None);
        locations.RecordRoom(new RoomKey(1, 100), new[] { "torch" });

        ChatLines.Hear(_routers[engine], Gangpath("Stranger", "@roomba sync"));

        Assert.DoesNotContain(Replies(), r => r.Contains(RoombaQueryHandler.SyncResponseToken));
    }
}
