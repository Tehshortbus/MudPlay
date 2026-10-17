using System.Text;
using MudPlay.Game;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Pins the write-side InventoryActionHandler commands
// (@drop-all / @deposit-all / @share): each reads the
// immutable InventoryManager.Snapshot, gates on "parse
// inventory first" until a full i dump lands, and emits its wire verb
// (drop / dep / with / give) on the bound sender
// while replying a friendly summary on the sender's channel.
public sealed class InventoryActionHandlerTests
{
    // What the remote-command engine put on the wire, through the sender the client binds.
    private readonly List<byte[]> _replies = new();

    private static readonly DateTime Now = new(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        public required RemoteCommandManager Engine { get; init; }
        public required InventoryManager Inventory { get; init; }
        // The game's output, fed the way the client receives it.
        public required ServerFeed Feed { get; init; }
        public required MessageRouter Router { get; init; }
        public required GroundItemTracker Ground { get; init; }
        public required PlayerDatabase Players { get; init; }
        public required PartyState Party { get; init; }
        public required CashSettings Cash { get; init; }
        public required List<byte[]> WireSent { get; init; }
    }

    private Harness Setup(bool paradigm = false, Func<string, bool, bool>? cannotDrop = null,
        Func<string, bool>? isCursed = null)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        RemoteCommandManager engine = new(chat, party, players);
        engine.SetWireSender(_replies.Add);
        InventoryManager inv = new(log: null, itemWeightResolver: null, slotResolver: null);
        ServerFeed feed = new();
        inv.AttachLineExtractor(feed.Lines);
        GroundItemTracker ground = new(router, new CurrencyNaming());
        CashSettings cash = new();
        List<byte[]> wire = new();
        InventoryActionHandler handler = new(engine, inv, ground, party, () => cash, new CurrencyNaming(),
            isParadigm: () => paradigm, cannotDrop: cannotDrop, isCursed: isCursed);
        handler.SetWireSender(wire.Add);
        return new Harness
        {
            Engine = engine,
            Inventory = inv,
            Feed = feed,
            Router = router,
            Ground = ground,
            Players = players,
            Party = party,
            Cash = cash,
            WireSent = wire,
        };
    }

    // Push a single-line "You notice ... here." through the router so the
    // GroundItemTracker's pattern subscription fires (the room-survey path).
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

    private static void JoinParty(PartyState party, params (string Name, bool IsSelf)[] members)
    {
        foreach ((string name, bool isSelf) in members)
            party.Members.Add(new PartyMember { Name = name, IsSelf = isSelf });
        party.IsInParty = true;
    }

    private List<string> Replies() =>
        _replies
            .Select(b => Encoding.Latin1.GetString(b))
            .Select(StripWire)
            .ToList();

    private static List<string> Wire(Harness h) =>
        h.WireSent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

    private static string StripWire(string wire)
    {
        string s = wire.TrimEnd('\r');
        int open = s.IndexOf('{');
        int close = s.LastIndexOf('}');
        return open >= 0 && close > open ? s[(open + 1)..close] : s;
    }

    // ----- @get-all ----------------------------------------------------

    [Fact]
    public void GetAll_EmptyCache_ResurveysThenGrabsOnFreshSurvey()
    {
        // Report stock-20260730-215247: a stale/empty ground cache (items dropped
        // mid-combat with no auto re-survey) left Get All doing nothing. It now
        // re-surveys with a bare CR and grabs on the fresh "You notice" survey.
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(h.Router, Telepath("Bob", "@get-all"));
        Assert.Equal("re-surveying the floor for get-all", Assert.Single(Replies()));
        Assert.Equal(new[] { "" }, Wire(h));   // the bare CR (trailing \r trimmed)

        // The fresh survey lands with loot → it's grabbed.
        FeedRoom(h.Router, "You notice a rusty dagger, an iron helm here.");
        Assert.Contains("get rusty dagger", Wire(h));
        Assert.Contains("get iron helm", Wire(h));
    }

    // A cursed item, once picked up and worn, can't be put down again: Get All leaves
    // it on the floor and says so (user, 2026-10-02).
    [Fact]
    public void GetAll_LeavesCursedItemsOnTheFloor()
    {
        Harness h = Setup(isCursed: name => name == "cursed ring");
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        FeedRoom(h.Router, "You notice a rusty dagger, a cursed ring here.");

        ChatLines.Hear(h.Router, Telepath("Bob", "@get-all"));

        Assert.Equal(new[] { "get rusty dagger" }, Wire(h));
        Assert.Equal("getting 1 ground item (leaving 1 cursed: cursed ring)", Assert.Single(Replies()));
    }

    [Fact]
    public void GetAll_EmptyCache_ResurveyStillEmpty_GrabsNothing()
    {
        // The re-survey confirms an empty floor → no gets fired (only the CR).
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(h.Router, Telepath("Bob", "@get-all"));
        FeedRoom(h.Router, "You notice 12 gold crowns here.");   // cash only, no items

        Assert.Equal(new[] { "" }, Wire(h));   // just the CR — nothing to grab
    }

    [Fact]
    public void GetAll_GetsEachGroundItem_StrippingArticlesExcludingCash()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        FeedRoom(h.Router, "You notice a rusty dagger, 12 gold crowns, "
                         + "an iron helm and a healing potion here.");

        ChatLines.Hear(h.Router, Telepath("Bob", "@get-all"));

        // Coin ("12 gold crowns") is excluded; each item's article is stripped.
        Assert.Equal(
            new[] { "get rusty dagger", "get iron helm", "get healing potion" },
            Wire(h));
        Assert.Equal("getting 3 ground items", Assert.Single(Replies()));
    }

    // ----- @drop-all ---------------------------------------------------

    [Fact]
    public void DropAll_BeforeParse_PointsAtInventory()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(h.Router, Telepath("Bob", "@drop-all"));

        Assert.Contains("inventory not parsed yet", Assert.Single(Replies()));
        Assert.Empty(Wire(h));
    }

    [Fact]
    public void DropAll_EmptyPack_ReportsNothing()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        FeedCurrencyDump(h.Feed); // coins only — no carried items

        ChatLines.Hear(h.Router, Telepath("Bob", "@drop-all"));

        Assert.Equal("nothing to drop", Assert.Single(Replies()));
        Assert.Empty(Wire(h));
    }

    [Fact]
    public void DropAll_DropsEachCarriedItem_StrippingArticles()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        h.Feed.Line("You are carrying a rusty dagger, a healing potion, "
                    + "padded vest (Torso), a jagged dagger.");
        h.Feed.Line("Encumbrance:    36/2880  -  Light  [1%]");

        ChatLines.Hear(h.Router, Telepath("Bob", "@drop-all"));

        // Worn "padded vest (Torso)" is excluded; the article is stripped.
        Assert.Equal(
            new[] { "drop rusty dagger", "drop healing potion", "drop jagged dagger" },
            Wire(h));
        Assert.Equal("dropping 3 carried items", Assert.Single(Replies()));
    }

    // A stacked pack entry: Stock has no item batching, so one `drop` per copy;
    // Paradigm drops the stack with one counted command.
    [Theory]
    [InlineData(false, new[] { "drop black diamond", "drop black diamond", "drop black diamond" })]
    [InlineData(true, new[] { "drop 3 black diamond" })]
    public void DropAll_StackedItem_BatchesOnlyOnParadigm(bool paradigm, string[] expected)
    {
        Harness h = Setup(paradigm);
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        h.Feed.Line("You are carrying 3 black diamond.");
        h.Feed.Line("Encumbrance:    36/2880  -  Light  [1%]");

        ChatLines.Hear(h.Router, Telepath("Bob", "@drop-all"));

        Assert.Equal(expected, Wire(h));
    }

    // The game refuses a no-drop item ("You may not drop that item!"), and a cursed
    // item while it is worn. Sending those only fills the game's command queue, so
    // the sweep leaves them out and says which it kept.
    [Fact]
    public void DropAllFull_LeavesOutWhatTheGameWontDrop_AndSaysSo()
    {
        Harness h = Setup(paradigm: true, cannotDrop: (name, worn) =>
            name == "token of Silvermere" || (worn && name == "cursed ring"));
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        h.Feed.Line("You are carrying a rusty dagger, token of Silvermere, cursed ring (Finger), "
                    + "padded vest (Torso).");
        h.Feed.Line("You have no keys.");
        h.Feed.Line("Wealth:    0 copper farthings");
        h.Feed.Line("Encumbrance:    36/2880  -  Light  [1%]");

        ChatLines.Hear(h.Router, Telepath("Bob", "@drop-all full"));

        string[] sent = Wire(h).ToArray();
        Assert.Contains("drop rusty dagger", sent);
        Assert.Contains("drop padded vest", sent);
        Assert.DoesNotContain("drop token of Silvermere", sent);
        Assert.DoesNotContain("drop cursed ring", sent);
        Assert.EndsWith("(keeping 2 that can't be dropped: token of Silvermere, cursed ring)",
            Assert.Single(Replies()));
    }

    [Fact]
    public void DropAllFull_DropsWornGearLightKeysAndCoins()
    {
        Harness h = Setup(paradigm: true);
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        h.Feed.Line("You are carrying 2 runic coins, 94 gold crowns, a rusty dagger, "
                    + "padded vest (Torso), torch (Readied/40).");
        h.Feed.Line("You have the following keys: 3 black star key, gate key.");
        h.Feed.Line("Wealth:    2009400 copper farthings");
        h.Feed.Line("Encumbrance:    36/2880  -  Light  [1%]");

        ChatLines.Hear(h.Router, Telepath("Bob", "@drop-all full"));

        string[] sent = Wire(h).ToArray();
        Assert.Contains("drop rusty dagger", sent);
        Assert.Contains("drop padded vest", sent);          // worn — dropped directly, no rem
        Assert.Contains("drop torch", sent);                // the readied light
        Assert.Contains("drop 3 black star key", sent);     // batched on Paradigm
        Assert.Contains("drop gate key", sent);
        Assert.Contains("drop 94 gold crown", sent);
        Assert.Contains("drop 2 runic coin", sent);
        Assert.StartsWith("dropping everything:", Assert.Single(Replies()));
    }

    [Fact]
    public void DropAllCoins_DropsOnlyCoins()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        h.Feed.Line("You are carrying 94 gold crowns, 5 copper farthings, a rusty dagger.");
        h.Feed.Line("Encumbrance:    36/2880  -  Light  [1%]");

        ChatLines.Hear(h.Router, Telepath("Bob", "@drop-all coins"));

        Assert.Equal(new[] { "drop 5 copper farthing", "drop 94 gold crown" }, Wire(h));
    }

    [Fact]
    public void DropAllKeys_OnStock_OneDropPerKey()
    {
        Harness h = Setup(paradigm: false);
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        h.Feed.Line("You are carrying a rusty dagger.");
        h.Feed.Line("You have the following keys: 2 black star key, gate key.");
        h.Feed.Line("Encumbrance:    36/2880  -  Light  [1%]");

        ChatLines.Hear(h.Router, Telepath("Bob", "@drop-all keys"));

        Assert.Equal(new[] { "drop black star key", "drop black star key", "drop gate key" }, Wire(h));
        Assert.Equal("dropping 3 keys", Assert.Single(Replies()));
    }

    [Fact]
    public void DropAll_UnknownScope_RepliesUsage_DropsNothing()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        h.Feed.Line("You are carrying a rusty dagger.");
        h.Feed.Line("Encumbrance:    36/2880  -  Light  [1%]");

        ChatLines.Hear(h.Router, Telepath("Bob", "@drop-all everything"));

        Assert.StartsWith("usage: @drop-all", Assert.Single(Replies()));
        Assert.Empty(Wire(h));
    }

    // ----- @hide-all ---------------------------------------------------

    [Fact]
    public void HideAllFull_HidesWornGearLightKeysAndCoins_NeverABareHide()
    {
        Harness h = Setup(paradigm: true);
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        h.Feed.Line("You are carrying 94 gold crowns, a rusty dagger, padded vest (Torso), torch (Readied/40).");
        h.Feed.Line("You have the following keys: 3 black star key.");
        h.Feed.Line("Encumbrance:    36/2880  -  Light  [1%]");

        ChatLines.Hear(h.Router, Telepath("Bob", "@hide-all full"));

        string[] sent = Wire(h).ToArray();
        Assert.Contains("hide rusty dagger", sent);
        Assert.Contains("hide padded vest", sent);          // worn — hidden directly, no rem
        Assert.Contains("hide torch", sent);
        Assert.Contains("hide 3 black star key", sent);     // batched on Paradigm
        Assert.Contains("hide 94 gold crown", sent);
        Assert.DoesNotContain("hide", sent);                // a bare hide hides the player
        Assert.StartsWith("hiding everything:", Assert.Single(Replies()));
    }

    [Fact]
    public void HideAll_OnStock_OneHidePerCopy()
    {
        Harness h = Setup(paradigm: false);
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        h.Feed.Line("You are carrying 2 black diamond.");
        h.Feed.Line("Encumbrance:    36/2880  -  Light  [1%]");

        ChatLines.Hear(h.Router, Telepath("Bob", "@hide-all"));

        Assert.Equal(new[] { "hide black diamond", "hide black diamond" }, Wire(h));
        Assert.Equal("hiding 2 carried items", Assert.Single(Replies()));
    }

    [Fact]
    public void HideAll_UnknownScope_RepliesUsage()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        h.Feed.Line("You are carrying a rusty dagger.");
        h.Feed.Line("Encumbrance:    36/2880  -  Light  [1%]");

        ChatLines.Hear(h.Router, Telepath("Bob", "@hide-all everything"));

        Assert.Empty(Wire(h));
        Assert.StartsWith("usage: @hide-all", Assert.Single(Replies()));
    }

    // ----- @deposit-all ------------------------------------------------

    [Fact]
    public void DepositAll_BeforeParse_PointsAtInventory()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);

        ChatLines.Hear(h.Router, Telepath("Bob", "@deposit-all"));

        Assert.Contains("parse inventory first", Assert.Single(Replies()));
        Assert.Empty(Wire(h));
    }

    [Fact]
    public void DepositAll_OverKeep_DepositsExcessInCopper()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        FeedCurrencyDump(h.Feed); // wealth 2,069,425 copper, keep defaults to 0

        ChatLines.Hear(h.Router, Telepath("Bob", "@deposit-all"));

        Assert.Equal(new[] { "dep 2069425" }, Wire(h));
        Assert.Equal("depositing 2,069,425 copper (keeping 0)", Assert.Single(Replies()));
    }

    [Fact]
    public void DepositAll_UnderKeep_WithdrawsShortfall()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        h.Cash.KeepOnHandWealth = 3_000_000; // keep floor in copper
        FeedCurrencyDump(h.Feed);  // holding 2,069,425 copper

        ChatLines.Hear(h.Router, Telepath("Bob", "@deposit-all"));

        Assert.Equal(new[] { "with 930575" }, Wire(h));
        Assert.Equal("withdrawing 930,575 copper (up to 3,000,000 on hand)",
            Assert.Single(Replies()));
    }

    [Fact]
    public void DepositAll_ExactlyAtKeep_NoOp()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        // Keep floor set to the exact held wealth (2,069,425 copper).
        h.Cash.KeepOnHandWealth = 2_069_425;
        FeedCurrencyDump(h.Feed);

        ChatLines.Hear(h.Router, Telepath("Bob", "@deposit-all"));

        Assert.Empty(Wire(h));
        Assert.Equal("already at keep-on-hand (2,069,425 copper)", Assert.Single(Replies()));
    }

    // ----- @share ------------------------------------------------------

    [Fact]
    public void Share_BeforeParse_PointsAtInventory()
    {
        Harness h = Setup();
        JoinParty(h.Party, ("Me", true), ("Bob", false));

        ChatLines.Hear(h.Router, Telepath("Bob", "@share"));

        Assert.Contains("parse inventory first", Assert.Single(Replies()));
        Assert.Empty(Wire(h));
    }

    [Fact]
    public void Share_SplitsCoinsPerDenominationAmongParty()
    {
        Harness h = Setup();
        // Party of three (self + two followers); split divides by 3.
        JoinParty(h.Party, ("Me", true), ("Bob", false), ("Carol Whitmane", false));
        FeedCurrencyDump(h.Feed); // 5c 2s 94g 6p 2r

        ChatLines.Hear(h.Router, Telepath("Bob", "@share"));

        // per-denom = count / 3: copper 1, silver 0 (skip), gold 31,
        // platinum 2, runic 0 (skip). Two-word name gives on the given name.
        Assert.Equal(
            new[]
            {
                "give 1 copper to Bob", "give 1 copper to Carol",
                "give 31 gold to Bob", "give 31 gold to Carol",
                "give 2 platinum to Bob", "give 2 platinum to Carol",
            },
            Wire(h));
        Assert.Equal("sharing coins among 3 party members", Assert.Single(Replies()));
    }

    [Fact]
    public void Share_TooFewCoinsToSplit_ReportsNothing()
    {
        Harness h = Setup();
        JoinParty(h.Party, ("Me", true), ("Bob", false), ("Carol", false),
            ("Dave", false), ("Ed", false)); // party of five
        h.Feed.Line("You are carrying 1 platinum piece, 4 gold crowns, "
                    + "2 silver nobles, 3 copper farthings.");
        h.Feed.Line("Encumbrance:    36/2880  -  Light  [1%]");

        ChatLines.Hear(h.Router, Telepath("Bob", "@share"));

        Assert.Empty(Wire(h)); // every denom < 5, so per-head share is 0
        Assert.Equal("nothing to share (too few coins to split)", Assert.Single(Replies()));
    }

    // ----- gating ------------------------------------------------------

    [Fact]
    public void Share_FromNonPartyMember_IsDenied()
    {
        Harness h = Setup();
        h.Engine.WarnOnDenial = false;
        JoinParty(h.Party, ("Me", true), ("Bob", false)); // Stranger not in party
        FeedCurrencyDump(h.Feed);

        ChatLines.Hear(h.Router, Telepath("Stranger", "@share"));

        Assert.Empty(Replies());
        Assert.Empty(Wire(h));
    }

    [Fact]
    public void DropAll_FromUnauthorisedSender_IsDenied()
    {
        Harness h = Setup();
        h.Engine.WarnOnDenial = false;
        SeedPlayer(h.Players, "Stranger",
            PlayerRemoteControls.All & ~PlayerRemoteControls.ExecuteCommands);
        h.Feed.Line("You are carrying a rusty dagger.");
        h.Feed.Line("Encumbrance:    36/2880  -  Light  [1%]");

        ChatLines.Hear(h.Router, Telepath("Stranger", "@drop-all"));

        Assert.Empty(Replies());
        Assert.Empty(Wire(h));
    }
}
