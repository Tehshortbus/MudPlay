using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game.Inventory;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// AutoBuyManager: watches the emitted line stream for a shop `list` readout,
// parses its three-column body, and buys each flagged ware up to MaxToGet — one
// `buy` per copy, advancing off the live "You just bought ..." / "You cannot
// afford ..." result. Live stock and the running carried count both cap the buy.
public sealed class AutoBuyManagerTests
{
    // Mirrors the aligned in-game grid the ShopListParser slices on.
    private const int QtyCol = 24;
    private const int PriceCol = 40;

    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public AutoBuyManager Buy { get; }
        // The game's output, fed the way the client receives it.
        public ServerFeed Server { get; } = new();
        public LineExtractor Lines => Server.Lines;
        public List<byte[]> Sent { get; } = new();
        public bool Enabled { get; set; } = true;
        public bool Paradigm { get; set; }
        // The clock the manager reads its reply window against.
        public DateTimeOffset Clock { get; set; } = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

        // name -> (Number, Buy, MaxToGet)
        private readonly Dictionary<string, (int Number, bool Buy, int Max)> _map =
            new(StringComparer.OrdinalIgnoreCase);

        // Number -> carried count
        public Dictionary<int, int> CarriedCount { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Buy = new AutoBuyManager(Router,
                resolve: Resolve,
                countCarried: n => CarriedCount.GetValueOrDefault(n),
                isEnabled: () => Enabled,
                log: Log,
                isParadigm: () => Paradigm,
                now: () => Clock);
            Buy.AttachLineExtractor(Lines);
            Buy.SetWireSender(b => Sent.Add(b));
        }

        public void Map(string name, int number, bool buy, int max)
            => _map[name] = (number, buy, max);

        private AutoBuyManager.ResolvedBuy? Resolve(string entry)
            => _map.TryGetValue(entry.Trim(), out (int Number, bool Buy, int Max) v)
                ? new AutoBuyManager.ResolvedBuy(v.Number, entry.Trim(), v.Buy, v.Max)
                : null;

        // Pattern-line dispatch (buy results) rides the MessageRouter.
        public void Feed(string line) => Router.Dispatch(new LineExtractor.EmittedLine(
            line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        // Shop-list capture rides the LineExtractor stream.
        public void Emit(string line) => Server.Line(line);

        // Feed a whole shop readout: header, column header, separator, each stock
        // row, then a trailing blank that closes the capture and starts the pump.
        public void ShopList(params (string Name, int Qty)[] rows)
        {
            Emit("The following items are for sale here:");
            Emit(string.Empty);
            Emit("Item".PadRight(QtyCol) + "Quantity".PadRight(PriceCol - QtyCol) + "Price");
            Emit(new string('-', 47));
            foreach ((string name, int qty) in rows)
                Emit(name.PadRight(QtyCol) + qty.ToString().PadRight(PriceCol - QtyCol) + "Free");
            Emit(string.Empty);   // blank terminator → FinishCapture
        }

        public List<string> SentText => Sent
            .Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

        public void Dispose() => Buy.Dispose();
    }

    // Report paradigm-20261005-091552: the readout ends at the prompt, which sits on
    // an unfinished line and so never arrives as a line by itself. An errand
    // waiting on the stock timed out at every shop.
    [Fact]
    public void APromptAfterTheRows_ClosesTheReadout_WithoutATerminatingLine()
    {
        using Harness h = new();
        List<IReadOnlyList<ShopListParser.StockRow>> listed = new();
        h.Buy.StockListed += listed.Add;

        h.Emit("The following items are for sale here:");
        h.Buy.NotePromptSeen();                   // a prompt before any row closes nothing
        h.Emit("Item".PadRight(QtyCol) + "Quantity".PadRight(PriceCol - QtyCol) + "Price");
        h.Emit(new string('-', 47));
        h.Emit("torch".PadRight(QtyCol) + "250".PadRight(PriceCol - QtyCol) + "Free");
        Assert.Empty(listed);

        h.Buy.NotePromptSeen();

        Assert.Equal("torch", Assert.Single(Assert.Single(listed)).Name);

        // The prompt's line arriving later is not a second readout.
        h.Emit("[HP=100/MA=10]:buy torch");
        Assert.Single(listed);
    }

    [Fact]
    public void FlaggedItem_BuysUpToCap_OnePerResult()
    {
        using Harness h = new();
        h.Map("dagger", 1, buy: true, max: 3);

        h.ShopList(("dagger", 50));
        Assert.Equal(new[] { "buy dagger" }, h.SentText);   // one at a time

        h.Feed("You just bought dagger for 5 gold crowns.");
        h.Feed("You just bought dagger for 5 gold crowns.");
        h.Feed("You just bought dagger for 5 gold crowns.");

        Assert.Equal(3, h.SentText.Count);
        Assert.All(h.SentText, s => Assert.Equal("buy dagger", s));

        // Cap reached — a stray further result must not push a 4th buy.
        h.Feed("You just bought dagger for 5 gold crowns.");
        Assert.Equal(3, h.SentText.Count);
    }

    [Fact]
    public void Paradigm_BuysWholeQuantityInOneCountedCommand()
    {
        using Harness h = new() { Paradigm = true };
        h.Map("dagger", 1, buy: true, max: 3);

        h.ShopList(("dagger", 50));
        Assert.Equal(new[] { "buy 3 dagger" }, h.SentText);   // one batched command

        // The counted reply's qty group drains the whole order.
        h.Feed("You just bought 3 dagger for 15 gold crowns.");

        Assert.Single(h.SentText);
    }

    [Fact]
    public void LiveStock_CapsBelowMaxToGet()
    {
        using Harness h = new();
        h.Map("dagger", 1, buy: true, max: 10);

        h.ShopList(("dagger", 2));   // only two in stock
        h.Feed("You just bought dagger for 5 gold crowns.");
        h.Feed("You just bought dagger for 5 gold crowns.");

        Assert.Equal(2, h.SentText.Count);
    }

    [Fact]
    public void CarriedCount_CountsTowardCap()
    {
        using Harness h = new();
        h.Map("dagger", 1, buy: true, max: 3);
        h.CarriedCount[1] = 2;       // already holding two of the three

        h.ShopList(("dagger", 50));
        h.Feed("You just bought dagger for 5 gold crowns.");

        Assert.Single(h.SentText);   // 3 cap − 2 carried = 1 buy
    }

    [Fact]
    public void UnboundedCap_BuysWholeStock()
    {
        using Harness h = new();
        h.Map("dagger", 1, buy: true, max: int.MaxValue);   // blank cap = "All"

        h.ShopList(("dagger", 2));
        h.Feed("You just bought dagger for 5 gold crowns.");
        h.Feed("You just bought dagger for 5 gold crowns.");

        Assert.Equal(2, h.SentText.Count);
    }

    [Fact]
    public void CannotAfford_AbandonsThatWare()
    {
        using Harness h = new();
        h.Map("dagger", 1, buy: true, max: 5);

        h.ShopList(("dagger", 50));
        Assert.Single(h.Sent);                        // first buy attempt

        h.Feed("You cannot afford dagger.");           // purse spent

        Assert.Single(h.Sent);                         // no further attempts
    }

    [Fact]
    public void CannotAfford_MovesToNextWare()
    {
        using Harness h = new();
        h.Map("dagger", 1, buy: true, max: 5);
        h.Map("torch", 2, buy: true, max: 5);

        h.ShopList(("dagger", 50), ("torch", 50));
        Assert.Equal(new[] { "buy dagger" }, h.SentText);

        h.Feed("You cannot afford dagger.");           // skip to torch
        Assert.Equal(new[] { "buy dagger", "buy torch" }, h.SentText);
    }

    // Stock's other answers to a buy that bought nothing. None is "You cannot
    // afford", so the pump used to wait on a result that never came and the wares
    // behind it went unbought.
    [Theory]
    [InlineData("You cannot buy dagger here!")]                          // out of stock
    [InlineData("dagger is not a known item.")]
    [InlineData("You cannot carry that much!")]                          // over what can be carried
    [InlineData("A strange force stops you from getting this item.")]
    public void BuyRefused_MovesToNextWare(string refusal)
    {
        using Harness h = new();
        h.Map("dagger", 1, buy: true, max: 5);
        h.Map("torch", 2, buy: true, max: 5);

        h.ShopList(("dagger", 50), ("torch", 50));
        Assert.Equal(new[] { "buy dagger" }, h.SentText);

        h.Feed(refusal);
        Assert.Equal(new[] { "buy dagger", "buy torch" }, h.SentText);
    }

    // The carry refusal also answers a `get`: with no buy of ours out it is not ours.
    [Fact]
    public void BuyRefused_WithNothingOut_DoesNothing()
    {
        using Harness h = new();
        h.Map("dagger", 1, buy: true, max: 1);

        h.ShopList(("dagger", 50));
        h.Feed("You just bought dagger for nothing.");   // the queue is done
        h.Feed("You cannot carry that much!");

        Assert.Equal(new[] { "buy dagger" }, h.SentText);
    }

    // A pump left waiting (its result line was lost) must not be set off by a carry
    // refusal that arrives long after: that one answers some other command, and the
    // pump would send its next `buy` wherever the character then stood.
    [Fact]
    public void StalledPump_NotAdvancedByALaterCarryRefusal()
    {
        using Harness h = new();
        h.Map("dagger", 1, buy: true, max: 5);
        h.Map("torch", 2, buy: true, max: 5);

        h.ShopList(("dagger", 50), ("torch", 50));
        Assert.Equal(new[] { "buy dagger" }, h.SentText);

        h.Clock += AutoBuyManager.ReplyWindow + TimeSpan.FromSeconds(1);
        h.Feed("You cannot carry that much!");
        h.Feed("A strange force stops you from getting this item.");

        Assert.Equal(new[] { "buy dagger" }, h.SentText);
    }

    // A nameless refusal after our buy was already answered belongs to another
    // command (a `get`, another engine's buy).
    [Fact]
    public void CarryRefusal_AfterOurBuyWasAnswered_IsNotOurs()
    {
        using Harness h = new();
        h.Map("dagger", 1, buy: true, max: 1);
        h.Map("torch", 2, buy: true, max: 1);

        h.ShopList(("dagger", 50), ("torch", 50));
        h.Feed("You just bought dagger for nothing.");
        h.Feed("You just bought torch for nothing.");     // queue done, nothing out
        h.Feed("You cannot carry that much!");

        Assert.Equal(new[] { "buy dagger", "buy torch" }, h.SentText);
    }

    // Another engine buying in the same shop: its refusal names its own ware, or
    // echoes its own words, and must not skip ours.
    [Fact]
    public void AnotherEnginesRefusal_DoesNotSkipAWare()
    {
        using Harness h = new();
        h.Map("dagger", 1, buy: true, max: 5);
        h.Map("torch", 2, buy: true, max: 5);
        h.Map("scroll of light", 3, buy: false, max: 0);

        h.ShopList(("dagger", 50), ("torch", 50), ("scroll of light", 0));
        Assert.Equal(new[] { "buy dagger" }, h.SentText);

        h.Feed("You cannot buy scroll of light here!");
        h.Feed("You cannot buy an item nobody mapped here!");
        h.Feed("scroll is not a known item.");
        Assert.Equal(new[] { "buy dagger" }, h.SentText);

        h.Feed("You cannot buy dagger here!");            // ours
        Assert.Equal(new[] { "buy dagger", "buy torch" }, h.SentText);
    }

    // A `buy` that landed outside a shop: nothing left in the queue can be bought
    // from here, so none of it is sent, whatever arrives afterwards.
    [Fact]
    public void NotInAShop_DropsTheQueue()
    {
        using Harness h = new();
        h.Map("dagger", 1, buy: true, max: 5);
        h.Map("torch", 2, buy: true, max: 5);

        h.ShopList(("dagger", 50), ("torch", 50));
        h.Feed("You cannot BUY if you are not in a shop!");
        h.Feed("You cannot carry that much!");
        h.Feed("You cannot afford dagger.");
        h.Feed("You cannot buy dagger here!");

        Assert.Equal(new[] { "buy dagger" }, h.SentText);
    }

    [Fact]
    public void LeavingTheRoom_DropsTheQueue()
    {
        using Harness h = new();
        h.Map("dagger", 1, buy: true, max: 5);
        h.Map("torch", 2, buy: true, max: 5);

        h.ShopList(("dagger", 50), ("torch", 50));
        h.Buy.OnRoomChanged();
        h.Feed("You cannot carry that much!");
        h.Feed("You just bought dagger for nothing.");

        Assert.Equal(new[] { "buy dagger" }, h.SentText);

        // The next shop's readout starts a fresh queue.
        h.ShopList(("torch", 50));
        Assert.Equal(new[] { "buy dagger", "buy torch" }, h.SentText);
    }

    // Paradigm sends one counted buy; an unknown-ware reply echoes what was typed.
    [Fact]
    public void Paradigm_UnknownWareEcho_MatchesTheCountedCommand()
    {
        using Harness h = new() { Paradigm = true };
        h.Map("dagger", 1, buy: true, max: 3);
        h.Map("torch", 2, buy: true, max: 1);

        h.ShopList(("dagger", 50), ("torch", 50));
        Assert.Equal(new[] { "buy 3 dagger" }, h.SentText);

        h.Feed("3 dagger is not a known item.");
        Assert.Equal(new[] { "buy 3 dagger", "buy torch" }, h.SentText);
    }

    [Fact]
    public void UnflaggedItem_NoBuy()
    {
        using Harness h = new();
        h.Map("dagger", 1, buy: false, max: 5);

        h.ShopList(("dagger", 50));

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void DisabledMaster_NoBuy()
    {
        using Harness h = new() { Enabled = false };
        h.Map("dagger", 1, buy: true, max: 5);

        h.ShopList(("dagger", 50));

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void FreshList_ResetsPump()
    {
        using Harness h = new();
        h.Map("dagger", 1, buy: true, max: 5);

        h.ShopList(("dagger", 50));    // sends buy #1
        h.ShopList(("dagger", 50));    // fresh list supersedes the in-flight pump

        // Each readout starts its own pump from the current state; the second
        // replaces the first rather than stacking a second queue.
        Assert.Equal(new[] { "buy dagger", "buy dagger" }, h.SentText);
    }
}
