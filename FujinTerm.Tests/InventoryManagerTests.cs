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

        h.Feed("You pick up rusty dagger.");

        Assert.Contains("rusty dagger", Carried(h));
        Assert.Contains("lantern", Carried(h));   // baseline item untouched
    }

    [Fact]
    public void Drop_RemovesItemFromCarried()
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
    public void CarriedPatch_BeforeBaseline_IsIgnored()
    {
        using Harness h = new();

        // No 'i' parsed yet — adding to an empty pack would imply it holds only
        // this one item, so the line is consumed but not applied.
        h.Feed("You pick up rusty dagger.");

        Assert.False(h.Inv.IsLoaded);
        Assert.Empty(Carried(h));
    }
}
