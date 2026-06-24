using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Cash;
using FujinTerm.Game.Inventory;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.E — <see cref="CashManager"/> per-currency policy dispatch,
/// held tally tracking via pick-up / drop lines, and the auto-
/// deposit threshold trigger.
/// </summary>
public sealed class CashManagerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public CashManager Cash { get; }
        public List<byte[]> Sent { get; } = new();
        public CashSettings Settings { get; set; } = new();
        public bool AutoGetCashEnabled { get; set; } = true;
        // Default Empty → MaxWeight 0 → encumbrance gate inert, so the
        // non-gate tests run the v1 full-pickup path unchanged. Gate
        // tests set a populated snapshot before feeding the cash line.
        public InventorySnapshot Snapshot { get; set; } = InventorySnapshot.Empty;
        public List<(string Currency, int Count, CashPolicy Policy)> Dispatches { get; } = new();
        public List<long> AutoDeposits { get; } = new();
        public List<(string Currency, int Count)> Collected { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Cash = new CashManager(Router,
                readSettings: () => Settings,
                isEnabled: () => AutoGetCashEnabled,
                getSnapshot: () => Snapshot,
                log: Log);
            Cash.SetWireSender(b => Sent.Add(b));
            Cash.CashDispatched += (c, n, p) => Dispatches.Add((c, n, p));
            Cash.AutoDepositRequested += t => AutoDeposits.Add(t);
            Cash.CoinCollected += (c, n) => Collected.Add((c, n));
        }

        public void Feed(string line)
        {
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));
        }

        public string LastSent => Sent.Count == 0
            ? string.Empty
            : Encoding.Latin1.GetString(Sent[^1]).TrimEnd('\r');

        public IReadOnlyList<string> AllSent =>
            Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

        public void Dispose() => Cash.Dispose();
    }

    // ----- per-currency policy dispatch -------------------------------

    [Fact]
    public void OnGround_Plural_CollectsWithExactCount()
    {
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;

        h.Feed("There are 50 gold pieces here.");

        Assert.Single(h.Sent);
        Assert.Equal("get 50 gold", h.LastSent);
        Assert.Single(h.Dispatches);
        Assert.Equal(("gold", 50, CashPolicy.Collect), h.Dispatches[0]);
    }

    [Fact]
    public void OnGround_Singular_CollectsAsCountOne()
    {
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;

        h.Feed("There is a gold piece here.");

        Assert.Single(h.Sent);
        Assert.Equal("get 1 gold", h.LastSent);
        Assert.Equal(1, h.Dispatches[0].Count);
    }

    [Fact]
    public void OnGround_Ignore_NoWireSend()
    {
        using Harness h = new();
        h.Settings.CopperPolicy = CashPolicy.Ignore;

        h.Feed("There are 50 copper pieces here.");

        Assert.Empty(h.Sent);
        Assert.Single(h.Dispatches);
        Assert.Equal(CashPolicy.Ignore, h.Dispatches[0].Policy);
    }

    [Fact]
    public void OnGround_Discard_NoWireSend()
    {
        using Harness h = new();
        h.Settings.CopperPolicy = CashPolicy.Discard;

        h.Feed("There are 50 copper pieces here.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void OnGround_UnknownCurrency_NoOp()
    {
        // Realm-specific name not in our table — defaults to Ignore.
        using Harness h = new();
        h.Feed("There are 50 zorkmid pieces here.");

        Assert.Empty(h.Sent);
        Assert.Single(h.Dispatches);
        Assert.Equal(CashPolicy.Ignore, h.Dispatches[0].Policy);
    }

    // ----- master switch ----------------------------------------------

    [Fact]
    public void AutoGetCashOff_NoDispatch()
    {
        using Harness h = new() { AutoGetCashEnabled = false };
        h.Settings.GoldPolicy = CashPolicy.Collect;

        h.Feed("There are 50 gold pieces here.");

        Assert.Empty(h.Sent);
        Assert.Empty(h.Dispatches);
    }

    // ----- held tally + auto-deposit ----------------------------------

    [Fact]
    public void PickedUp_IncrementsHeldCoin()
    {
        using Harness h = new();
        h.Feed("You picked up 50 gold pieces.");

        Assert.Equal(50, h.Cash.HeldCoin("gold"));
    }

    [Fact]
    public void Dropped_DecrementsHeldCoin()
    {
        using Harness h = new();
        h.Feed("You picked up 50 gold pieces.");
        h.Feed("You dropped 20 gold pieces.");

        Assert.Equal(30, h.Cash.HeldCoin("gold"));
    }

    [Fact]
    public void PickedUp_RaisesCoinCollected()
    {
        using Harness h = new();
        h.Feed("You picked up 50 gold pieces.");

        Assert.Equal(("gold", 50), Assert.Single(h.Collected));
    }

    [Fact]
    public void AutoDeposit_FiresWhenWealthExceedsThreshold()
    {
        // The wealth gate reads the authoritative Wealth: value
        // (TotalCopperValue), not the local pickup tally.
        using Harness h = new();
        h.Settings.AutoDepositIfWealthExceeds = 100;
        h.Settings.BankRoomKey = "bank";

        h.Snapshot = Wealth(150);
        h.Cash.OnInventoryChanged();

        Assert.Single(h.AutoDeposits);
        Assert.Equal(150, h.AutoDeposits[0]);
    }

    [Fact]
    public void AutoDeposit_SingleShotPerCrossing()
    {
        using Harness h = new();
        h.Settings.AutoDepositIfWealthExceeds = 100;
        h.Settings.BankRoomKey = "bank";

        h.Snapshot = Wealth(150);
        h.Cash.OnInventoryChanged();
        h.Snapshot = Wealth(200);
        h.Cash.OnInventoryChanged();

        Assert.Single(h.AutoDeposits);
    }

    [Fact]
    public void AutoDeposit_ReArmsAfterDropBelowThreshold()
    {
        using Harness h = new();
        h.Settings.AutoDepositIfWealthExceeds = 100;
        h.Settings.BankRoomKey = "bank";

        h.Snapshot = Wealth(150);
        h.Cash.OnInventoryChanged();
        Assert.Single(h.AutoDeposits);

        h.Snapshot = Wealth(30);          // below threshold → re-arms
        h.Cash.OnInventoryChanged();
        h.Snapshot = Wealth(130);         // back above → re-fires
        h.Cash.OnInventoryChanged();

        Assert.Equal(2, h.AutoDeposits.Count);
    }

    [Fact]
    public void AutoDeposit_ZeroThreshold_NoFire()
    {
        // Both gates 0 means disabled — no fire regardless of holdings.
        using Harness h = new();
        h.Settings.AutoDepositIfWealthExceeds = 0;
        h.Settings.BankRoomKey = "bank";

        h.Snapshot = Wealth(100_000);
        h.Cash.OnInventoryChanged();

        Assert.Empty(h.AutoDeposits);
    }

    // ----- location precondition --------------------------------------

    [Fact]
    public void AutoDeposit_NoLocation_NoFire()
    {
        // A gate is set and exceeded, but no bank / stash room is picked —
        // there's nowhere to detour, so the gate can't arm.
        using Harness h = new();
        h.Settings.AutoDepositIfWealthExceeds = 100;
        h.Settings.BankRoomKey = string.Empty;

        h.Snapshot = Wealth(500);
        h.Cash.OnInventoryChanged();

        Assert.Empty(h.AutoDeposits);
    }

    // ----- coin-count gate (independent of wealth value) --------------

    [Fact]
    public void AutoDeposit_FiresWhenCoinCountExceeds_EvenIfWealthBelow()
    {
        using Harness h = new();
        h.Settings.AutoDepositIfWealthExceeds = 1000;  // wealth gate stays above
        h.Settings.AutoDepositIfCoinsExceed = 10;
        h.Settings.BankRoomKey = "bank";

        h.Snapshot = Wealth(50, copper: 50);  // count 50 > 10, value 50 < 1000
        h.Cash.OnInventoryChanged();

        Assert.Single(h.AutoDeposits);
    }

    [Fact]
    public void AutoDeposit_EitherGateFires_OrLogic()
    {
        using Harness h = new();
        h.Settings.AutoDepositIfWealthExceeds = 100;
        h.Settings.AutoDepositIfCoinsExceed = 1000;    // coin gate stays above
        h.Settings.BankRoomKey = "bank";

        h.Snapshot = Wealth(150, copper: 1);  // wealth 150 > 100, count 1 < 1000
        h.Cash.OnInventoryChanged();

        Assert.Single(h.AutoDeposits);
    }

    [Fact]
    public void AutoDeposit_ReArmsOnlyWhenBothGatesBelow()
    {
        using Harness h = new();
        h.Settings.AutoDepositIfWealthExceeds = 100;
        h.Settings.AutoDepositIfCoinsExceed = 200;
        h.Settings.BankRoomKey = "bank";

        // wealth 150 > 100 fires; count 150 < 200.
        h.Snapshot = Wealth(150, copper: 150);
        h.Cash.OnInventoryChanged();
        Assert.Single(h.AutoDeposits);

        // Both below → re-armed.
        h.Snapshot = Wealth(20, copper: 20);
        h.Cash.OnInventoryChanged();
        // wealth 170 > 100 → re-fires.
        h.Snapshot = Wealth(170, copper: 170);
        h.Cash.OnInventoryChanged();
        Assert.Equal(2, h.AutoDeposits.Count);
    }

    [Fact]
    public void AutoDeposit_StaysFiredWhileEitherGateAbove()
    {
        using Harness h = new();
        h.Settings.AutoDepositIfWealthExceeds = 1000;
        h.Settings.AutoDepositIfCoinsExceed = 10;
        h.Settings.BankRoomKey = "bank";

        h.Snapshot = Wealth(50, copper: 50);  // count 50 > 10 → fires
        h.Cash.OnInventoryChanged();
        Assert.Single(h.AutoDeposits);

        h.Snapshot = Wealth(45, copper: 45);  // count 45 still > 10 → no re-arm
        h.Cash.OnInventoryChanged();
        h.Snapshot = Wealth(50, copper: 50);  // count 50 again
        h.Cash.OnInventoryChanged();
        Assert.Single(h.AutoDeposits);
    }

    // ----- reset tallies ----------------------------------------------

    [Fact]
    public void ResetTallies_ClearsHeldCoin()
    {
        using Harness h = new();
        h.Feed("You picked up 50 gold pieces.");
        h.Cash.ResetTallies();

        Assert.Equal(0, h.Cash.HeldCoin("gold"));
    }

    // ----- stash-room foundation (hide pattern) ----------------------

    [Fact]
    public void Hidden_DecrementsHeldCoin()
    {
        // Stash room visits run `hide N <coin>` — server replies
        // "You hid N <coin> pieces.". Without subscribing here the
        // held tally goes stale and AutoDeposit misfires.
        using Harness h = new();
        h.Feed("You picked up 50 gold pieces.");
        h.Feed("You hid 30 gold pieces.");

        Assert.Equal(20, h.Cash.HeldCoin("gold"));
    }

    [Fact]
    public void Hidden_Singular_DecrementsByOne()
    {
        using Harness h = new();
        h.Feed("You picked up 5 gold pieces.");
        h.Feed("You hid a gold piece.");

        Assert.Equal(4, h.Cash.HeldCoin("gold"));
    }

    // ----- Discard auto-drop -----------------------------------------

    [Fact]
    public void Discard_PickedUpFlaggedCurrency_DropsImmediately()
    {
        using Harness h = new();
        h.Settings.CopperPolicy = CashPolicy.Discard;

        h.Feed("You picked up 50 copper pieces.");

        List<string> lines = h.Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();
        Assert.Contains("drop 50 copper", lines);
    }

    [Fact]
    public void Discard_OnSettingsChange_DropsHeld()
    {
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.Feed("You picked up 100 gold pieces.");
        Assert.Equal(100, h.Cash.HeldCoin("gold"));
        h.Sent.Clear();

        h.Settings.GoldPolicy = CashPolicy.Discard;
        h.Cash.OnSettingsChanged();

        List<string> lines = h.Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();
        Assert.Contains("drop 100 gold", lines);
    }

    [Fact]
    public void Discard_NoHeldOfFlaggedCurrency_NoDrop()
    {
        using Harness h = new();
        h.Settings.CopperPolicy = CashPolicy.Discard;
        // No copper held.
        h.Cash.OnSettingsChanged();

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Discard_Disabled_NoDrop()
    {
        using Harness h = new() { AutoGetCashEnabled = false };
        h.Settings.CopperPolicy = CashPolicy.Discard;
        // Tally adjusted via test seam — pick-up doesn't fire because
        // the master is off.
        h.Feed("You picked up 50 copper pieces.");
        Assert.Empty(h.Sent);
    }

    // ----- settings-changed reapply ----------------------------------

    [Fact]
    public void OnSettingsChanged_ReEvaluatesAutoDeposit()
    {
        // User edits threshold while already holding wealth above the new
        // value. Without OnSettingsChanged() the trigger waits for the next
        // inventory change — could be a long time.
        using Harness h = new();
        h.Snapshot = Wealth(200);
        h.Cash.OnInventoryChanged();
        Assert.Empty(h.AutoDeposits);                // no threshold / location yet

        h.Settings.AutoDepositIfWealthExceeds = 100;
        h.Settings.BankRoomKey = "bank";
        h.Cash.OnSettingsChanged();

        Assert.Single(h.AutoDeposits);
        Assert.Equal(200, h.AutoDeposits[0]);
    }

    // ----- You-notice room survey (realm-specific format) -----------

    [Fact]
    public void YouNotice_SingleLine_DispatchesCashEntries()
    {
        // Mirrors the user's smoke-test wire output:
        //   "You notice 56 silver nobles, 198 copper farthings here."
        using Harness h = new();
        h.Settings.SilverPolicy = CashPolicy.Collect;
        h.Settings.CopperPolicy = CashPolicy.Ignore;

        h.Feed("You notice 56 silver nobles, 198 copper farthings here.");

        Assert.Equal(2, h.Dispatches.Count);
        Assert.Contains(h.Dispatches, d => d.Currency == "silver" && d.Count == 56);
        Assert.Contains(h.Dispatches, d => d.Currency == "copper" && d.Count == 198);
        // Silver is Collect → `get 56 silver`. Copper is Ignore.
        List<string> lines = h.Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();
        Assert.Contains("get 56 silver", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("get") && l.Contains("copper"));
    }

    [Fact]
    public void YouNotice_WithItems_SkipsItems_ParsesOnlyCash()
    {
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;

        h.Feed("You notice 5 gold sovereigns, a longsword, a potion of healing here.");

        Assert.Single(h.Dispatches);
        Assert.Equal("gold", h.Dispatches[0].Currency);
        Assert.Equal(5, h.Dispatches[0].Count);
    }

    [Fact]
    public void YouNotice_MultiLine_Wrap_StitchesAndParses()
    {
        // 80-col wrap mid-list, just like Also-Here. Requires the
        // LineExtractor buffer-path.
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.Settings.SilverPolicy = CashPolicy.Collect;

        Terminal.LineExtractor lines = new(new FujinTerm.Terminal.TerminalEmulator(80, 24));
        h.Cash.AttachLineExtractor(lines);

        FeedLine(lines, "You notice 5 gold sovereigns, 10 silver nobles, a longsword, a shield, a potion of");
        FeedLine(lines, "healing here.");

        Assert.Equal(2, h.Dispatches.Count);
        Assert.Contains(h.Dispatches, d => d.Currency == "gold" && d.Count == 5);
        Assert.Contains(h.Dispatches, d => d.Currency == "silver" && d.Count == 10);
    }

    // ----- Corpse loot (monster kill drops) ---------------------------

    [Fact]
    public void CorpseDrop_CollectsViaSameDispatch()
    {
        // Per the user's screenshot — after a kill the server emits
        // "N <currency> drop to the ground." lines, distinct shape
        // from the room-display CashOnGround. Should funnel into the
        // same policy decision as room-display cash.
        using Harness h = new();
        h.Settings.SilverPolicy = CashPolicy.Collect;

        h.Feed("7 silver drop to the ground.");

        Assert.Single(h.Sent);
        Assert.Equal("get 7 silver", h.LastSent);
        Assert.Single(h.Dispatches);
        Assert.Equal(("silver", 7, CashPolicy.Collect), h.Dispatches[0]);
    }

    [Fact]
    public void CorpseDrop_HonoursIgnorePolicy()
    {
        using Harness h = new();
        h.Settings.CopperPolicy = CashPolicy.Ignore;

        h.Feed("11 copper drop to the ground.");

        Assert.Empty(h.Sent);
        Assert.Single(h.Dispatches);
        Assert.Equal(CashPolicy.Ignore, h.Dispatches[0].Policy);
    }

    [Fact]
    public void CorpseDrop_AutoGetCashOff_NoDispatch()
    {
        using Harness h = new() { AutoGetCashEnabled = false };
        h.Settings.GoldPolicy = CashPolicy.Collect;

        h.Feed("25 gold drop to the ground.");

        Assert.Empty(h.Sent);
        Assert.Empty(h.Dispatches);
    }

    [Fact]
    public void CorpseDrop_UnknownCurrency_NoOp()
    {
        // Realm-specific name not in our denomination table — silent
        // skip (no Ignore dispatch either; we don't recognise it as
        // cash at all).
        using Harness h = new();
        h.Feed("50 zorkmid drop to the ground.");

        Assert.Empty(h.Sent);
        Assert.Empty(h.Dispatches);
    }

    // ----- encumbrance gate + cascade --------------------------------

    /// <summary>Build a snapshot with given per-coin counts + a numeric
    /// encumbrance reading; TotalCopperValue is irrelevant to the gate so
    /// it's left zero.</summary>
    private static InventorySnapshot Snap(
        int copper, int silver, int gold, int platinum, int runic,
        int currentWeight, int maxWeight)
    {
        return new InventorySnapshot(
            new CurrencyHoldings(copper, silver, gold, platinum, runic, 0),
            new EncumbranceReading(currentWeight, maxWeight, 0, EncumbranceLevel.None),
            Array.Empty<EquippedItem>(),
            Array.Empty<string>(),
            DateTimeOffset.UtcNow);
    }

    /// <summary>Build a holdings snapshot for the auto-deposit gates:
    /// <paramref name="wealthValue"/> is the game's Wealth: figure
    /// (TotalCopperValue); the coin counts drive TotalCoinCount. Encumbrance
    /// is irrelevant to the gates so it's left empty.</summary>
    private static InventorySnapshot Wealth(
        long wealthValue,
        int copper = 0, int silver = 0, int gold = 0, int platinum = 0, int runic = 0)
    {
        return new InventorySnapshot(
            new CurrencyHoldings(copper, silver, gold, platinum, runic, wealthValue),
            EncumbranceReading.Empty,
            Array.Empty<EquippedItem>(),
            Array.Empty<string>(),
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Gate_NoFlag_CollectsFullEvenWithSnapshot()
    {
        // A populated snapshot but no SkipCollectIfMakes* flag → gate
        // inert, full pickup as v1.
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.Snapshot = Snap(0, 0, 0, 0, 0, currentWeight: 0, maxWeight: 1000);

        h.Feed("There are 1000 gold pieces here.");

        Assert.Equal("get 1000 gold", h.LastSent);
    }

    [Fact]
    public void Gate_Light_ClampsPickupToHeadroom()
    {
        // MaxWeight 1000, Light starts at 17%. GateBoundaryCap = 169 weight
        // units → budget 507 coins (169*3). Collect clamps to 507 copper.
        using Harness h = new();
        h.Settings.CopperPolicy = CashPolicy.Collect;
        h.Settings.SkipCollectIfMakesLight = true;
        h.Snapshot = Snap(0, 0, 0, 0, 0, currentWeight: 0, maxWeight: 1000);

        h.Feed("There are 1000 copper pieces here.");

        Assert.Equal("get 507 copper", h.LastSent);
    }

    [Fact]
    public void Gate_AlreadyOverBracket_SkipsPickup()
    {
        // Current weight 20% of max already over the Light gate → zero
        // budget, no wire send.
        using Harness h = new();
        h.Settings.CopperPolicy = CashPolicy.Collect;
        h.Settings.SkipCollectIfMakesLight = true;
        h.Snapshot = Snap(0, 0, 0, 0, 0, currentWeight: 200, maxWeight: 1000);

        h.Feed("There are 1000 copper pieces here.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Gate_Cascade_DropsSmallerForLarger()
    {
        // Hold 300 copper (100 weight units). MaxWeight 1000, Light gate →
        // cap 169 → budget 207 gold. Want 300 gold: 207 free, 93 short.
        // Cascade drops 93 copper (encumbrance-neutral) to pick up the
        // full 300 gold.
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.Settings.SkipCollectIfMakesLight = true;
        h.Settings.DropSmallerForLarger = true;
        h.Snapshot = Snap(300, 0, 0, 0, 0, currentWeight: 100, maxWeight: 1000);

        h.Feed("There are 300 gold pieces here.");

        Assert.Equal(new[] { "drop 93 copper", "get 300 gold" }, h.AllSent);
    }

    [Fact]
    public void Gate_InFlightDelta_ThreadsConsecutiveBatches()
    {
        // First collect clamps to 507 copper (Light gate, max 1000). The
        // in-flight delta now projects 507 held copper; a second identical
        // ground line (before the pickup confirms) sees no remaining
        // headroom and skips.
        using Harness h = new();
        h.Settings.CopperPolicy = CashPolicy.Collect;
        h.Settings.SkipCollectIfMakesLight = true;
        h.Snapshot = Snap(0, 0, 0, 0, 0, currentWeight: 0, maxWeight: 1000);

        h.Feed("There are 1000 copper pieces here.");
        Assert.Equal("get 507 copper", h.LastSent);

        h.Feed("There are 1000 copper pieces here.");
        Assert.Single(h.Sent);   // second batch gated out by the in-flight projection
    }

    private static void FeedLine(Terminal.LineExtractor lines, string text)
    {
        System.Reflection.FieldInfo? field = typeof(Terminal.LineExtractor)
            .GetField("LineEmitted",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
        if (field?.GetValue(lines) is Action<Terminal.LineExtractor.EmittedLine> handler)
        {
            handler(new Terminal.LineExtractor.EmittedLine(
                text, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));
        }
    }
}
