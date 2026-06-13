using System.Reflection;
using FujinTerm.Game;
using FujinTerm.Game.Inventory;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Phase 9 PR 9.1 (cash slice) — <see cref="InventoryManager"/> parses the
/// full <c>i</c> dump into a currency + numeric-encumbrance snapshot and
/// patches it incrementally on coin pickups / drops / bank deposits /
/// withdrawals. Currency ratios are MajorMUD-faithful (1 silver = 10 copper,
/// 1 gold = 100, 1 platinum = 10000, 1 runic = 1000000) and coin weight
/// follows the 3-coins-per-encumbrance-unit rule.
/// </summary>
public sealed class InventoryManagerTests
{
    private sealed class Harness : IDisposable
    {
        public InventoryManager Inv { get; }
        public LineExtractor Lines { get; }
        public int ChangedCount { get; private set; }

        public Harness()
        {
            Inv = new InventoryManager();
            Lines = new LineExtractor(new TerminalEmulator(80, 24));
            Inv.AttachLineExtractor(Lines);
            Inv.Changed += () => ChangedCount++;
        }

        public void Feed(string text)
        {
            FieldInfo? field = typeof(LineExtractor).GetField(
                "LineEmitted", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(Lines) is Action<LineExtractor.EmittedLine> handler)
            {
                handler(new LineExtractor.EmittedLine(
                    text, Array.Empty<CellAttributes>(),
                    DateTimeOffset.UtcNow, IsPromptLine: false));
            }
        }

        public void Dispose() => Inv.Dispose();
    }

    // A representative full 'i' dump: carried currency + Wealth + Encumbrance.
    private static void FeedFullInventory(Harness h)
    {
        h.Feed("You are carrying 2 runic coins, 6 platinum pieces, 94 gold crowns, "
             + "2 silver nobles, 5 copper farthings.");
        h.Feed("You have no keys.");
        h.Feed("Wealth:    2069425 copper farthings");
        h.Feed("Encumbrance:    36/2880  -  Light  [1%]");
    }

    [Fact]
    public void FullParse_PopulatesCurrencyAndEncumbrance()
    {
        using Harness h = new();

        FeedFullInventory(h);

        Assert.True(h.Inv.IsLoaded);
        InventorySnapshot snap = h.Inv.Snapshot;

        CurrencyHoldings c = snap.Currency;
        Assert.Equal(2, c.Runic);
        Assert.Equal(6, c.Platinum);
        Assert.Equal(94, c.Gold);
        Assert.Equal(2, c.Silver);
        Assert.Equal(5, c.Copper);
        // Game's literal Wealth line is authoritative.
        Assert.Equal(2069425, c.TotalCopperValue);

        EncumbranceReading e = snap.Encumbrance;
        Assert.Equal(36, e.CurrentWeight);
        Assert.Equal(2880, e.MaxWeight);
        Assert.Equal(1, e.Percentage);
        Assert.Equal(EncumbranceLevel.Light, e.Category);
    }

    [Fact]
    public void FullParse_FiresChanged()
    {
        using Harness h = new();
        FeedFullInventory(h);
        Assert.Equal(1, h.ChangedCount);
    }

    [Fact]
    public void EmptyPurse_BeforeParse_NotLoaded()
    {
        using Harness h = new();
        InventorySnapshot snap = h.Inv.Snapshot;

        Assert.False(h.Inv.IsLoaded);
        Assert.Equal(CurrencyHoldings.Empty, snap.Currency);
        Assert.Equal(System.DateTimeOffset.MinValue, snap.LastUpdated);
    }

    [Fact]
    public void CarryingNothing_StillReadsWealthAndEncumbrance()
    {
        using Harness h = new();

        h.Feed("You are carrying nothing.");
        h.Feed("You have no keys.");
        h.Feed("Wealth:    0 copper farthings");
        h.Feed("Encumbrance:    0/2880  -  None  [0%]");

        Assert.True(h.Inv.IsLoaded);
        InventorySnapshot snap = h.Inv.Snapshot;
        Assert.Equal(0, snap.Currency.TotalCopperValue);
        Assert.Equal(0, snap.Currency.TotalCoinCount);
        Assert.Equal(EncumbranceLevel.None, snap.Encumbrance.Category);
    }

    [Fact]
    public void PickupCurrency_AddsCoinsAndWealth()
    {
        using Harness h = new();
        // Start from a clean zero-coin / zero-weight baseline.
        h.Feed("You are carrying nothing.");
        h.Feed("Wealth:    0 copper farthings");
        h.Feed("Encumbrance:    0/2880  -  None  [0%]");

        h.Feed("You picked up 30 gold crowns.");

        CurrencyHoldings c = h.Inv.Snapshot.Currency;
        Assert.Equal(30, c.Gold);
        Assert.Equal(3000, c.TotalCopperValue);       // 30 * 100
        Assert.Equal(30, c.TotalCoinCount);
        // 30 coins / 3 = 10 encumbrance units.
        Assert.Equal(10, h.Inv.Snapshot.Encumbrance.CurrentWeight);
    }

    [Fact]
    public void DropCurrency_RemovesCoinsClampedAtZero()
    {
        using Harness h = new();
        h.Feed("You are carrying 10 gold crowns.");
        h.Feed("Wealth:    1000 copper farthings");
        h.Feed("Encumbrance:    3/2880  -  None  [0%]");

        h.Feed("You dropped 25 gold crowns.");   // more than held → clamps

        CurrencyHoldings c = h.Inv.Snapshot.Currency;
        Assert.Equal(0, c.Gold);
        Assert.Equal(0, c.TotalCopperValue);
    }

    [Fact]
    public void PickupTwoCoins_NoWeightChangeBelowThreshold()
    {
        using Harness h = new();
        h.Feed("You are carrying nothing.");
        h.Feed("Wealth:    0 copper farthings");
        h.Feed("Encumbrance:    0/2880  -  None  [0%]");

        // 2 coins / 3 = 0 encumbrance units — weight stays put.
        h.Feed("You picked up 2 copper farthings.");

        Assert.Equal(2, h.Inv.Snapshot.Currency.Copper);
        Assert.Equal(0, h.Inv.Snapshot.Encumbrance.CurrentWeight);
    }

    [Fact]
    public void Deposit_ConsolidatesRemainingCoins()
    {
        using Harness h = new();
        // 1 gold crown = 100 copper. Carry 5 gold (500 copper).
        h.Feed("You are carrying 5 gold crowns.");
        h.Feed("Wealth:    500 copper farthings");
        h.Feed("Encumbrance:    1/2880  -  None  [0%]");

        // Deposit 300 copper worth — leaves 200 copper, re-decomposed greedily
        // into 2 gold crowns.
        h.Feed("You deposit 3 gold crowns.");

        CurrencyHoldings c = h.Inv.Snapshot.Currency;
        Assert.Equal(200, c.TotalCopperValue);
        Assert.Equal(2, c.Gold);
        Assert.Equal(0, c.Silver);
        Assert.Equal(0, c.Copper);
    }

    [Fact]
    public void Withdraw_AddsAndConsolidates()
    {
        using Harness h = new();
        h.Feed("You are carrying nothing.");
        h.Feed("Wealth:    0 copper farthings");
        h.Feed("Encumbrance:    0/2880  -  None  [0%]");

        // Withdraw a multi-denomination amount: 1 platinum (10000) + 5 gold (500)
        // = 10500 copper → greedy 1 platinum, 5 gold.
        h.Feed("You withdrew 1 platinum piece, 5 gold crowns.");

        CurrencyHoldings c = h.Inv.Snapshot.Currency;
        Assert.Equal(10500, c.TotalCopperValue);
        Assert.Equal(1, c.Platinum);
        Assert.Equal(5, c.Gold);
    }

    [Fact]
    public void Deposit_WrapMerge_StitchesSplitLine()
    {
        using Harness h = new();
        h.Feed("You are carrying 1 platinum piece, 5 gold crowns.");
        h.Feed("Wealth:    10500 copper farthings");
        h.Feed("Encumbrance:    2/2880  -  None  [0%]");

        // The MUD wraps the long deposit echo across two rows: first half has
        // no trailing period, second half completes it.
        h.Feed("You deposit 1 platinum piece, 5 gold crowns");
        h.Feed(".");

        // Merged into "You deposit 1 platinum piece, 5 gold crowns." → the full
        // 10500-copper purse is deposited away.
        Assert.Equal(0, h.Inv.Snapshot.Currency.TotalCopperValue);
    }

    [Fact]
    public void Buy_ExactDenoms_DeductsPerCoinKeepingMix()
    {
        using Harness h = new();
        // Hold a mix; buy for 3 gold + 5 copper, both of which we hold exactly.
        h.Feed("You are carrying 2 platinum pieces, 10 gold crowns, 9 copper farthings.");
        h.Feed("Wealth:    21009 copper farthings");
        h.Feed("Encumbrance:    7/2880  -  None  [0%]");

        h.Feed("You bought broadsword for 3 gold crowns, 5 copper farthings.");

        CurrencyHoldings c = h.Inv.Snapshot.Currency;
        // Per-coin subtract, no change broken: platinum untouched, gold 10→7,
        // copper 9→4. Mix preserved.
        Assert.Equal(2, c.Platinum);
        Assert.Equal(7, c.Gold);
        Assert.Equal(4, c.Copper);
        Assert.Equal(20704, c.TotalCopperValue);   // 21009 - 305
    }

    [Fact]
    public void Buy_ForcedBreak_ConsolidatesWholePurse()
    {
        using Harness h = new();
        // Hold only 1 platinum (10000). Buy for 3 gold (300) — we lack the gold,
        // so the game breaks the platinum and hands back consolidated change.
        h.Feed("You are carrying 1 platinum piece.");
        h.Feed("Wealth:    10000 copper farthings");
        h.Feed("Encumbrance:    0/2880  -  None  [0%]");

        h.Feed("You just bought broadsword for 3 gold crowns.");

        CurrencyHoldings c = h.Inv.Snapshot.Currency;
        // 10000 - 300 = 9700 → greedy: 97 gold crowns, nothing else.
        Assert.Equal(9700, c.TotalCopperValue);
        Assert.Equal(0, c.Platinum);
        Assert.Equal(97, c.Gold);
        Assert.Equal(0, c.Silver);
        Assert.Equal(0, c.Copper);
    }

    [Fact]
    public void Sell_AddsConsolidatedChange()
    {
        using Harness h = new();
        h.Feed("You are carrying 5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    1/2880  -  None  [0%]");

        h.Feed("You sold lantern for 101 copper farthings.");

        CurrencyHoldings c = h.Inv.Snapshot.Currency;
        // 5 + 101 = 106 → greedy: 1 gold, 0 silver, 6 copper.
        Assert.Equal(106, c.TotalCopperValue);
        Assert.Equal(1, c.Gold);
        Assert.Equal(0, c.Silver);
        Assert.Equal(6, c.Copper);
    }

    [Fact]
    public void Buy_ForNothing_NoChange()
    {
        using Harness h = new();
        h.Feed("You are carrying 10 gold crowns.");
        h.Feed("Wealth:    1000 copper farthings");
        h.Feed("Encumbrance:    3/2880  -  None  [0%]");

        h.Feed("You just bought 2 torch for nothing.");

        Assert.Equal(1000, h.Inv.Snapshot.Currency.TotalCopperValue);
        Assert.Equal(10, h.Inv.Snapshot.Currency.Gold);
    }

    [Fact]
    public void MarkStale_KeepsDataDropsLoadedFlag()
    {
        using Harness h = new();
        FeedFullInventory(h);
        Assert.True(h.Inv.IsLoaded);

        h.Inv.MarkStale();

        Assert.False(h.Inv.IsLoaded);
        // Data survives — wealth still reads from the last parse.
        Assert.Equal(2069425, h.Inv.Snapshot.Currency.TotalCopperValue);
    }

    [Fact]
    public void FullParse_OverridesIncrementalDrift()
    {
        using Harness h = new();
        h.Feed("You are carrying nothing.");
        h.Feed("Wealth:    0 copper farthings");
        h.Feed("Encumbrance:    0/2880  -  None  [0%]");
        h.Feed("You picked up 30 gold crowns.");

        // A fresh 'i' dump re-bases everything from the game's own numbers.
        h.Feed("You are carrying 99 platinum pieces.");
        h.Feed("Wealth:    990000 copper farthings");
        h.Feed("Encumbrance:    33/2880  -  Light  [1%]");

        CurrencyHoldings c = h.Inv.Snapshot.Currency;
        Assert.Equal(0, c.Gold);
        Assert.Equal(99, c.Platinum);
        Assert.Equal(990000, c.TotalCopperValue);
    }
}
