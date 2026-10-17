using MudPlay.Game;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// InventoryManager parses the
// full i dump into a currency + numeric-encumbrance snapshot and
// patches it incrementally on coin pickups / drops / bank deposits /
// withdrawals. Currency ratios are MajorMUD-faithful (1 silver = 10 copper,
// 1 gold = 100, 1 platinum = 10000, 1 runic = 1000000) and coin weight
// follows the 3-coins-per-encumbrance-unit rule.
public sealed class InventoryManagerTests
{
    private sealed class Harness : IDisposable
    {
        public InventoryManager Inv { get; }
        // The game's output, fed the way the client receives it.
        public ServerFeed Server { get; } = new();
        public int ChangedCount { get; private set; }

        public Harness(
            Func<string, int?>? itemWeight = null,
            Func<string, string?>? slotResolver = null,
            Func<string, bool>? isItemRecordName = null,
            Func<string, bool>? isKey = null,
            Func<string, bool>? staysWhenSpent = null)
        {
            Inv = new InventoryManager(
                log: null, itemWeightResolver: itemWeight, slotResolver: slotResolver,
                isItemRecordName: isItemRecordName, isKey: isKey, staysWhenSpent: staysWhenSpent);
            Inv.AttachLineExtractor(Server.Lines);
            Inv.Changed += () => ChangedCount++;
        }

        public void Feed(string text) => Server.Line(text);

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
    public void IsCapturing_TracksTheDumpBlock_ForTheUnrecognizedLineWatcher()
    {
        // The watcher reads IsCapturing to exclude the dump's word-wrapped continuation
        // rows. It must be true from the "You are carrying …" anchor through every row
        // and go false once the terminating "Encumbrance:" line is consumed.
        using Harness h = new();
        Assert.False(h.Inv.IsCapturing);

        h.Feed("You are carrying 8 platinum pieces, 865 gold crowns, crested war helm (Head),");
        Assert.True(h.Inv.IsCapturing);
        h.Feed("light plate leggings (Legs), dwarven work boots (Feet), severed head of Rastep");
        Assert.True(h.Inv.IsCapturing);            // an anchorless continuation row
        h.Feed("You have the following keys: 2 black serpent key, gate key.");
        Assert.True(h.Inv.IsCapturing);
        h.Feed("Wealth: 166500 copper farthings");
        Assert.True(h.Inv.IsCapturing);
        h.Feed("Encumbrance:    36/2880  -  Light  [1%]");
        Assert.False(h.Inv.IsCapturing);           // terminator ends the block
    }

    [Fact]
    public void SoldAndDropped_FireItemEventsWithNameAndCount()
    {
        using Harness h = new();
        (string Name, int Count)? sold = null;
        (string Name, int Count)? dropped = null;
        h.Inv.ItemSold += (n, c, _) => sold = (n, c);
        h.Inv.ItemDropped += (n, c) => dropped = (n, c);

        // Paradigm's counted forms — SplitLeadingCount strips the leading count.
        h.Feed("You sold 5 orc-head for 250 copper farthings.");
        h.Feed("You dropped 3 moonstone.");

        Assert.Equal(("orc-head", 5), sold);
        Assert.Equal(("moonstone", 3), dropped);
    }

    [Fact]
    public void Sold_CarriesTheSalePriceInCopper()
    {
        using Harness h = new();
        long? price = null;
        h.Inv.ItemSold += (_, _, copper) => price = copper;

        h.Feed("You sold 5 orc-head for 250 copper farthings.");

        Assert.Equal(250L, price);
    }

    [Fact]
    public void Took_FiresItemTaken_WithNameAndCount()
    {
        using Harness h = new();
        List<(string Name, int Count)> taken = new();
        h.Inv.ItemTaken += (n, c) => taken.Add((n, c));

        h.Feed("You took rusty dagger.");
        h.Feed("You took 5 orc-head.");                // Paradigm's counted get
        h.Feed("You took 12 damage.");                 // damage, not a get
        h.Feed("You picked up 10 gold crowns");         // coin pickup, not an item

        Assert.Equal(new[] { ("rusty dagger", 1), ("orc-head", 5) }, taken);
    }

    [Fact]
    public void SoldSingular_StockForm_FiresCountOne()
    {
        using Harness h = new();
        (string Name, int Count)? sold = null;
        h.Inv.ItemSold += (n, c, _) => sold = (n, c);

        h.Feed("You sold lantern for 101 copper farthings.");

        Assert.Equal(("lantern", 1), sold);
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
    public void FullInventoryParsed_FiresOnlyForAuthoritativeDump()
    {
        using Harness h = new();
        int fullParseCount = 0;
        h.Inv.FullInventoryParsed += () => fullParseCount++;

        h.Feed("You took war hammer.");
        Assert.Equal(0, fullParseCount);

        FeedFullInventory(h);
        Assert.Equal(1, fullParseCount);
    }

    [Fact]
    public void FullParse_ReadiedLight_ParsedAsLitLightNotCarried()
    {
        using Harness h = new();

        // Exact live capture: a lit lantern lists inline as "(Readied/239)".
        h.Feed("You are carrying 2 platinum pieces, 38 gold crowns, 2 silver nobles, "
             + "8 copper farthings, padded vest (Torso), padded pants (Legs), "
             + "padded helm (Head), padded gloves (Hands), padded boots (Feet), "
             + "lantern (Readied/239), quarterstaff (Two handed), dagger");
        h.Feed("You have no keys.");
        h.Feed("Wealth:    23828 copper farthings");
        h.Feed("Encumbrance:    624/2880  -  Light  [21%]");

        InventorySnapshot snap = h.Inv.Snapshot;
        Assert.NotNull(snap.ReadiedLight);
        ReadiedLight light = snap.ReadiedLight!.Value;
        Assert.Equal("lantern", light.Name);
        Assert.Equal(239, light.Readied);

        // The lit light is not double-counted as a plain carried item; the
        // unworn pack still holds the quarterstaff-less bits (dagger).
        Assert.DoesNotContain(snap.CarriedItems, s => s.Contains("Readied", StringComparison.Ordinal));
        Assert.DoesNotContain(snap.CarriedItems, s => s.Equals("lantern", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("dagger", snap.CarriedItems);
    }

    [Fact]
    public void FullParse_NoReadiedLight_LeavesLightNull()
    {
        using Harness h = new();
        FeedFullInventory(h);
        Assert.Null(h.Inv.Snapshot.ReadiedLight);
    }

    [Fact]
    public void FullParse_WrappedKeysLine_KeepsKeyTailOffCarriedItems()
    {
        using Harness h = new();

        // A long keys ring word-wraps at ~80 cols: the trailing "key." of
        // "large iron key" lands on the next row. That continuation used to be
        // stranded — the last key lost its "key" and the orphaned tail bled into
        // the last carried item ("3 waterskin" → "3 waterskin key").
        h.Feed("You are carrying a mine pass, black keyring, turquoise potion, 3 waterskin.");
        h.Feed("You have the following keys:  2 black star key, 2 black serpent key, large iron");
        h.Feed("key.");
        h.Feed("Wealth:    0 copper farthings");
        h.Feed("Encumbrance:    18/2880  -  None  [0%]");

        InventorySnapshot snap = h.Inv.Snapshot;

        Assert.Contains("3 waterskin", snap.CarriedItems);
        Assert.DoesNotContain(snap.CarriedItems, s => s.EndsWith(" key", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(snap.Keys);
        Assert.Equal(
            new[] { "2 black star key", "2 black serpent key", "large iron key" },
            snap.Keys!);
    }

    [Theory]
    [InlineData("copper", 7, 7L)]
    [InlineData("silver", 3, 30L)]
    [InlineData("gold", 4, 400L)]
    [InlineData("platinum", 2, 20_000L)]
    [InlineData("runic", 1, 1_000_000L)]
    [InlineData("GOLD", 4, 400L)]     // case-insensitive
    [InlineData("doubloons", 9, 0L)]  // unknown denomination → 0
    public void ToCopper_AppliesTheRatioLadder(string currency, long count, long expected)
    {
        Assert.Equal(expected, CurrencyHoldings.ToCopper(currency, count));
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
    public void BuyBeforeFullInventory_SeedsBaselineAndTracksItem()
    {
        // Character creation reaches the starting-gear store before any full `i`
        // (that fires on realm entry), so the manager isn't "loaded". A zero-cost
        // purchase must still land in the carried list — otherwise "Equip all"
        // sees an empty pack (the reported bug). The first buy seeds the baseline.
        using Harness h = new();
        Assert.False(h.Inv.IsLoaded);

        h.Feed("You just bought club for 0 copper farthings.");

        Assert.True(h.Inv.IsLoaded);
        Assert.Contains("club", h.Inv.Snapshot.CarriedItems);
    }

    [Fact]
    public void MultipleBuysBeforeFullInventory_AllTracked()
    {
        // The starting-gear spree: several free buys back to back, none preceded
        // by an `i`. Every one must accumulate in the carried list.
        using Harness h = new();
        h.Feed("You just bought club for 0 copper farthings.");
        h.Feed("You just bought padded vest for 0 copper farthings.");
        h.Feed("You just bought padded helm for 0 copper farthings.");

        System.Collections.Generic.IReadOnlyList<string> carried = h.Inv.Snapshot.CarriedItems;
        Assert.Contains("club", carried);
        Assert.Contains("padded vest", carried);
        Assert.Contains("padded helm", carried);
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
        // The "nothing" carry line must not register a phantom "nothing" item.
        Assert.Empty(snap.CarriedItems);
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

    // Single-coin article form ("a gold crown") and present-tense ("You pick up") were
    // missed by the old digit-only, past-tense-only regex, so a lone-coin loot left
    // @wealth / @enc stale until the next full `i`. Both forms now patch the snapshot.
    [Fact]
    public void PickupSingleCoin_ArticleAndPresentTense_UpdateWealth()
    {
        using Harness h = new();
        h.Feed("You are carrying nothing.");
        h.Feed("Wealth:    0 copper farthings");
        h.Feed("Encumbrance:    0/2880  -  None  [0%]");

        h.Feed("You picked up a gold crown.");        // single coin, article form
        Assert.Equal(1, h.Inv.Snapshot.Currency.Gold);

        h.Feed("You pick up 2 silver nobles.");         // present tense, counted
        Assert.Equal(2, h.Inv.Snapshot.Currency.Silver);

        h.Feed("You pick up a copper farthing.");       // present tense, single coin
        Assert.Equal(1, h.Inv.Snapshot.Currency.Copper);
    }

    // A BBS can rename the runic word (e.g. "quatloos"), but the coin noun stays.
    // Parsing is noun-keyed, so the renamed leading word still lands as Runic —
    // no CurrencyNaming injection needed on the parser.
    [Fact]
    public void PickupRenamedRunic_LandsAsRunicByNoun()
    {
        using Harness h = new();
        h.Feed("You are carrying nothing.");
        h.Feed("Wealth:    0 copper farthings");
        h.Feed("Encumbrance:    0/2880  -  None  [0%]");

        h.Feed("You picked up 6 quatloos coins.");

        CurrencyHoldings c = h.Inv.Snapshot.Currency;
        Assert.Equal(6, c.Runic);
        Assert.Equal(6_000_000, c.TotalCopperValue);   // 6 * 1_000_000
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
    public void HideCurrency_DecrementsHoldingsLikeADrop()
    {
        using Harness h = new();
        h.Feed("You are carrying 50 gold crowns, 6 copper farthings.");
        h.Feed("Wealth:    5006 copper farthings");
        h.Feed("Encumbrance:    18/2880  -  None  [0%]");

        // Stashing coins removes them from the purse exactly like a drop.
        // Without this the snapshot stays stale and the next auto-stash
        // computes its `hide` amounts from pre-stash holdings.
        h.Feed("You hid 50 gold crowns.");

        CurrencyHoldings c = h.Inv.Snapshot.Currency;
        Assert.Equal(0, c.Gold);
        Assert.Equal(6, c.Copper);
        Assert.Equal(6, c.TotalCopperValue);
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
    public void Deposit_FiresBankDeposited_WithCopperValue()
    {
        using Harness h = new();
        h.Feed("You are carrying 5 gold crowns.");
        h.Feed("Wealth:    500 copper farthings");
        h.Feed("Encumbrance:    1/2880  -  None  [0%]");

        long deposited = 0;
        h.Inv.BankDeposited += copper => deposited += copper;

        // Manual or auto, the echo carries the full deposited copper: 3 gold = 300.
        h.Feed("You deposit 3 gold crowns.");

        Assert.Equal(300, deposited);
    }

    [Fact]
    public void ItemHide_FiresItemHidden_WithName()
    {
        using Harness h = new();
        List<string> hidden = new();
        h.Inv.ItemHidden += hidden.Add;

        h.Feed("You hid a torch.");

        Assert.Equal(new[] { "a torch" }, hidden);
    }

    // An item merely named after a coin is echoed by its bare name, with no amount
    // in front: it is an item on the hide path as it is on the drop path. Paradigm's
    // counted echo of one names no coin metal either.
    [Theory]
    [InlineData("iron crown")]
    [InlineData("storm crown")]
    [InlineData("fiery crown")]
    [InlineData("woven ivy crown")]
    [InlineData("sack of coins")]
    [InlineData("3 iron crown")]
    [InlineData("2 woven ivy crown")]
    [InlineData("4 sack of coins")]
    public void ItemNamedAfterACoin_Hidden_FiresItemHidden(string echo)
    {
        using Harness h = new();
        List<string> hidden = new();
        h.Inv.ItemHidden += hidden.Add;

        h.Feed($"You hid {echo}.");

        Assert.Equal(new[] { echo }, hidden);
    }

    [Fact]
    public void CoinHide_DoesNotFireItemHidden()
    {
        using Harness h = new();
        h.Feed("You are carrying 50 gold crowns, 6 copper farthings.");
        h.Feed("Wealth:    5006 copper farthings");
        h.Feed("Encumbrance:    18/2880  -  None  [0%]");

        List<string> hidden = new();
        h.Inv.ItemHidden += hidden.Add;

        // Plural (caught by the coin-hide regex) and singular "a <coin>" (which the
        // coin regex's leading digit misses) must both stay off the item path —
        // CashManager's coin channel records coins, so a duplicate item row here
        // would double-log the stash.
        h.Feed("You hid 50 gold crowns.");
        h.Feed("You hid a gold piece.");

        Assert.Empty(hidden);
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
    public void NoteAutoDeposit_DecrementsPurseImmediately()
    {
        using Harness h = new();
        h.Feed("You are carrying 5 gold crowns.");   // 500 copper
        h.Feed("Wealth:    500 copper farthings");
        h.Feed("Encumbrance:    1/2880  -  None  [0%]");

        // The reroute dispatches `dep 300` and tells the tracker at once, before
        // the server's echo lands — the return-leg router must see the drained
        // purse right away or it will route through a toll it can't afford.
        h.Inv.NoteAutoDeposit(300);

        Assert.Equal(200, h.Inv.Snapshot.Currency.TotalCopperValue);
        Assert.Equal(2, h.Inv.Snapshot.Currency.Gold);
    }

    [Fact]
    public void NoteAutoDeposit_ThenEcho_DoesNotDoubleCount()
    {
        using Harness h = new();
        h.Feed("You are carrying 5 gold crowns.");   // 500 copper
        h.Feed("Wealth:    500 copper farthings");
        h.Feed("Encumbrance:    1/2880  -  None  [0%]");

        h.Inv.NoteAutoDeposit(300);                  // optimistic → 200
        h.Feed("You deposit 3 gold crowns.");        // echo of the SAME deposit

        // The echo reconciles against the pending amount instead of subtracting
        // again — the purse stays at 200, not 0.
        Assert.Equal(200, h.Inv.Snapshot.Currency.TotalCopperValue);
        Assert.Equal(2, h.Inv.Snapshot.Currency.Gold);
    }

    [Fact]
    public void FullInventory_ClearsPendingAutoDeposit()
    {
        using Harness h = new();
        h.Feed("You are carrying 5 gold crowns.");   // 500 copper
        h.Feed("Wealth:    500 copper farthings");
        h.Feed("Encumbrance:    1/2880  -  None  [0%]");

        h.Inv.NoteAutoDeposit(300);                  // pending = 300, purse 200

        // A fresh 'i' dump re-bases the purse authoritatively and drops any
        // still-pending optimistic deposit; a genuinely later deposit must then
        // subtract in full rather than being swallowed as an unseen echo.
        h.Feed("You are carrying 2 gold crowns.");
        h.Feed("Wealth:    200 copper farthings");
        h.Feed("Encumbrance:    1/2880  -  None  [0%]");

        h.Feed("You deposit 2 gold crowns.");        // a new, real deposit

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
    public void FullParse_HarvestsWornItemsWithSlots()
    {
        using Harness h = new();

        // Worn items carry a trailing "(Slot)"; carried-but-unworn don't.
        h.Feed("You are carrying padded vest (Torso), padded pants (Legs), "
             + "padded gloves (Hands), quarterstaff (Two handed), padded helm, "
             + "padded boots, 5 copper farthings.");
        h.Feed("You have no keys.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    20/2880  -  None  [0%]");

        IReadOnlyList<EquippedItem> eq = h.Inv.Snapshot.EquippedItems;
        Assert.Equal(4, eq.Count);
        Assert.Contains(new EquippedItem("padded vest", "Torso"), eq);
        Assert.Contains(new EquippedItem("padded pants", "Legs"), eq);
        Assert.Contains(new EquippedItem("padded gloves", "Hands"), eq);
        // "Two handed" normalizes to "Weapon Hand".
        Assert.Contains(new EquippedItem("quarterstaff", "Weapon Hand"), eq);
        // Unworn items + currency are not equipped.
        Assert.DoesNotContain(eq, i => i.Name == "padded helm");
        Assert.DoesNotContain(eq, i => i.Name == "padded boots");
    }

    [Fact]
    public void FullParse_NoWornItems_EmptyEquippedList()
    {
        using Harness h = new();
        FeedFullInventory(h);
        Assert.Empty(h.Inv.Snapshot.EquippedItems);
    }

    [Fact]
    public void FullParse_HarvestsCarriedUnwornItems()
    {
        using Harness h = new();

        // padded vest is worn (slot suffix); padded helm / padded boots are
        // carried-but-unworn; the trailing coins are currency.
        h.Feed("You are carrying padded vest (Torso), padded helm, padded boots, "
             + "5 copper farthings.");
        h.Feed("You have no keys.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    20/2880  -  None  [0%]");

        IReadOnlyList<string> carried = h.Inv.Snapshot.CarriedItems;
        Assert.Contains("padded helm", carried);
        Assert.Contains("padded boots", carried);
        // Worn items + currency stay out of the carried list.
        Assert.DoesNotContain("padded vest", carried);
        Assert.DoesNotContain(carried, c => c.Contains("copper"));
    }

    [Fact]
    public void FullParse_NoCarriedItems_EmptyCarriedList()
    {
        using Harness h = new();
        // FeedFullInventory carries only currency — no unworn items.
        FeedFullInventory(h);
        Assert.Empty(h.Inv.Snapshot.CarriedItems);
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

    // ----- incremental equip / remove ----------------------------------

    // A baseline 'i' dump that includes a worn weapon + gloves, so the patches
    // below have a real loadout to edit.
    private static void FeedEquippedBaseline(Harness h)
    {
        h.Feed("You are carrying quarterstaff (Weapon Hand), padded gloves (Hands), "
             + "5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    50/2880  -  Light  [2%]");
    }

    private static IReadOnlyList<EquippedItem> Worn(Harness h) => h.Inv.Snapshot.EquippedItems;

    [Fact]
    public void Equip_WeaponSwap_ReplacesWeaponHand()
    {
        using Harness h = new();
        FeedEquippedBaseline(h);

        h.Feed("You are now holding dagger.");

        IReadOnlyList<EquippedItem> worn = Worn(h);
        Assert.Single(worn, e => e.Slot == "Weapon Hand");
        Assert.Contains(worn, e => e is { Name: "dagger", Slot: "Weapon Hand" });
        Assert.DoesNotContain(worn, e => e.Name == "quarterstaff");
        // gloves untouched
        Assert.Contains(worn, e => e.Name == "padded gloves");
    }

    [Fact]
    public void Equip_WeaponSwap_ReturnsDisplacedWeaponToCarried()
    {
        using Harness h = new();
        // Worn quarterstaff + a carried dagger, mirroring the reported scenario.
        h.Feed("You are carrying quarterstaff (Weapon Hand), dagger, 5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    60/2880  -  Light  [2%]");

        // Wielding the dagger vacates the hand: the dagger leaves the pack for the
        // hand and the displaced quarterstaff returns to the pack (not vanishes).
        h.Feed("You are now holding dagger.");

        Assert.Contains(Worn(h), e => e is { Name: "dagger", Slot: "Weapon Hand" });
        Assert.DoesNotContain(Worn(h), e => e.Name == "quarterstaff");
        Assert.Contains("quarterstaff", Carried(h));
        Assert.DoesNotContain("dagger", Carried(h));
    }

    [Fact]
    public void Equip_WeaponSwapBack_MovesBothWeaponsCorrectly()
    {
        using Harness h = new();
        h.Feed("You are carrying quarterstaff (Weapon Hand), dagger, 5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    60/2880  -  Light  [2%]");

        h.Feed("You are now holding dagger.");        // quarterstaff → pack
        h.Feed("You are now holding quarterstaff.");  // dagger → pack, quarterstaff → hand

        Assert.Single(Worn(h), e => e.Slot == "Weapon Hand");
        Assert.Contains(Worn(h), e => e is { Name: "quarterstaff", Slot: "Weapon Hand" });
        Assert.Contains("dagger", Carried(h));
        Assert.DoesNotContain("quarterstaff", Carried(h));
    }

    [Fact]
    public void Equip_PairedRingSwap_PreservesPhysicalSlotOrder()
    {
        // `eq <ring>` into a full finger pair prints remove-then-wear (the game evicts
        // slot 1 and wears the new ring there). The snapshot must keep the new ring in
        // that freed position, NOT append it at the end — appending inverts the finger
        // order the paired-slot gear composer reads, which made it emit a redundant
        // `rem` on the next swap (report paradigm-20260909-074126).
        using Harness h = new(slotResolver: name =>
            name.EndsWith("ring", StringComparison.OrdinalIgnoreCase) ? "Finger" : null);
        h.Feed("You are carrying diamond-studded ring (Finger), gold jeweled ring (Finger), "
             + "5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    50/2880  -  Light  [2%]");

        h.Feed("You have removed diamond-studded ring.");   // eq pearl evicts slot 1
        h.Feed("You are now wearing pearl ring.");

        var fingers = new List<string>();
        foreach (EquippedItem e in Worn(h))
            if (e.Slot == "Finger") fingers.Add(e.Name);
        // pearl took diamond's slot-1 spot → [pearl, gold], not [gold, pearl].
        Assert.Equal(new[] { "pearl ring", "gold jeweled ring" }, fingers);
    }

    [Fact]
    public void Equip_SameWeaponReconfirmed_DoesNotDuplicateIntoCarried()
    {
        using Harness h = new();
        FeedEquippedBaseline(h);   // quarterstaff already in hand

        // Re-confirming the same weapon must not shove a phantom copy into the pack.
        h.Feed("You are now holding quarterstaff.");

        Assert.Single(Worn(h), e => e.Slot == "Weapon Hand");
        Assert.Contains(Worn(h), e => e is { Name: "quarterstaff", Slot: "Weapon Hand" });
        Assert.DoesNotContain("quarterstaff", Carried(h));
    }

    [Fact]
    public void Remove_WeaponReadied_ClearsWeaponHand()
    {
        using Harness h = new();
        FeedEquippedBaseline(h);

        h.Feed("You now have no weapon readied.");

        IReadOnlyList<EquippedItem> worn = Worn(h);
        Assert.DoesNotContain(worn, e => e.Slot == "Weapon Hand");
        Assert.Contains(worn, e => e.Name == "padded gloves");
    }

    [Fact]
    public void Remove_Armor_DropsItemByName()
    {
        using Harness h = new();
        FeedEquippedBaseline(h);

        h.Feed("You have removed padded gloves.");

        Assert.DoesNotContain(Worn(h), e => e.Name == "padded gloves");
    }

    [Fact]
    public void Equip_ArmorSwap_RemovesOldThenWearsNew()
    {
        using Harness h = new();
        FeedEquippedBaseline(h);

        // Wearing into an occupied slot prints the removal first, then the wear.
        h.Feed("You have removed padded gloves.");
        h.Feed("You are now wearing cotton gloves.");

        IReadOnlyList<EquippedItem> worn = Worn(h);
        Assert.DoesNotContain(worn, e => e.Name == "padded gloves");
        Assert.Contains(worn, e => e.Name == "cotton gloves");
    }

    [Fact]
    public void Equip_BeforeBaseline_IsIgnored()
    {
        using Harness h = new();

        // No 'i' parsed yet — patching an empty set would misrepresent the
        // loadout, so the line is consumed but not applied.
        h.Feed("You are now holding dagger.");

        Assert.False(h.Inv.IsLoaded);
        Assert.Empty(Worn(h));
    }

    // ----- incremental carried-item get / drop / buy / sell ------------

    // A baseline 'i' dump with one worn item (padded gloves) and one carried-
    // but-unworn item (lantern), so the patches below have a real pack to edit.
    private static void FeedCarriedBaseline(Harness h)
    {
        h.Feed("You are carrying padded gloves (Hands), lantern, 5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    50/2880  -  Light  [2%]");
    }

    private static IReadOnlyList<string> Carried(Harness h) => h.Inv.Snapshot.CarriedItems;

    [Fact]
    public void Get_AddsItemToCarried()
    {
        using Harness h = new();
        FeedCarriedBaseline(h);

        // MajorMUD's item-get confirmation is "You took X."
        h.Feed("You took rusty dagger.");

        Assert.Contains("rusty dagger", Carried(h));
        Assert.Contains("lantern", Carried(h));   // baseline item untouched
    }

    // MajorMUD's actual drop confirmation is past tense ("You dropped X.").
    [Fact]
    public void Drop_RemovesItemFromCarried()
    {
        using Harness h = new();
        FeedCarriedBaseline(h);

        h.Feed("You dropped lantern.");

        Assert.DoesNotContain("lantern", Carried(h));
    }

    // The present-tense phrasing is tolerated too, in case a realm uses it.
    [Fact]
    public void Drop_PresentTense_RemovesItemFromCarried()
    {
        using Harness h = new();
        FeedCarriedBaseline(h);

        h.Feed("You drop lantern.");

        Assert.DoesNotContain("lantern", Carried(h));
    }

    [Fact]
    public void Buy_AddsItemToCarried()
    {
        using Harness h = new();
        FeedCarriedBaseline(h);

        h.Feed("You bought torch for 5 copper farthings.");

        Assert.Contains("torch", Carried(h));
    }

    [Fact]
    public void Sell_RemovesItemFromCarried()
    {
        using Harness h = new();
        FeedCarriedBaseline(h);

        h.Feed("You sold lantern for 3 copper farthings.");

        Assert.DoesNotContain("lantern", Carried(h));
    }

    [Fact]
    public void Equip_MovesItemOutOfCarried()
    {
        using Harness h = new();
        FeedCarriedBaseline(h);

        // Wielding a carried weapon moves it from the pack to the hand — it must
        // not linger in both lists.
        h.Feed("You are now holding lantern.");

        Assert.DoesNotContain("lantern", Carried(h));
        Assert.Contains(Worn(h), e => e is { Name: "lantern", Slot: "Weapon Hand" });
    }

    [Fact]
    public void Wear_MovesItemOutOfCarried()
    {
        using Harness h = new();
        FeedCarriedBaseline(h);

        h.Feed("You are now wearing lantern.");

        Assert.DoesNotContain("lantern", Carried(h));
    }

    [Fact]
    public void Remove_MovesWornItemIntoCarried()
    {
        using Harness h = new();
        FeedCarriedBaseline(h);

        // A removed worn piece returns to the pack as carried-but-unworn.
        h.Feed("You have removed padded gloves.");

        Assert.Contains("padded gloves", Carried(h));
        Assert.DoesNotContain(Worn(h), e => e.Name == "padded gloves");
    }

    // A wear, wield or remove line moves one piece between the pack and the worn
    // set. Listeners count the two together (a keep amount, a copy to sell), so
    // they are told once, with the piece in exactly one of them: a notice half-way
    // would show it in both, or in neither.
    [Theory]
    [InlineData("padded gloves (Hands), lantern", "You are now wearing lantern.", "lantern")]
    [InlineData("padded gloves (Hands), lantern", "You are now holding lantern.", "lantern")]
    [InlineData("padded gloves (Hands), lantern", "You have removed padded gloves.", "padded gloves")]
    [InlineData("lantern (Weapon Hand), padded gloves", "You now have no weapon readied.", "lantern")]
    // A swap: the displaced weapon and the new one each change sides in one notice.
    [InlineData("quarterstaff (Weapon Hand), lantern", "You are now holding lantern.", "lantern")]
    [InlineData("quarterstaff (Weapon Hand), lantern", "You are now holding lantern.", "quarterstaff")]
    public void MoveBetweenPackAndWorn_IsOneNotice_WithThePieceHeldOnce(string carrying, string line, string piece)
    {
        using Harness h = new();
        h.Feed($"You are carrying {carrying}, 5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    50/2880  -  Light  [2%]");
        List<int> heldAtEachNotice = new();
        h.Inv.Changed += () => heldAtEachNotice.Add(
            Carried(h).Count(c => c == piece) + Worn(h).Count(e => e.Name == piece));
        int before = h.ChangedCount;

        h.Feed(line);

        Assert.Equal(1, h.ChangedCount - before);
        Assert.Equal(new[] { 1 }, heldAtEachNotice);
    }

    // The listing sets the lit light apart from the pack, and only a full read used
    // to clear it. A copy that leaves with no pack entry to take it from was the lit
    // one: left listed, it was counted as held after it had gone.
    [Theory]
    [InlineData("You dropped torch.")]
    [InlineData("You hid torch.")]
    [InlineData("You sold torch for 2 copper farthings.")]
    public void LitLightLeaving_WithNoPackCopy_IsNoLongerListedAsLit(string line)
    {
        using Harness h = new();
        h.Feed("You are carrying torch (Readied/200), 5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    50/2880  -  Light  [2%]");
        Assert.Equal("torch", h.Inv.Snapshot.ReadiedLight?.Name);

        h.Feed(line);

        Assert.Null(h.Inv.Snapshot.ReadiedLight);
        Assert.DoesNotContain("torch", Carried(h));
    }

    // With a spare in the pack the client can't tell which copy the game took. The
    // spare is taken off first and the light stays listed: one torch held either way.
    [Fact]
    public void LitLightAndASpare_OneLeaving_TakesTheSpareFirst()
    {
        using Harness h = new();
        h.Feed("You are carrying torch (Readied/200), torch, 5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    50/2880  -  Light  [2%]");

        h.Feed("You dropped torch.");
        Assert.Equal("torch", h.Inv.Snapshot.ReadiedLight?.Name);
        Assert.DoesNotContain("torch", Carried(h));

        h.Feed("You dropped torch.");
        Assert.Null(h.Inv.Snapshot.ReadiedLight);
    }

    // Stock's line for taking off the lit light. The plain removal pattern read the
    // item as "torch and extinguished it" and left the light listed as lit.
    [Fact]
    public void RemovingTheLitLight_PutsItBackInThePack_InOneNotice()
    {
        using Harness h = new();
        h.Feed("You are carrying torch (Readied/200), 5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    50/2880  -  Light  [2%]");
        int before = h.ChangedCount;

        h.Feed("You have removed torch and extinguished it.");

        Assert.Null(h.Inv.Snapshot.ReadiedLight);
        Assert.Equal(new[] { "torch" }, Carried(h));
        Assert.Equal(1, h.ChangedCount - before);
    }

    // A light lit since the last full read was never listed apart: its pack entry
    // is still there, and taking it off adds no second one.
    [Fact]
    public void RemovingALight_LitSinceTheLastRead_AddsNoSecondPackEntry()
    {
        using Harness h = new();
        h.Feed("You are carrying torch, 5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    50/2880  -  Light  [2%]");

        h.Feed("You have removed torch and extinguished it.");

        Assert.Equal(new[] { "torch" }, Carried(h));
    }

    // A plain removal line naming the light listed as lit, with nothing worn by
    // that name: the light is what came off, and it is not counted twice.
    [Fact]
    public void PlainRemovalOfTheLitLight_DoesNotListItTwice()
    {
        using Harness h = new();
        h.Feed("You are carrying torch (Readied/200), 5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    50/2880  -  Light  [2%]");

        h.Feed("You have removed torch.");

        Assert.Null(h.Inv.Snapshot.ReadiedLight);
        Assert.Equal(new[] { "torch" }, Carried(h));
    }

    // ----- a light that burns out ---------------------------------------

    // The burn-out line reaches the manager through the router's subscription
    // (KnownPatterns.LightBurnedOut), not its own line reader.

    [Fact]
    public void TheLitLightBurningOut_IsNoLongerHeld_InOneNotice()
    {
        // Left listed as lit until the next full read, a light that had burned out
        // went onto a death record as lost, and a Stock pile waited for it for good.
        using Harness h = new();
        h.Feed("You are carrying torch (Readied/1), torch, 5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    50/2880  -  Light  [2%]");
        int before = h.ChangedCount;

        h.Inv.NoteLitLightBurnedOut();

        Assert.Null(h.Inv.Snapshot.ReadiedLight);
        Assert.Equal(new[] { "torch" }, Carried(h));            // the spare only
        Assert.Equal(1, h.ChangedCount - before);
        Assert.Equal(new[] { "torch" },
            DeathLootCapture.LostOf(h.Inv.Snapshot).Select(i => i.Name).ToArray());
    }

    [Fact]
    public void ALightKeptWhenSpent_BurningOut_GoesBackIntoThePack()
    {
        // No light in the imported data is one; the engine's rule allows for it.
        using Harness h = new(staysWhenSpent: name => name == "glow stone");
        h.Feed("You are carrying glow stone (Readied/1), 5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    50/2880  -  Light  [2%]");

        h.Inv.NoteLitLightBurnedOut();

        Assert.Null(h.Inv.Snapshot.ReadiedLight);
        Assert.Equal(new[] { "glow stone" }, Carried(h));
    }

    [Fact]
    public void TheLitLightBurningOut_LowersTheTrackedWeight_ByTheOneThatLeft()
    {
        using Harness h = new(name => name == "torch" ? 40 : null);
        h.Feed("You are carrying torch (Readied/1), torch, 5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    90/2880  -  Light  [3%]");

        h.Inv.NoteLitLightBurnedOut();

        Assert.Equal(50, h.Inv.Snapshot.Encumbrance.CurrentWeight);
    }

    [Fact]
    public void ALightLitSinceTheRead_BurningOut_LowersTheTrackedWeight_ByTheOneThatLeft()
    {
        using Harness h = new(name => name == "torch" ? 40 : null);
        h.Feed("You are carrying torch, torch, 5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    90/2880  -  Light  [3%]");
        h.Feed("You lit the torch.");

        h.Inv.NoteLitLightBurnedOut();

        Assert.Equal(50, h.Inv.Snapshot.Encumbrance.CurrentWeight);
    }

    [Fact]
    public void ALightKeptWhenSpent_BurningOut_LeavesTheTrackedWeightAlone()
    {
        using Harness h = new(name => name == "glow stone" ? 40 : null, staysWhenSpent: name => name == "glow stone");
        h.Feed("You are carrying glow stone (Readied/1), 5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    90/2880  -  Light  [3%]");

        h.Inv.NoteLitLightBurnedOut();

        Assert.Equal(90, h.Inv.Snapshot.Encumbrance.CurrentWeight);
    }

    [Fact]
    public void ASpareLitAfterTheFirstBurnedOut_IsCountedOnce_AndLeavesWhenItBurnsOutToo()
    {
        // The whole of the case: read with a torch lit and one spare; the lit one
        // burns out; the spare is lit (Stock's line; it stays in the pack list);
        // then that one burns out as well.
        using Harness h = new();
        h.Feed("You are carrying torch (Readied/1), torch, 5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    50/2880  -  Light  [2%]");

        h.Inv.NoteLitLightBurnedOut();
        h.Feed("You lit the torch.");
        Assert.Equal(new[] { "torch" },
            DeathLootCapture.LostOf(h.Inv.Snapshot).Select(i => i.Name).ToArray());

        h.Inv.NoteLitLightBurnedOut();
        Assert.Empty(DeathLootCapture.LostOf(h.Inv.Snapshot));
    }

    [Fact]
    public void ABurnOut_WithNoLightKnownToBeLit_LeavesThePackAlone()
    {
        using Harness h = new();
        h.Feed("You are carrying torch, 5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    50/2880  -  Light  [2%]");
        int before = h.ChangedCount;

        h.Inv.NoteLitLightBurnedOut();

        Assert.Equal(new[] { "torch" }, Carried(h));
        Assert.Equal(0, h.ChangedCount - before);
    }

    [Fact]
    public void GetItem_DoesNotCollideWithCurrencyPickup()
    {
        using Harness h = new();
        FeedCarriedBaseline(h);

        // Past-tense currency line must not land in the carried item list.
        h.Feed("You picked up 30 gold crowns.");

        Assert.DoesNotContain(Carried(h), c => c.Contains("gold"));
        Assert.Equal(30, h.Inv.Snapshot.Currency.Gold);
    }

    [Fact]
    public void RemoveWeapon_MovesWeaponIntoCarried()
    {
        using Harness h = new();
        // Baseline: a worn weapon (quarterstaff) plus carried gear.
        h.Feed("You are carrying quarterstaff (Weapon Hand), padded gloves (Hands), lantern, 5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    60/2880  -  Light  [2%]");

        // Weapon removal is unnamed, so the manager reads the outgoing weapon's
        // name from the worn set and returns it to the pack.
        h.Feed("You now have no weapon readied.");

        Assert.DoesNotContain(Worn(h), e => e.Slot == "Weapon Hand");
        Assert.Contains("quarterstaff", Carried(h));
    }

    [Fact]
    public void CarriedPatch_BeforeBaseline_IsIgnored()
    {
        using Harness h = new();

        // No 'i' parsed yet — adding to an empty pack would imply it holds only
        // this one item, so the line is consumed but not applied.
        h.Feed("You took rusty dagger.");

        Assert.False(h.Inv.IsLoaded);
        Assert.Empty(Carried(h));
    }

    // ----- incremental give / receive ----------------------------------

    [Fact]
    public void GiveAway_RemovesItemFromCarried()
    {
        using Harness h = new();
        FeedCarriedBaseline(h);   // holds lantern

        h.Feed("You just gave lantern to Bob.");

        Assert.DoesNotContain("lantern", Carried(h));
    }

    // A multi-word recipient (an NPC) must not eat into the greedy item group.
    [Fact]
    public void GiveAway_MultiWordRecipient_RemovesItem()
    {
        using Harness h = new();
        FeedCarriedBaseline(h);

        h.Feed("You just gave lantern to the old man.");

        Assert.DoesNotContain("lantern", Carried(h));
    }

    [Fact]
    public void Receive_AddsItemToCarried()
    {
        using Harness h = new();
        FeedCarriedBaseline(h);

        h.Feed("Bob just gave you rusty dagger.");

        Assert.Contains("rusty dagger", Carried(h));
        Assert.Contains("lantern", Carried(h));   // baseline item untouched
    }

    // An NPC / quest giver names more than one word before "just gave you".
    [Fact]
    public void Receive_MultiWordGiver_AddsItem()
    {
        using Harness h = new();
        FeedCarriedBaseline(h);

        h.Feed("The old man just gave you a brass key.");

        Assert.Contains("a brass key", Carried(h));
    }

    // A hand-off raises ItemReceived (item, giver) so death recovery can count gear a
    // party member gave back; a coin hand-off doesn't.
    [Fact]
    public void Receive_RaisesItemReceived_ForItemsNotCoins()
    {
        using Harness h = new();
        FeedCarriedBaseline(h);
        List<(string Item, string Giver)> got = new();
        h.Inv.ItemReceived += (item, giver) => got.Add((item, giver));

        h.Feed("Nineteen just gave you shimmering white robes.");
        h.Feed("Nineteen just gave you 30 gold crowns.");

        Assert.Equal(new[] { ("shimmering white robes", "Nineteen") }, got);
    }

    // Paradigm also words a player's hand-over "<Player> gives you <item>.", naming
    // the item as its record does (report paradigm-20261007-164408: a handed-over
    // gate item was unknown to the route picker until the next `i`).
    private static bool IsTestRecordName(string name)
        => name is "magical quartz rod" or "torch" or "darkwood ring" or "lantern" or "black star key";

    private static bool IsTestKey(string name) => name is "black star key";

    private static void FeedWithKeyRing(Harness h)
    {
        h.Feed("You are carrying lantern.");
        h.Feed("You have the following keys:  2 black star key, gate key.");
        h.Feed("Wealth:    0 copper farthings");
        h.Feed("Encumbrance:    50/2880  -  Light  [2%]");
    }

    [Fact]
    public void HandedItem_AddsItemToCarried_AndRaisesItemReceived()
    {
        using Harness h = new(isItemRecordName: IsTestRecordName);
        FeedCarriedBaseline(h);
        List<(string Item, string Giver)> got = new();
        h.Inv.ItemReceived += (item, giver) => got.Add((item, giver));

        h.Feed("Bob gives you magical quartz rod.");

        Assert.Contains("magical quartz rod", Carried(h));
        Assert.Contains("lantern", Carried(h));   // baseline item untouched
        Assert.Equal(new[] { ("magical quartz rod", "Bob") }, got);
        Assert.True(h.Inv.IsReceivedHandOverLine("Bob gives you magical quartz rod."));
    }

    [Fact]
    public void HandedItem_MovesTheWeightEstimate()
    {
        using Harness h = new(itemWeight: TestWeight, isItemRecordName: IsTestRecordName);
        FeedCarriedBaseline(h);   // 50/2880

        h.Feed("Bob gives you torch.");

        Assert.Equal(90, Weight(h));   // 50 + 40
    }

    // More than one copy arrives under a count, the name still singular (report
    // paradigm-20261007-182345: "Fujin gives you 2 darkwood ring.").
    [Fact]
    public void HandedItem_UnderACount_AddsEveryCopy()
    {
        using Harness h = new(itemWeight: TestWeight, isItemRecordName: IsTestRecordName);
        FeedCarriedBaseline(h);   // 50/2880
        List<string> got = new();
        h.Inv.ItemReceived += (item, _) => got.Add(item);

        h.Feed("Fujin gives you 2 torch.");

        Assert.Contains("2 torch", Carried(h));
        Assert.Equal(130, Weight(h));   // 50 + 2 x 40
        Assert.Equal(new[] { "torch", "torch" }, got);
        Assert.True(h.Inv.IsReceivedHandOverLine("Fujin gives you 2 torch."));
    }

    // The giver's side of the same wording (report paradigm-20261007-182434:
    // "You give 2 darkwood ring to Fujin.", "You give mine pass to Fujin.").
    [Fact]
    public void HandedAway_RemovesTheCopiesGiven()
    {
        using Harness h = new(isItemRecordName: IsTestRecordName);
        h.Feed("You are carrying lantern, 3 darkwood ring.");
        h.Feed("Wealth:    0 copper farthings");
        h.Feed("Encumbrance:    50/2880  -  Light  [2%]");

        h.Feed("You give 2 darkwood ring to Fujin.");
        Assert.Contains("darkwood ring", Carried(h));
        Assert.DoesNotContain("3 darkwood ring", Carried(h));

        h.Feed("You give darkwood ring to Fujin.");
        Assert.DoesNotContain(Carried(h), c => c.Contains("darkwood ring"));

        h.Feed("You give lantern to Fujin.");
        Assert.DoesNotContain("lantern", Carried(h));
    }

    // The giver's own line is the one proof a give landed, in either wording: the
    // item, how many, and who was given it. Coins and a refused give raise nothing.
    [Fact]
    public void GiveConfirmed_RaisesItemGivenAway_ForItemsNotCoins()
    {
        using Harness h = new(isItemRecordName: IsTestRecordName);
        h.Feed("You are carrying lantern, 3 darkwood ring, torch.");
        h.Feed("Wealth:    0 copper farthings");
        h.Feed("Encumbrance:    50/2880  -  Light  [2%]");
        List<(string Item, int Copies, string To)> gave = new();
        h.Inv.ItemGivenAway += (item, copies, to) => gave.Add((item, copies, to));

        h.Feed("You just gave lantern to Member.");
        h.Feed("You give 2 darkwood ring to Member.");
        h.Feed("You give torch to Member.");
        h.Feed("You just gave 30 gold crowns to Member.");
        h.Feed("You give 5 gold crowns to Member");
        h.Feed("Member refuses your offer.");

        Assert.Equal(
            new[] { ("lantern", 1, "Member"), ("darkwood ring", 2, "Member"), ("torch", 1, "Member") }, gave);
    }

    // The Stock engine's three refusals of a give: two name the player it was for,
    // one names nobody. Nothing leaves the pack.
    [Fact]
    public void GiveRefused_IsRaisedWithThePlayerTheLineNames()
    {
        using Harness h = new();
        FeedCarriedBaseline(h);
        List<string?> refused = new();
        h.Inv.GiveRefused += refused.Add;

        h.Feed("Member refuses your offer.");
        h.Feed("Member cannot accept your offer.");
        h.Feed("You may not give that item away!");

        Assert.Equal(new string?[] { "Member", "Member", null }, refused);
        Assert.Contains("lantern", Carried(h));
    }

    // Keys sit on their own ring in the dump. A key given away must leave it, or the
    // character still "holds" the key and a door it opens stays passable to the planner.
    [Fact]
    public void HandedAway_Key_LeavesTheKeyRing()
    {
        using Harness h = new(isItemRecordName: IsTestRecordName, isKey: IsTestKey);
        FeedWithKeyRing(h);

        h.Feed("You give black star key to Fujin.");
        Assert.Contains("black star key", h.Inv.Snapshot.Keys!);
        Assert.DoesNotContain("2 black star key", h.Inv.Snapshot.Keys!);

        h.Feed("You give black star key to Fujin.");
        Assert.DoesNotContain(h.Inv.Snapshot.Keys!, k => k.Contains("black star key"));
        Assert.Contains("gate key", h.Inv.Snapshot.Keys!);
        Assert.Contains("lantern", Carried(h));
    }

    [Fact]
    public void HandedItem_Key_GoesOnTheKeyRing()
    {
        using Harness h = new(isItemRecordName: IsTestRecordName, isKey: IsTestKey);
        FeedCarriedBaseline(h);   // no ring yet

        h.Feed("Fujin gives you 2 black star key.");
        h.Feed("Fujin gives you black star key.");

        Assert.Contains("3 black star key", h.Inv.Snapshot.Keys!);
        Assert.DoesNotContain(Carried(h), c => c.Contains("black star key"));
    }

    // The engine's own wording moves the ring too; a key already on it is known to
    // be one without asking the game data.
    [Fact]
    public void JustGave_Key_MovesOnTheKeyRing()
    {
        using Harness h = new();
        FeedWithKeyRing(h);

        h.Feed("Bob just gave you gate key.");
        Assert.Contains("2 gate key", h.Inv.Snapshot.Keys!);

        h.Feed("You just gave black star key to Bob.");
        h.Feed("You just gave black star key to Bob.");
        Assert.DoesNotContain(h.Inv.Snapshot.Keys!, k => k.Contains("black star key"));
        Assert.DoesNotContain(Carried(h), c => c.Contains("key"));
    }

    // Picking a key up, dropping it, hiding it, buying and selling it move the ring
    // the same way a hand-over does.
    [Fact]
    public void Key_GotDroppedHiddenBoughtSold_MovesOnTheKeyRing()
    {
        using Harness h = new(isItemRecordName: IsTestRecordName, isKey: IsTestKey);
        FeedWithKeyRing(h);   // 2 black star key, gate key

        h.Feed("You took black star key.");
        Assert.Contains("3 black star key", h.Inv.Snapshot.Keys!);

        h.Feed("You dropped 2 black star key.");
        Assert.Contains("black star key", h.Inv.Snapshot.Keys!);
        Assert.DoesNotContain("3 black star key", h.Inv.Snapshot.Keys!);

        h.Feed("You hid black star key.");
        Assert.DoesNotContain(h.Inv.Snapshot.Keys!, k => k.Contains("black star key"));

        h.Feed("You just bought black star key for 10 copper farthings.");
        Assert.Contains("black star key", h.Inv.Snapshot.Keys!);

        h.Feed("You sold black star key for 5 copper farthings.");
        Assert.DoesNotContain(h.Inv.Snapshot.Keys!, k => k.Contains("black star key"));

        Assert.Contains("gate key", h.Inv.Snapshot.Keys!);
        Assert.DoesNotContain(Carried(h), c => c.Contains("black star key"));
    }

    // With nothing to say an item is a key it is filed in the pack, and leaves from
    // there: the pack gives up its copy before the ring is asked.
    [Fact]
    public void Key_NotKnownToBeOne_IsHandledInThePack()
    {
        using Harness h = new(isItemRecordName: IsTestRecordName);
        FeedCarriedBaseline(h);

        h.Feed("You took black star key.");
        Assert.Contains("black star key", Carried(h));

        h.Feed("You give black star key to Fujin.");
        Assert.DoesNotContain(Carried(h), c => c.Contains("black star key"));
    }

    // Flavour in the giver's shape names no record, so nothing leaves the pack.
    [Fact]
    public void HandedAway_IgnoresFlavourThatNamesNoRecord()
    {
        using Harness h = new(isItemRecordName: IsTestRecordName);
        FeedCarriedBaseline(h);
        IReadOnlyList<string> before = Carried(h);

        h.Feed("You give the lantern to the old man.");
        h.Feed("You give her the news of Commander Markus, and hand her the darkwood box.");

        Assert.Equal(before, Carried(h));
    }

    // Coins in this wording print the coin's full noun and no full stop (reports
    // paradigm-20261007-182345 and -182434).
    [Fact]
    public void HandedCoins_MoveThePurseBothWays()
    {
        using Harness h = new(isItemRecordName: IsTestRecordName);
        FeedCarriedBaseline(h);

        h.Feed("Fujin gives you 2 runic coins");
        h.Feed("Fujin gives you 30 platinum pieces");
        Assert.Equal(2, h.Inv.Snapshot.Currency.Runic);
        Assert.Equal(30, h.Inv.Snapshot.Currency.Platinum);
        Assert.True(h.Inv.IsReceivedHandOverLine("Fujin gives you 30 platinum pieces"));

        h.Feed("You give 2 runic coins to Fujin");
        h.Feed("You give 25 platinum pieces to Fujin");
        Assert.Equal(0, h.Inv.Snapshot.Currency.Runic);
        Assert.Equal(5, h.Inv.Snapshot.Currency.Platinum);

        // One coin still prints its count, with the noun in the singular.
        h.Feed("Raijin gives you 1 gold crown");
        Assert.Equal(1, h.Inv.Snapshot.Currency.Gold);
        h.Feed("You give 1 gold crown to Fujin");
        Assert.Equal(0, h.Inv.Snapshot.Currency.Gold);
        Assert.DoesNotContain(Carried(h), c => c.Contains("platinum") || c.Contains("runic"));
    }

    // An NPC's keyword give and a spell line share the shape. Their wording isn't a
    // record name, and isn't proof an item arrived, so nothing is filed.
    [Theory]
    [InlineData("Dhelvanen gives you a green potion.")]
    [InlineData("The gnome commander gives you the heavy bloodstone orb.")]
    [InlineData("A glowing flame gives you magical sight.")]
    [InlineData("The black cat gives you an evil eye before dying.")]
    [InlineData("Bob gives you 30 gold crowns.")]
    public void HandedItem_IgnoresLinesThatNameNoRecord(string line)
    {
        using Harness h = new(isItemRecordName: IsTestRecordName);
        FeedCarriedBaseline(h);
        IReadOnlyList<string> before = Carried(h);
        bool raised = false;
        h.Inv.ItemReceived += (_, _) => raised = true;

        h.Feed(line);

        Assert.Equal(before, Carried(h));
        Assert.Equal(0, h.Inv.Snapshot.Currency.Gold);
        Assert.False(raised);
        Assert.False(h.Inv.IsReceivedHandOverLine(line));
    }

    // Giving coins adjusts the purse, not the pack — no phantom carried item.
    [Fact]
    public void GiveAway_Coins_AdjustsCurrencyNotCarried()
    {
        using Harness h = new();
        h.Feed("You are carrying lantern, 30 gold crowns.");
        h.Feed("Wealth:    3000 copper farthings");
        h.Feed("Encumbrance:    50/2880  -  Light  [2%]");

        h.Feed("You just gave 10 gold crowns to Bob.");

        Assert.Equal(20, h.Inv.Snapshot.Currency.Gold);
        Assert.DoesNotContain(Carried(h), c => c.Contains("gold"));
    }

    [Fact]
    public void Receive_Coins_AdjustsCurrencyNotCarried()
    {
        using Harness h = new();
        FeedCarriedBaseline(h);   // 5 copper

        h.Feed("Bob just gave you 30 gold crowns.");

        Assert.Equal(30, h.Inv.Snapshot.Currency.Gold);
        Assert.DoesNotContain(Carried(h), c => c.Contains("gold"));
    }

    // The engine words a coin hand-over apart from an item's: no "just", no full
    // stop, and `give` names the bare metal where `share` prints the coin's noun.
    [Theory]
    [InlineData("Bob gave you 30 gold", 30, 0, 0)]
    [InlineData("Bob gave you 30 gold crowns", 30, 0, 0)]
    [InlineData("Bob gave you 1 gold crown", 1, 0, 0)]
    [InlineData("Bob gave you 7 platinum", 0, 7, 0)]
    [InlineData("Bob gave you 2 runic", 0, 0, 2)]
    [InlineData("Bob gave you 2 quatloos", 0, 0, 2)]   // a board's own name for the fifth coin
    public void Receive_Coins_EngineCoinWording_AdjustsCurrency(string line, int gold, int platinum, int runic)
    {
        using Harness h = new();
        FeedCarriedBaseline(h);
        bool raised = false;
        h.Inv.ItemReceived += (_, _) => raised = true;

        h.Feed(line);

        Assert.Equal(gold, h.Inv.Snapshot.Currency.Gold);
        Assert.Equal(platinum, h.Inv.Snapshot.Currency.Platinum);
        Assert.Equal(runic, h.Inv.Snapshot.Currency.Runic);
        Assert.DoesNotContain(Carried(h), c => c.Contains("gold") || c.Contains("Bob"));
        Assert.False(raised);
        Assert.True(h.Inv.IsReceivedHandOverLine(line));
    }

    [Theory]
    [InlineData("You gave Bob 10 gold")]
    [InlineData("You gave Bob 10 gold crowns")]
    public void GiveAway_Coins_EngineCoinWording_AdjustsCurrency(string line)
    {
        using Harness h = new();
        h.Feed("You are carrying lantern, 30 gold crowns.");
        h.Feed("Wealth:    3000 copper farthings");
        h.Feed("Encumbrance:    50/2880  -  Light  [2%]");

        h.Feed(line);

        Assert.Equal(20, h.Inv.Snapshot.Currency.Gold);
    }

    // A coin hand-over of ours names who got it and what it was worth, in both
    // wordings: what a give that waits to be confirmed is confirmed by. Coins handed
    // to us raise nothing.
    [Theory]
    [InlineData("You gave Bob 3 gold", "Bob", 300)]
    [InlineData("You gave Bob 3 gold crowns", "Bob", 300)]
    [InlineData("You gave Bob 0 gold", "Bob", 0)]
    [InlineData("You give 3 gold crowns to Bob", "Bob", 300)]
    [InlineData("You give 1 platinum piece to Bob", "Bob", 10_000)]
    // A fifth coin under a board's own name is worth what a runic is.
    [InlineData("You give 2 mithril coins to Bob", "Bob", 2_000_000)]
    [InlineData("You gave Bob 2 mithril", "Bob", 2_000_000)]
    public void GiveAway_Coins_SaysWhoGotThemAndWhatTheyWereWorth(string line, string recipient, long copper)
    {
        using Harness h = new();
        h.Feed("You are carrying lantern, 30 gold crowns, 2 platinum pieces.");
        h.Feed("Wealth:    23000 copper farthings");
        h.Feed("Encumbrance:    50/2880  -  Light  [2%]");
        List<(string Recipient, long Copper)> given = new();
        h.Inv.CoinsGivenAway += (to, worth) => given.Add((to, worth));

        h.Feed("Bob gave you 5 gold");
        Assert.Empty(given);
        h.Feed(line);

        Assert.Equal(new[] { (recipient, copper) }, given);
    }

    // The Stock engine's answer to a coin give at nobody it can find for us: told
    // apart from the refusals that name the player, and moving nothing.
    [Fact]
    public void GiveAway_Coins_TheLineForNobodyHereToGiveTo_IsRaised_AndMovesNothing()
    {
        using Harness h = new();
        h.Feed("You are carrying lantern, 30 gold crowns.");
        h.Feed("Wealth:    3000 copper farthings");
        h.Feed("Encumbrance:    50/2880  -  Light  [2%]");
        int misaimed = 0;
        bool refused = false;
        h.Inv.CoinGiveMisaimed += () => misaimed++;
        h.Inv.GiveRefused += _ => refused = true;

        h.Feed("Why would you want to give to that?");

        Assert.Equal(1, misaimed);
        Assert.False(refused);
        Assert.Equal(30, h.Inv.Snapshot.Currency.Gold);
    }

    // The engine's answer to a coin give of more of a coin than is held, one wording
    // for each of the four metals and one for the board's fifth coin.
    [Theory]
    [InlineData("You do not have that much copper!")]
    [InlineData("You do not have that much silver!")]
    [InlineData("You do not have that much gold!")]
    [InlineData("You do not have that much platinum!")]
    [InlineData("You do not have that many runic coins!")]
    public void GiveAway_Coins_TheLineForMoreThanIsHeld_IsRaised_AndMovesNothing(string line)
    {
        using Harness h = new();
        h.Feed("You are carrying lantern, 30 gold crowns.");
        h.Feed("Wealth:    3000 copper farthings");
        h.Feed("Encumbrance:    50/2880  -  Light  [2%]");
        int lacked = 0;
        h.Inv.CoinGiveLackedCoins += () => lacked++;

        h.Feed(line);
        h.Feed("You do not have that.");                          // another command's line: not this

        Assert.Equal(1, lacked);
        Assert.Equal(30, h.Inv.Snapshot.Currency.Gold);
    }

    // The count-then-words shape with more than one unknown word names no coin.
    [Fact]
    public void Receive_Coins_IgnoresALineThatNamesNoCoin()
    {
        using Harness h = new();
        FeedCarriedBaseline(h);

        h.Feed("Bob gave you 5 dirty looks");

        Assert.Equal(0, h.Inv.Snapshot.Currency.Runic);
        Assert.False(h.Inv.IsReceivedHandOverLine("Bob gave you 5 dirty looks"));
    }

    // "You don't have X to give." is a bounced give — nothing changes.
    [Fact]
    public void GiveFailed_LeavesCarriedUnchanged()
    {
        using Harness h = new();
        FeedCarriedBaseline(h);

        h.Feed("You don't have torch to give.");

        Assert.Contains("lantern", Carried(h));
        Assert.Single(Carried(h));
    }

    // ----- incremental item-weight encumbrance -------------------------

    // Stands in for the game-data Encum lookup: a fixed name→weight table.
    // Unlisted names resolve to null, exercising the unknown-item path.
    private static int? TestWeight(string name) => name switch
    {
        "torch" => 40,
        "lantern" => 30,
        "broadsword" => 150,
        _ => null,
    };

    private static int Weight(Harness h) => h.Inv.Snapshot.Encumbrance.CurrentWeight;

    [Fact]
    public void Get_AddsItemWeightToEncumbrance()
    {
        using Harness h = new(TestWeight);
        FeedCarriedBaseline(h);   // 50/2880

        h.Feed("You took torch.");

        Assert.Equal(90, Weight(h));   // 50 + 40
    }

    [Fact]
    public void GetBatch_AddsCountTimesItemWeight()
    {
        // Paradigm's counted get echoes "You took N <item>." — carried + weight
        // must move by N, not one.
        using Harness h = new(TestWeight);
        FeedCarriedBaseline(h);   // 50/2880

        h.Feed("You took 3 torch.");

        Assert.Equal(170, Weight(h));   // 50 + 3*40
        // Stacked into one "3 torch" entry, not three bare rows.
        Assert.Contains("3 torch", Carried(h));
        Assert.DoesNotContain("torch", Carried(h));
    }

    [Fact]
    public void Drop_SubtractsItemWeightFromEncumbrance()
    {
        using Harness h = new(TestWeight);
        FeedCarriedBaseline(h);   // 50/2880, holds lantern (30)

        h.Feed("You dropped lantern.");

        Assert.Equal(20, Weight(h));   // 50 - 30
    }

    [Fact]
    public void Hide_SubtractsItemWeightFromEncumbrance()
    {
        using Harness h = new(TestWeight);
        FeedCarriedBaseline(h);   // 50/2880, holds lantern (30)

        h.Feed("You hid lantern.");   // a stash leaves the pack like a drop

        Assert.Equal(20, Weight(h));   // 50 - 30
    }

    [Fact]
    public void HideBatch_SubtractsCountTimesItemWeight()
    {
        // Paradigm batches a stash into one line ("You hid 35 orc-head.") — the
        // weight estimate must drop by N*weight, else it reads too heavy until the
        // next full 'i' and the collect gates wrongly skip a pickup while the
        // character actually has room (report paradigm-20260812-201631).
        using Harness h = new(TestWeight);
        h.Feed("You are carrying torch, torch, torch, 5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    170/2880  -  Light  [5%]");   // 50 + 3*40

        h.Feed("You hid 3 torch.");

        Assert.Equal(50, Weight(h));    // 170 - 3*40
        Assert.DoesNotContain("torch", Carried(h));
    }

    [Fact]
    public void AutoGet_StacksOntoExistingStack_NoDuplicateRows()
    {
        // Repro: a full `i` shows "43 black diamond" (one stacked row); each auto-get then
        // appended a separate "black diamond" row, so @inv / @have / Character Info filled
        // with duplicates. Now the get folds into the stack, and repeated bare tokens in
        // the dump collapse too.
        using Harness h = new();
        h.Feed("You are carrying 43 black diamond, torch, torch, 5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    50/2880  -  Light  [2%]");

        Assert.Contains("43 black diamond", Carried(h));
        Assert.Contains("2 torch", Carried(h));        // two bare tokens collapsed

        h.Feed("You took black diamond.");
        h.Feed("You took 2 black diamond.");
        h.Feed("You took torch.");

        Assert.Contains("46 black diamond", Carried(h));   // 43 + 1 + 2
        Assert.Contains("3 torch", Carried(h));            // 2 + 1
        // Exactly one row per distinct item — no duplicate rows.
        Assert.Equal(1, Carried(h).Count(n => n.EndsWith("black diamond", StringComparison.Ordinal)));
        Assert.Equal(1, Carried(h).Count(n => n.EndsWith("torch", StringComparison.Ordinal)));

        // A drop decrements the stack instead of missing the count-prefixed row.
        h.Feed("You dropped 6 black diamond.");
        Assert.Contains("40 black diamond", Carried(h));
    }

    [Fact]
    public void Buy_AddsItemWeightToEncumbrance()
    {
        using Harness h = new(TestWeight);
        FeedCarriedBaseline(h);

        // "for nothing" so the purchase price moves no coins — isolates the
        // item-weight change from the coin-weight change buying normally causes.
        h.Feed("You bought broadsword for nothing.");

        Assert.Equal(200, Weight(h));   // 50 + 150
    }

    [Fact]
    public void Sell_SubtractsItemWeightFromEncumbrance()
    {
        using Harness h = new(TestWeight);
        // 3 copper + a 2-copper sale = 5 copper: both sides floor to 1
        // coin-weight unit, so the sale's coin weight is a no-op and only the
        // lantern's item weight moves.
        h.Feed("You are carrying lantern, 3 copper farthings.");
        h.Feed("Wealth:    3 copper farthings");
        h.Feed("Encumbrance:    50/2880  -  Light  [2%]");

        h.Feed("You sold lantern for 2 copper farthings.");

        Assert.Equal(20, Weight(h));   // 50 - 30
    }

    [Fact]
    public void Equip_DoesNotChangeTotalWeight()
    {
        using Harness h = new(TestWeight);
        FeedCarriedBaseline(h);   // holds lantern

        // Wielding an item already on your person doesn't change carried weight —
        // it moves between lists, not on/off the body.
        h.Feed("You are now holding lantern.");

        Assert.Equal(50, Weight(h));
    }

    [Fact]
    public void Get_UnknownItem_LeavesWeightUntouched()
    {
        using Harness h = new(TestWeight);
        FeedCarriedBaseline(h);

        // Not in the weight table — the carried list still updates, but the
        // encumbrance estimate holds until the next 'i' dump re-bases it.
        h.Feed("You took mysterious orb.");

        Assert.Contains("mysterious orb", Carried(h));
        Assert.Equal(50, Weight(h));
    }

    [Fact]
    public void Drop_ClampsWeightAtZero()
    {
        using Harness h = new(TestWeight);
        // A light 10-unit pack holding a heavy broadsword (150).
        h.Feed("You are carrying broadsword, 5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    10/2880  -  None  [0%]");

        h.Feed("You dropped broadsword.");

        // 10 - 150 floors at 0 rather than going negative.
        Assert.Equal(0, Weight(h));
    }

    [Fact]
    public void ItemWeight_RecomputesPercentageAndCategory()
    {
        using Harness h = new(TestWeight);
        // Small max so a single heavy item visibly moves the bracket.
        h.Feed("You are carrying 5 copper farthings.");
        h.Feed("Wealth:    5 copper farthings");
        h.Feed("Encumbrance:    10/200  -  None  [5%]");

        h.Feed("You took broadsword.");   // +150 → 160/200 = 80%

        EncumbranceReading e = h.Inv.Snapshot.Encumbrance;
        Assert.Equal(160, e.CurrentWeight);
        Assert.Equal(80, e.Percentage);
        Assert.Equal(EncumbranceLevel.Heavy, e.Category);
    }

    [Fact]
    public void ItemWeight_NoResolver_LeavesEncumbranceUntouched()
    {
        using Harness h = new();   // no weight resolver wired
        FeedCarriedBaseline(h);

        h.Feed("You took torch.");

        Assert.Contains("torch", Carried(h));
        Assert.Equal(50, Weight(h));
    }

    // ----- incremental wear-slot resolution ----------------------------

    [Fact]
    public void Wear_ResolvesRealSlotFromGameData()
    {
        // The "You are now wearing X." line names no slot; the resolver supplies
        // the item's true one so the worn set — and "Snapshot Current" — file it
        // under "Torso" rather than a generic bucket.
        using Harness h = new(slotResolver: name => name == "padded vest" ? "Torso" : null);
        FeedCarriedBaseline(h);

        h.Feed("You are now wearing padded vest.");

        Assert.Contains(Worn(h), e => e is { Name: "padded vest", Slot: "Torso" });
    }

    [Fact]
    public void Wear_NoResolver_FallsBackToGenericWornSlot()
    {
        using Harness h = new();   // no slot resolver wired
        FeedCarriedBaseline(h);

        h.Feed("You are now wearing padded vest.");

        // Unresolved slots keep the generic placeholder — the next 'i' dump
        // restores the exact placement.
        Assert.Contains(Worn(h), e => e is { Name: "padded vest", Slot: "Worn" });
    }

    [Fact]
    public void Wear_UnknownItem_FallsBackToGenericWornSlot()
    {
        using Harness h = new(slotResolver: _ => null);   // resolver knows nothing
        FeedCarriedBaseline(h);

        h.Feed("You are now wearing padded vest.");

        Assert.Contains(Worn(h), e => e is { Name: "padded vest", Slot: "Worn" });
    }

    // ----- Cross-session encumbrance persistence --------------------

    [Fact]
    public void SnapshotEncumbrance_BeforeAnyParse_ReturnsNull()
    {
        using Harness h = new();
        // Nothing observed yet — a null keeps the save hook from clobbering a
        // previously-persisted good reading with an empty one.
        Assert.Null(h.Inv.SnapshotEncumbrance());
    }

    [Fact]
    public void SnapshotEncumbrance_AfterFullParse_MirrorsReading()
    {
        using Harness h = new();
        FeedFullInventory(h);

        LastKnownEncumbrance? snap = h.Inv.SnapshotEncumbrance();
        Assert.NotNull(snap);
        Assert.Equal(36, snap!.CurrentWeight);
        Assert.Equal(2880, snap.MaxWeight);
        Assert.Equal(1, snap.Percentage);
        Assert.Equal(EncumbranceLevel.Light, snap.Category);
    }

    [Fact]
    public void HydrateEncumbrance_SeedsReadingButStaysUnloaded()
    {
        using Harness h = new();

        h.Inv.HydrateEncumbrance(new LastKnownEncumbrance
        {
            CurrentWeight = 3908, MaxWeight = 6804, Percentage = 57,
            Category = EncumbranceLevel.Medium,
        });

        EncumbranceReading e = h.Inv.Snapshot.Encumbrance;
        Assert.Equal(3908, e.CurrentWeight);
        Assert.Equal(6804, e.MaxWeight);
        Assert.Equal(57, e.Percentage);
        Assert.Equal(EncumbranceLevel.Medium, e.Category);
        // A restored value is a stale estimate — the connect-`i` still owns the
        // authoritative full re-base, so IsLoaded must stay false.
        Assert.False(h.Inv.IsLoaded);
        Assert.Equal(1, h.ChangedCount);
    }

    [Fact]
    public void HydrateEncumbrance_Null_ClearsToUnobserved()
    {
        using Harness h = new();
        h.Inv.HydrateEncumbrance(new LastKnownEncumbrance
        {
            CurrentWeight = 3908, MaxWeight = 6804, Percentage = 57,
            Category = EncumbranceLevel.Medium,
        });

        h.Inv.HydrateEncumbrance(null);

        Assert.Null(h.Inv.SnapshotEncumbrance());
        EncumbranceReading e = h.Inv.Snapshot.Encumbrance;
        Assert.Equal(0, e.MaxWeight);
        Assert.Equal(EncumbranceLevel.Unknown, e.Category);
    }

    [Fact]
    public void SnapshotThenHydrate_RoundTripsIntoFreshManager()
    {
        // The "close client, reconnect" flow: parse an `i`, snapshot, hydrate
        // into a freshly-constructed manager — the reading survives intact.
        using Harness writer = new();
        FeedFullInventory(writer);
        LastKnownEncumbrance? snap = writer.Inv.SnapshotEncumbrance();

        using InventoryManager reader = new();
        reader.HydrateEncumbrance(snap);

        EncumbranceReading e = reader.Snapshot.Encumbrance;
        Assert.Equal(36, e.CurrentWeight);
        Assert.Equal(2880, e.MaxWeight);
        Assert.Equal(1, e.Percentage);
        Assert.Equal(EncumbranceLevel.Light, e.Category);
    }

    // The trainer's fee leaves the purse. Missed, the purse kept coins it no longer
    // held and the weight estimate ran up to the cap (report paradigm-20261001-085357).
    [Theory]
    [InlineData("You hand over 350 copper farthings to train to the next level!", 350)]
    [InlineData("You hand over 1 gold crown and you receive training to attain level 3.", 100)]
    public void TrainingFee_ComesOffThePurse(string trainLine, long fee)
    {
        Harness h = new();
        FeedFullInventory(h);
        long before = h.Inv.Snapshot.Currency.TotalCopperValue;
        Assert.True(before >= fee);

        h.Feed(trainLine);

        Assert.Equal(before - fee, h.Inv.Snapshot.Currency.TotalCopperValue);
    }
}
