using System.Text;
using MudPlay.Game;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Pins the read-only InventoryQueryHandler replies
// (@wealth / @enc / @have / @what): the
// wealth / carry / have trio read the immutable
// InventoryManager.Snapshot and gate on "parse inventory
// first" until the first full i dump lands; @what reads the
// GroundItemTracker's last "You notice" survey.
public sealed class InventoryQueryHandlerTests
{
    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    // What the remote-command engine put on the wire, through the sender the client binds.
    private readonly List<byte[]> _replies = new();

    private static readonly DateTime Now = new(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc);

    private (RemoteCommandManager engine, InventoryManager inv, ServerFeed feed,
        PlayerDatabase players, MessageRouter router, GroundItemTracker ground)
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
        InventoryManager inv = new(log: null, itemWeightResolver: null, slotResolver: null);
        ServerFeed feed = new();
        inv.AttachLineExtractor(feed.Lines);
        GroundItemTracker ground = new(router, new CurrencyNaming());
        _ = new InventoryQueryHandler(engine, inv, ground);
        return (engine, inv, feed, players, router, ground);
    }

    // Push a single-line "You notice ... here." through the router so the
    // GroundItemTracker's pattern subscription fires (the same path a real
    // room survey travels).
    private static void FeedRoom(MessageRouter router, string text) =>
        router.Dispatch(new LineExtractor.EmittedLine(
            text, Array.Empty<CellAttributes>(),
            DateTimeOffset.UtcNow, IsPromptLine: false));

    private static void FeedCurrencyDump(ServerFeed feed)
    {
        feed.Line("You are carrying 2 runic coins, 6 platinum pieces, 94 gold crowns, "
                  + "2 silver nobles, 5 copper farthings.");
        feed.Line("You have no keys.");
        feed.Line("Wealth:    2069425 copper farthings");
        feed.Line("Encumbrance:    36/2880  -  Light  [1%]");
    }

    private static ChatLine Telepath(string sender, string msg) =>
        new(ChatChannel.TelepathIncoming, sender, msg);

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    private List<string> Replies() =>
        _replies
            .Select(b => Encoding.Latin1.GetString(b))
            .Select(StripWire)
            .ToList();

    private static string StripWire(string wire)
    {
        string s = wire.TrimEnd('\r');
        int open = s.IndexOf('{');
        int close = s.LastIndexOf('}');
        return open >= 0 && close > open ? s[(open + 1)..close] : s;
    }

    // ----- @wealth -----------------------------------------------------

    [Fact]
    public void Wealth_BeforeParse_PointsAtInventory()
    {
        var (engine, _, _, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@wealth"));

        Assert.Contains("parse inventory first", Assert.Single(Replies()));
    }

    [Fact]
    public void Wealth_ReportsCoinsHighToLow_WithCopperTotal()
    {
        var (engine, _, feed, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        FeedCurrencyDump(feed);

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@wealth"));

        string reply = Assert.Single(Replies());
        Assert.Equal(
            "2 runic, 6 platinum, 94 gold, 2 silver, 5 copper (= 2,069,425 copper)",
            reply);
    }

    // ----- @enc --------------------------------------------------------

    [Fact]
    public void Enc_BeforeParse_PointsAtInventory()
    {
        var (engine, _, _, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@enc"));

        Assert.Contains("parse inventory first", Assert.Single(Replies()));
    }

    [Fact]
    public void Enc_ReportsCurMaxPctBracket()
    {
        var (engine, _, feed, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        FeedCurrencyDump(feed);

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@enc"));

        Assert.Equal("Encumbrance 36/2880 (1%) - Light", Assert.Single(Replies()));
    }

    // ----- @have -------------------------------------------------------

    [Fact]
    public void Have_WithoutArgs_ShowsUsage()
    {
        var (engine, _, feed, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        FeedCurrencyDump(feed);

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@have"));

        Assert.Equal("usage: @have <item name>", Assert.Single(Replies()));
    }

    [Fact]
    public void Have_BeforeParse_PointsAtInventory()
    {
        var (engine, _, _, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@have dagger"));

        Assert.Contains("inventory not parsed yet", Assert.Single(Replies()));
    }

    [Fact]
    public void Have_MatchesCarriedAndEquipped_CaseInsensitiveSubstring()
    {
        var (engine, _, feed, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        feed.Line("You are carrying a rusty dagger, a healing potion, "
                  + "padded vest (Torso), a jagged dagger.");
        feed.Line("Encumbrance:    36/2880  -  Light  [1%]");

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@have DAGGER"));

        Assert.Equal("yes - 2x 'DAGGER'", Assert.Single(Replies()));
    }

    [Fact]
    public void Have_StackedItem_ReportsTheStackQuantity_NotOne()
    {
        // Report: another user @have'd an item the sender held 25 of (a stack prints
        // as one "25 black diamonds" line) and got "1x". The reply must report the
        // real quantity, echoing the queried name: "yes - 25x 'black diamond'".
        var (engine, _, feed, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        feed.Line("You are carrying a rusty dagger, 25 black diamonds, a healing potion.");
        feed.Line("Encumbrance:    36/2880  -  Light  [1%]");

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@have black diamond"));

        Assert.Equal("yes - 25x 'black diamond'", Assert.Single(Replies()));
    }

    [Fact]
    public void Have_NoMatch_ReportsNo()
    {
        var (engine, _, feed, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        feed.Line("You are carrying a rusty dagger, a healing potion.");
        feed.Line("Encumbrance:    36/2880  -  Light  [1%]");

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@have longsword"));

        Assert.Equal("no - nothing matching 'longsword'", Assert.Single(Replies()));
    }

    [Fact]
    public void Have_MatchesKeyRing_NotJustCarriedPack()
    {
        // Report: "@have black star key" replied "no" while the key sat on the ring.
        // Keys live on the dump's separate "You have the following keys:" list, not in
        // CarriedItems — @have has to search the ring too.
        var (engine, _, feed, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        feed.Line("You are carrying a brass lantern.");
        feed.Line("You have the following keys:  black star key, brass key.");
        feed.Line("Encumbrance:    36/2880  -  Light  [1%]");

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@have black star key"));

        Assert.Equal("yes - 1x 'black star key'", Assert.Single(Replies()));
    }

    [Fact]
    public void Have_StackedKey_ReportsRingQuantity()
    {
        // A duplicated key stacks behind a leading count ("3 iron key") — the reply
        // must tally the stack, not report 1.
        var (engine, _, feed, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        feed.Line("You are carrying a brass lantern.");
        feed.Line("You have the following keys:  3 large iron key, brass key.");
        feed.Line("Encumbrance:    36/2880  -  Light  [1%]");

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@have iron key"));

        Assert.Equal("yes - 3x 'iron key'", Assert.Single(Replies()));
    }

    // ----- @inv --------------------------------------------------------

    [Fact]
    public void Inv_BeforeParse_PointsAtInventory()
    {
        var (engine, _, _, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@inv"));

        Assert.Contains("inventory not parsed yet", Assert.Single(Replies()));
    }

    [Fact]
    public void Inv_ReportsCarriedPack_ExcludingWornAndCurrency()
    {
        var (engine, _, feed, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        // Worn piece (Torso) and a readied light are excluded; coins never
        // land in CarriedItems. Only the two unworn pack items report.
        feed.Line("You are carrying a rusty dagger, a healing potion, "
                  + "padded vest (Torso), lantern (Readied/240), 5 copper farthings.");
        feed.Line("You have no keys.");
        feed.Line("Encumbrance:    36/2880  -  Light  [1%]");

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@inv"));

        Assert.Equal("carrying: a rusty dagger, a healing potion", Assert.Single(Replies()));
    }

    [Fact]
    public void Inv_AppendsKeyRing_WhenKeysHeld()
    {
        var (engine, _, feed, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        feed.Line("You are carrying a brass lantern.");
        feed.Line("You have the following keys:  black star key, brass key.");
        feed.Line("Encumbrance:    36/2880  -  Light  [1%]");

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@inv"));

        Assert.Equal("carrying: a brass lantern; keys: black star key, brass key",
            Assert.Single(Replies()));
    }

    [Fact]
    public void Inv_EmptyPackNoKeys_ReportsCarryingNothing()
    {
        var (engine, _, feed, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        feed.Line("You are carrying nothing.");
        feed.Line("You have no keys.");
        feed.Line("Encumbrance:    0/2880  -  Light  [0%]");

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@inv"));

        Assert.Equal("carrying nothing", Assert.Single(Replies()));
    }

    [Fact]
    public void Inv_LongPack_SplitsAcrossReplies_ReportsEveryItem_NoTruncation()
    {
        var (engine, _, feed, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        // 40 padded item names blow past a single say's budget; the WHOLE pack must
        // go out across multiple chunked replies rather than a "+N more" summary.
        var items = Enumerable.Range(1, 40).Select(i => $"a padded widget number {i}").ToList();
        feed.Line($"You are carrying {string.Join(", ", items)}.");
        feed.Line("Encumbrance:    36/2880  -  Light  [1%]");

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@inv"));

        List<string> replies = Replies();
        Assert.True(replies.Count > 1, "a long pack should split across multiple replies");
        // No truncation summary anywhere.
        Assert.DoesNotContain(replies, r => r.Contains("more, type i)"));
        // Every item appears across the replies, and each reply fits a say line.
        string all = string.Join(" ", replies);
        foreach (string item in items)
            Assert.Contains(item, all);
        foreach (string r in replies)
            Assert.True(r.Length < 200, $"reply too long ({r.Length}): {r}");
        // Split parts are labelled "carrying (i/N): …".
        Assert.All(replies, r => Assert.StartsWith("carrying (", r));
    }

    // ----- @what -------------------------------------------------------

    [Fact]
    public void What_NoSurvey_ReportsNothing()
    {
        var (engine, _, _, players, _, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@what"));

        Assert.Equal("nothing on the ground here", Assert.Single(Replies()));
    }

    [Fact]
    public void What_ReportsGroundItems_ExcludingCash()
    {
        var (engine, _, _, players, router, _) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryInventory);
        // Survey mixes loot and coin — coin is filtered, items keep wording.
        FeedRoom(router, "You notice a rusty dagger, 5 gold crowns and a healing potion here.");

        ChatLines.Hear(_routers[engine], Telepath("Bob", "@what"));

        Assert.Equal("on the ground: a rusty dagger, a healing potion",
            Assert.Single(Replies()));
    }

    // ----- gating ------------------------------------------------------

    [Fact]
    public void Wealth_FromUnauthorisedSender_IsDenied()
    {
        var (engine, _, feed, players, _, _) = Setup();
        engine.WarnOnDenial = false;
        SeedPlayer(players, "Stranger",
            PlayerRemoteControls.All & ~PlayerRemoteControls.QueryInventory);
        FeedCurrencyDump(feed);

        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@wealth"));

        Assert.Empty(Replies());
    }
}
