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

        public Harness(
            Func<string, int?>? itemWeight = null,
            Func<string, string?>? slotResolver = null)
        {
            Inv = new InventoryManager(
                log: null, itemWeightResolver: itemWeight, slotResolver: slotResolver);
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
        Assert.Equal(TimeSpan.FromSeconds(239 * 30), light.RemainingTime);

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
    public void Drop_SubtractsItemWeightFromEncumbrance()
    {
        using Harness h = new(TestWeight);
        FeedCarriedBaseline(h);   // 50/2880, holds lantern (30)

        h.Feed("You dropped lantern.");

        Assert.Equal(20, Weight(h));   // 50 - 30
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
}
