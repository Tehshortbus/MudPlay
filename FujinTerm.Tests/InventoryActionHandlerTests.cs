using System.Reflection;
using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Cash;
using FujinTerm.Game.Inventory;
using FujinTerm.Game.Remote;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the write-side <see cref="InventoryActionHandler"/> commands
/// (<c>@drop-all</c> / <c>@deposit-all</c> / <c>@share</c>): each reads the
/// immutable <see cref="InventoryManager.Snapshot"/>, gates on "parse
/// inventory first" until a full <c>i</c> dump lands, and emits its wire verb
/// (<c>drop</c> / <c>dep</c> / <c>with</c> / <c>give</c>) on the bound sender
/// while replying a friendly summary on the sender's channel.
/// </summary>
public sealed class InventoryActionHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        public required RemoteCommandManager Engine { get; init; }
        public required InventoryManager Inventory { get; init; }
        public required LineExtractor Lines { get; init; }
        public required MessageRouter Router { get; init; }
        public required GroundItemTracker Ground { get; init; }
        public required PlayerDatabase Players { get; init; }
        public required PartyState Party { get; init; }
        public required CashSettings Cash { get; init; }
        public required List<byte[]> WireSent { get; init; }
    }

    private static Harness Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        RemoteCommandManager engine = new(chat, party, players);
        InventoryManager inv = new(log: null, itemWeightResolver: null, slotResolver: null);
        LineExtractor lines = new(new TerminalEmulator(80, 24));
        inv.AttachLineExtractor(lines);
        GroundItemTracker ground = new(router, new CurrencyNaming());
        CashSettings cash = new();
        List<byte[]> wire = new();
        InventoryActionHandler handler = new(engine, inv, ground, party, () => cash, new CurrencyNaming());
        handler.SetWireSender(wire.Add);
        return new Harness
        {
            Engine = engine,
            Inventory = inv,
            Lines = lines,
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

    private static void Feed(LineExtractor lines, string text)
    {
        FieldInfo? field = typeof(LineExtractor).GetField(
            "LineEmitted", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(lines) is Action<LineExtractor.EmittedLine> handler)
        {
            handler(new LineExtractor.EmittedLine(
                text, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));
        }
    }

    private static void FeedCurrencyDump(LineExtractor lines)
    {
        Feed(lines, "You are carrying 2 runic coins, 6 platinum pieces, 94 gold crowns, "
                  + "2 silver nobles, 5 copper farthings.");
        Feed(lines, "You have no keys.");
        Feed(lines, "Wealth:    2069425 copper farthings");
        Feed(lines, "Encumbrance:    36/2880  -  Light  [1%]");
    }

    private static ChatLogEntry Telepath(string sender, string msg) =>
        new(Now, ChatChannel.TelepathIncoming, sender, msg, $"{sender} telepaths: {msg}");

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

    private static List<string> Replies(RemoteCommandManager engine) =>
        engine.LastSentForTests
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
    public void GetAll_EmptyFloor_ReportsNothing()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);

        h.Engine.DispatchForTests(Telepath("Bob", "@get-all"));

        Assert.Equal("nothing on the ground to get", Assert.Single(Replies(h.Engine)));
        Assert.Empty(Wire(h));
    }

    [Fact]
    public void GetAll_GetsEachGroundItem_StrippingArticlesExcludingCash()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        FeedRoom(h.Router, "You notice a rusty dagger, 12 gold crowns, "
                         + "an iron helm and a healing potion here.");

        h.Engine.DispatchForTests(Telepath("Bob", "@get-all"));

        // Coin ("12 gold crowns") is excluded; each item's article is stripped.
        Assert.Equal(
            new[] { "get rusty dagger", "get iron helm", "get healing potion" },
            Wire(h));
        Assert.Equal("getting 3 ground items", Assert.Single(Replies(h.Engine)));
    }

    // ----- @drop-all ---------------------------------------------------

    [Fact]
    public void DropAll_BeforeParse_PointsAtInventory()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);

        h.Engine.DispatchForTests(Telepath("Bob", "@drop-all"));

        Assert.Contains("inventory not parsed yet", Assert.Single(Replies(h.Engine)));
        Assert.Empty(Wire(h));
    }

    [Fact]
    public void DropAll_EmptyPack_ReportsNothing()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        FeedCurrencyDump(h.Lines); // coins only — no carried items

        h.Engine.DispatchForTests(Telepath("Bob", "@drop-all"));

        Assert.Equal("nothing to drop", Assert.Single(Replies(h.Engine)));
        Assert.Empty(Wire(h));
    }

    [Fact]
    public void DropAll_DropsEachCarriedItem_StrippingArticles()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        Feed(h.Lines, "You are carrying a rusty dagger, a healing potion, "
                    + "padded vest (Torso), a jagged dagger.");
        Feed(h.Lines, "Encumbrance:    36/2880  -  Light  [1%]");

        h.Engine.DispatchForTests(Telepath("Bob", "@drop-all"));

        // Worn "padded vest (Torso)" is excluded; the article is stripped.
        Assert.Equal(
            new[] { "drop rusty dagger", "drop healing potion", "drop jagged dagger" },
            Wire(h));
        Assert.Equal("dropping 3 carried items", Assert.Single(Replies(h.Engine)));
    }

    // ----- @deposit-all ------------------------------------------------

    [Fact]
    public void DepositAll_BeforeParse_PointsAtInventory()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);

        h.Engine.DispatchForTests(Telepath("Bob", "@deposit-all"));

        Assert.Contains("parse inventory first", Assert.Single(Replies(h.Engine)));
        Assert.Empty(Wire(h));
    }

    [Fact]
    public void DepositAll_OverKeep_DepositsExcessInCopper()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        FeedCurrencyDump(h.Lines); // wealth 2,069,425 copper, keep defaults to 0

        h.Engine.DispatchForTests(Telepath("Bob", "@deposit-all"));

        Assert.Equal(new[] { "dep 2069425" }, Wire(h));
        Assert.Equal("depositing 2,069,425 copper (keeping 0)", Assert.Single(Replies(h.Engine)));
    }

    [Fact]
    public void DepositAll_UnderKeep_WithdrawsShortfall()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        h.Cash.KeepRunicOnHand = 3; // keep floor = 3,000,000 copper
        FeedCurrencyDump(h.Lines);  // holding 2,069,425 copper

        h.Engine.DispatchForTests(Telepath("Bob", "@deposit-all"));

        Assert.Equal(new[] { "with 930575" }, Wire(h));
        Assert.Equal("withdrawing 930,575 copper (up to 3,000,000 on hand)",
            Assert.Single(Replies(h.Engine)));
    }

    [Fact]
    public void DepositAll_ExactlyAtKeep_NoOp()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        // Keep floors that sum to the exact held wealth (2,069,425 copper).
        h.Cash.KeepRunicOnHand = 2;
        h.Cash.KeepPlatinumOnHand = 6;
        h.Cash.KeepGoldOnHand = 94;
        h.Cash.KeepSilverOnHand = 2;
        h.Cash.KeepCopperOnHand = 5;
        FeedCurrencyDump(h.Lines);

        h.Engine.DispatchForTests(Telepath("Bob", "@deposit-all"));

        Assert.Empty(Wire(h));
        Assert.Equal("already at keep-on-hand (2,069,425 copper)", Assert.Single(Replies(h.Engine)));
    }

    // ----- @share ------------------------------------------------------

    [Fact]
    public void Share_BeforeParse_PointsAtInventory()
    {
        Harness h = Setup();
        JoinParty(h.Party, ("Me", true), ("Bob", false));

        h.Engine.DispatchForTests(Telepath("Bob", "@share"));

        Assert.Contains("parse inventory first", Assert.Single(Replies(h.Engine)));
        Assert.Empty(Wire(h));
    }

    [Fact]
    public void Share_SplitsCoinsPerDenominationAmongParty()
    {
        Harness h = Setup();
        // Party of three (self + two followers); split divides by 3.
        JoinParty(h.Party, ("Me", true), ("Bob", false), ("Carol Whitmane", false));
        FeedCurrencyDump(h.Lines); // 5c 2s 94g 6p 2r

        h.Engine.DispatchForTests(Telepath("Bob", "@share"));

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
        Assert.Equal("sharing coins among 3 party members", Assert.Single(Replies(h.Engine)));
    }

    [Fact]
    public void Share_TooFewCoinsToSplit_ReportsNothing()
    {
        Harness h = Setup();
        JoinParty(h.Party, ("Me", true), ("Bob", false), ("Carol", false),
            ("Dave", false), ("Ed", false)); // party of five
        Feed(h.Lines, "You are carrying 1 platinum piece, 4 gold crowns, "
                    + "2 silver nobles, 3 copper farthings.");
        Feed(h.Lines, "Encumbrance:    36/2880  -  Light  [1%]");

        h.Engine.DispatchForTests(Telepath("Bob", "@share"));

        Assert.Empty(Wire(h)); // every denom < 5, so per-head share is 0
        Assert.Equal("nothing to share (too few coins to split)", Assert.Single(Replies(h.Engine)));
    }

    // ----- gating ------------------------------------------------------

    [Fact]
    public void Share_FromNonPartyMember_IsDenied()
    {
        Harness h = Setup();
        h.Engine.WarnOnDenial = false;
        JoinParty(h.Party, ("Me", true), ("Bob", false)); // Stranger not in party
        FeedCurrencyDump(h.Lines);

        h.Engine.DispatchForTests(Telepath("Stranger", "@share"));

        Assert.Empty(Replies(h.Engine));
        Assert.Empty(Wire(h));
    }

    [Fact]
    public void DropAll_FromUnauthorisedSender_IsDenied()
    {
        Harness h = Setup();
        h.Engine.WarnOnDenial = false;
        SeedPlayer(h.Players, "Stranger",
            PlayerRemoteControls.All & ~PlayerRemoteControls.ExecuteCommands);
        Feed(h.Lines, "You are carrying a rusty dagger.");
        Feed(h.Lines, "Encumbrance:    36/2880  -  Light  [1%]");

        h.Engine.DispatchForTests(Telepath("Stranger", "@drop-all"));

        Assert.Empty(Replies(h.Engine));
        Assert.Empty(Wire(h));
    }
}
