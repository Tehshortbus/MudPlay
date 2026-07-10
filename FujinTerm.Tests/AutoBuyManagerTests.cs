using System.Collections.Generic;
using System.Linq;
using System.Text;
using FujinTerm.Game.Inventory;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

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
        public LineExtractor Lines { get; } = new(new TerminalEmulator(80, 24));
        public List<byte[]> Sent { get; } = new();
        public bool Enabled { get; set; } = true;

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
                log: Log);
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

        // Shop-list capture rides the LineExtractor stream, so it must fire the
        // extractor's LineEmitted event (reflection — the backing field is private).
        public void Emit(string line, bool prompt = false)
        {
            System.Reflection.FieldInfo? field = typeof(LineExtractor)
                .GetField("LineEmitted",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic);
            if (field?.GetValue(Lines) is Action<LineExtractor.EmittedLine> handler)
                handler(new LineExtractor.EmittedLine(
                    line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: prompt));
        }

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
