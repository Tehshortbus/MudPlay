using System.Text;
using MudPlay.Game;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// CashManager per-currency policy dispatch, the pick-up / drop / hide lines,
// and the auto-deposit threshold trigger.
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
        public bool PeekSuppressed { get; set; }
        // Room still holds engageable hostiles — drives the collect-after-combat
        // defer path. Defaults false so the existing tests keep the immediate
        // collect behaviour.
        public bool HasHostiles { get; set; }
        // Default Empty → MaxWeight 0 → encumbrance gate inert, so the
        // non-gate tests run the v1 full-pickup path unchanged. Gate
        // tests set a populated snapshot before feeding the cash line.
        public InventorySnapshot Snapshot { get; set; } = InventorySnapshot.Empty;
        // Item-table tiebreaker double. Null (default) → unwired, the
        // count+denomination heuristic stands alone. Read lazily inside the
        // injected lambda so a test can set it after construction.
        public Func<string, bool>? KnownItem { get; set; }
        public List<long> AutoDeposits { get; } = new();
        public List<(string Currency, int Count)> Collected { get; } = new();
        public List<(string Currency, int Count)> Hidden { get; } = new();
        // When false (default) posted actions run inline, so existing tests see
        // the collect synchronously. Set true to capture them (room-survey race
        // tests) and run them on demand via RunPosts().
        public bool DeferPosts { get; set; }
        public List<Action> Posts { get; } = new();
        // The clocks behind the unanswered-get timing and the auto-deposit cooldown.
        public Func<DateTime> UnansweredNow { get; set; } = () => DateTime.UtcNow;
        public Func<DateTime> AutoDepositNow { get; set; } = () => DateTime.UtcNow;

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Cash = new CashManager(Router,
                readSettings: () => Settings,
                isEnabled: () => AutoGetCashEnabled,
                collectAfterCombatFinished: () => Settings.CollectAfterCombatFinished,
                hasEngageableHostiles: () => HasHostiles,
                getSnapshot: () => Snapshot,
                isPeekSuppressed: () => PeekSuppressed,
                log: Log,
                isKnownItem: entry => KnownItem?.Invoke(entry) ?? false,
                post: a => { if (DeferPosts) Posts.Add(a); else a(); },
                unansweredClock: () => UnansweredNow(),
                autoDepositClock: () => AutoDepositNow());
            Cash.SetWireSender(b => Sent.Add(b));
            Cash.AutoDepositRequested += t => AutoDeposits.Add(t);
            Cash.CoinCollected += (c, n) => Collected.Add((c, n));
            Cash.CoinHidden += (c, n) => Hidden.Add((c, n));
        }

        public void Feed(string line)
        {
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));
        }

        // Run + clear any captured posted actions (see DeferPosts).
        public void RunPosts()
        {
            List<Action> snap = Posts.ToList();
            Posts.Clear();
            foreach (Action a in snap) a();
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
        Assert.Equal("get 50 gold crown", h.LastSent);
    }

    [Fact]
    public void OnGround_Collect_Suppressed_InStashRoomWhileLooping()
    {
        // Report paradigm-20260819-121516: a search in a stash room re-exposes the
        // just-hidden pile and auto-collect grabs it back. In a stash room mid-loop
        // the collect must be skipped — no `get` sent — even under Collect policy.
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.Cash.SuppressCollectInStashRoom = () => true;

        h.Feed("There are 50 gold pieces here.");

        Assert.Empty(h.Sent);   // no `get` — the pile stays stashed
    }

    [Fact]
    public void YouNotice_Collect_Suppressed_InStashRoomWhileLooping()
    {
        // Report paradigm-20260820-055720: auto-search's `sea` re-renders the just-
        // stashed pile as a "You notice N here" SURVEY line, which reached the collect
        // funnel by a path the per-handler guard didn't cover — so the gold got grabbed
        // right back. The funnel guard must suppress this path too.
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.Cash.SuppressCollectInStashRoom = () => true;

        h.Feed("You notice 50 gold crowns here.");

        Assert.Empty(h.Sent);   // no `get` — even via the room-survey path
    }

    [Fact]
    public void OnGround_Singular_CollectsAsCountOne()
    {
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;

        h.Feed("There is a gold piece here.");

        Assert.Single(h.Sent);
        Assert.Equal("get 1 gold crown", h.LastSent);
    }

    // Every get / drop currency command names the coin in full (two words), not
    // the bare adjective — MajorMUD binds "get 1 silver" to a silver ring
    // instead of the silver-noble coins. One case per denomination pins the
    // outgoing wire noun. Runic is board-configurable; the harness uses stock
    // "runic coin".
    [Theory]
    [InlineData("copper",   "get 5 copper farthing")]
    [InlineData("silver",   "get 5 silver noble")]
    [InlineData("gold",     "get 5 gold crown")]
    [InlineData("platinum", "get 5 platinum piece")]
    [InlineData("runic",    "get 5 runic coin")]
    public void Collect_EmitsTwoWordCoinNoun(string currency, string expected)
    {
        using Harness h = new();
        h.Settings.CopperPolicy = CashPolicy.Collect;
        h.Settings.SilverPolicy = CashPolicy.Collect;
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.Settings.PlatinumPolicy = CashPolicy.Collect;
        h.Settings.RunicPolicy = CashPolicy.Collect;

        h.Feed($"5 {currency} drop to the ground.");

        Assert.Equal(expected, h.LastSent);
    }

    [Fact]
    public void OnGround_Ignore_NoWireSend()
    {
        using Harness h = new();
        h.Settings.CopperPolicy = CashPolicy.Ignore;

        h.Feed("There are 50 copper pieces here.");

        Assert.Empty(h.Sent);
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
    }

    // ----- master switch ----------------------------------------------

    [Fact]
    public void AutoGetCashOff_NoDispatch()
    {
        using Harness h = new() { AutoGetCashEnabled = false };
        h.Settings.GoldPolicy = CashPolicy.Collect;

        h.Feed("There are 50 gold pieces here.");

        Assert.Empty(h.Sent);
    }

    // ----- pickups, drops + auto-deposit ------------------------------

    // A Discard drop sends nothing more for that coin until the server answers it.
    // The drop line is the answer, so the next audit drops what's still carried.
    [Fact]
    public void Dropped_AnswersTheDiscardDrop()
    {
        using Harness h = new();
        h.Snapshot = Wealth(5000, gold: 50);
        h.Settings.GoldPolicy = CashPolicy.Discard;
        h.Cash.OnSettingsChanged();
        h.Cash.OnInventoryChanged();             // unanswered: no second drop
        Assert.Equal(new[] { "drop 50 gold crown" }, h.AllSent);

        h.Feed("You dropped 20 gold pieces.");
        h.Cash.OnInventoryChanged();

        Assert.Equal(new[] { "drop 50 gold crown", "drop 50 gold crown" }, h.AllSent);
    }

    // Full coin name on the drop / hide confirmations (live wording).
    [Fact]
    public void DroppedAndHidden_FullCoinName_AreCoinLines()
    {
        using Harness h = new();
        h.Snapshot = Wealth(5000, gold: 50);
        h.Settings.GoldPolicy = CashPolicy.Discard;
        h.Cash.OnSettingsChanged();

        h.Feed("You dropped 5 gold crowns.");
        h.Feed("You hid 10 gold crowns.");
        h.Cash.OnInventoryChanged();

        Assert.Equal(new[] { "drop 50 gold crown", "drop 50 gold crown" }, h.AllSent);
        Assert.Equal(("gold", 10), Assert.Single(h.Hidden));
    }

    [Fact]
    public void PickedUp_RaisesCoinCollected()
    {
        using Harness h = new();
        h.Feed("You picked up 50 gold pieces.");

        Assert.Equal(("gold", 50), Assert.Single(h.Collected));
    }

    // Live wire wording: the pickup line names the coin in full and carries
    // NO trailing period ("You picked up 6 silver nobles"). The old pattern
    // required a literal "pieces" + period, so real pickups never matched and
    // Session Stats currency stayed at zero. One case per denomination pins
    // every full coin name.
    [Theory]
    [InlineData("You picked up 6 silver nobles", "silver", 6)]
    [InlineData("You picked up 1 silver noble", "silver", 1)]
    [InlineData("You picked up 4 copper farthings", "copper", 4)]
    [InlineData("You picked up 12 gold crowns", "gold", 12)]
    [InlineData("You picked up 3 platinum pieces", "platinum", 3)]
    [InlineData("You picked up 2 runic coins", "runic", 2)]
    public void PickedUp_FullCoinName_NoPeriod_RaisesCoinCollected(
        string line, string currency, int count)
    {
        using Harness h = new();
        h.Feed(line);

        Assert.Equal((currency, count), Assert.Single(h.Collected));
    }

    // Item drops / hides share the "You dropped" / "You hid" verb with coin
    // lines; the coin-noun anchor keeps a colour-adjective item ("a silver
    // key") from being counted as currency.
    [Fact]
    public void Dropped_ItemNamedLikeCoin_IsNotTheDiscardDrop()
    {
        using Harness h = new();
        h.Snapshot = Wealth(1000, silver: 10);
        h.Settings.SilverPolicy = CashPolicy.Discard;
        h.Cash.OnSettingsChanged();

        h.Feed("You dropped a silver key.");
        h.Cash.OnInventoryChanged();

        Assert.Equal(new[] { "drop 10 silver noble" }, h.AllSent);
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

    // A reroute that never deposited must re-arm the gate so it retries — but
    // not on the very next inventory line. Regression: the single-fire guard
    // latched on fire and only cleared when wealth fell below threshold, which a
    // never-completed deposit never reaches, so auto-deposit stayed wedged for
    // the whole session (reported: bank room configured, wealth over threshold,
    // no bank trip ever ran).
    [Fact]
    public void AutoDeposit_NotifyAborted_ReArmsAfterCooldown()
    {
        using Harness h = new();
        h.Settings.AutoDepositIfWealthExceeds = 100;
        h.Settings.BankRoomKey = "bank";

        DateTime now = DateTime.UtcNow;
        h.AutoDepositNow = () => now;

        h.Snapshot = Wealth(150);
        h.Cash.OnInventoryChanged();
        Assert.Single(h.AutoDeposits);

        // Reroute couldn't deposit → subscriber re-arms the gate.
        h.Cash.NotifyAutoDepositAborted();

        // Still within the cooldown: an immediate re-check must not re-fire (no
        // engine thrash while the bank stays unreachable).
        h.Snapshot = Wealth(160);
        h.Cash.OnInventoryChanged();
        Assert.Single(h.AutoDeposits);

        // Past the cooldown: the next inventory change retries.
        now = now.AddMinutes(2);
        h.Snapshot = Wealth(170);
        h.Cash.OnInventoryChanged();
        Assert.Equal(2, h.AutoDeposits.Count);
    }

    // The natural re-arm (both gates fall below threshold) clears any pending
    // retry hold, so a genuine deposit that dips wealth then climbs back fires
    // again immediately without waiting out a stale cooldown.
    [Fact]
    public void AutoDeposit_NaturalReArm_ClearsAbortCooldown()
    {
        using Harness h = new();
        h.Settings.AutoDepositIfWealthExceeds = 100;
        h.Settings.BankRoomKey = "bank";

        DateTime now = DateTime.UtcNow;
        h.AutoDepositNow = () => now;

        h.Snapshot = Wealth(150);
        h.Cash.OnInventoryChanged();
        Assert.Single(h.AutoDeposits);

        h.Cash.NotifyAutoDepositAborted();   // arms a cooldown

        // Wealth drops below threshold → natural re-arm clears the hold.
        h.Snapshot = Wealth(30);
        h.Cash.OnInventoryChanged();

        // Back above threshold, still inside the original cooldown window — must
        // fire immediately because the natural re-arm dropped the hold.
        h.Snapshot = Wealth(130);
        h.Cash.OnInventoryChanged();
        Assert.Equal(2, h.AutoDeposits.Count);
    }

    // ----- reset tallies ----------------------------------------------

    // A new character starts with the auto-deposit gate armed again.
    [Fact]
    public void ResetTallies_RearmsAutoDeposit()
    {
        using Harness h = new();
        h.Settings.AutoDepositIfWealthExceeds = 100;
        h.Settings.BankRoomKey = "bank";
        h.Snapshot = Wealth(150);
        h.Cash.OnInventoryChanged();

        h.Cash.ResetTallies();
        h.Cash.OnInventoryChanged();

        Assert.Equal(2, h.AutoDeposits.Count);
    }

    // ----- stash-room foundation (hide pattern) ----------------------

    [Fact]
    public void Hidden_Singular_IsOneCoin()
    {
        using Harness h = new();
        h.Feed("You picked up 5 gold pieces.");
        h.Feed("You hid a gold piece.");

        Assert.Equal(("gold", 1), Assert.Single(h.Hidden));
    }

    // The ledger sources coin stashes off the hide echo, so a manual `hide N <coin>` (not just an auto stash) reaches the
    // transaction history. CoinHidden carries the normalized currency + count.
    [Fact]
    public void Hidden_RaisesCoinHidden()
    {
        using Harness h = new();
        h.Feed("You picked up 50 gold pieces.");
        h.Feed("You hid 30 gold pieces.");

        Assert.Equal(("gold", 30), Assert.Single(h.Hidden));
    }

    // Paradigm echoes a counted item hide by the item's bare name, and some items
    // are named after a coin. That is an item leaving the pack, not coin.
    [Fact]
    public void ItemNamedAfterACoin_Hidden_IsNotCoin()
    {
        using Harness h = new();
        h.Feed("You picked up 50 gold pieces.");
        h.Feed("You hid 3 iron crown.");

        Assert.Empty(h.Hidden);
    }

    // ----- Discard auto-drop -----------------------------------------

    // A Discard currency picked up drops once the inventory snapshot has it — the
    // pickup line and the inventory change after it send one drop between them.
    [Fact]
    public void Discard_PickedUpFlaggedCurrency_DropsOnce()
    {
        using Harness h = new();
        h.Settings.CopperPolicy = CashPolicy.Discard;

        h.Feed("You picked up 50 copper pieces.");
        h.Snapshot = Wealth(50, copper: 50);
        h.Cash.OnInventoryChanged();
        h.Cash.OnInventoryChanged();

        Assert.Equal(new[] { "drop 50 copper farthing" }, h.AllSent);
    }

    // Report paradigm-20260929-183240: the session pickup tally said 2182 copper,
    // `i` said 671; the drop must follow the inventory, not the tally.
    [Fact]
    public void Discard_DropsWhatInventorySays_NotThePickupTally()
    {
        using Harness h = new();
        h.Feed("You picked up 2182 copper pieces.");
        h.Snapshot = Wealth(671, copper: 671);

        h.Settings.CopperPolicy = CashPolicy.Discard;
        h.Cash.OnSettingsChanged();

        Assert.Equal(new[] { "drop 671 copper farthing" }, h.AllSent);
    }

    // A refused drop means our counts are stale: re-read the inventory, and let the
    // next audit drop again (at most one `i` per cooldown).
    [Fact]
    public void Discard_RefusedDrop_ReReadsInventoryAndRetries()
    {
        using Harness h = new();
        h.Snapshot = Wealth(2182, copper: 2182);
        h.Settings.CopperPolicy = CashPolicy.Discard;
        h.Cash.OnSettingsChanged();

        h.Feed("You don't have 2182 copper farthing to drop!");
        h.Feed("You don't have 2182 copper farthing to drop!");
        h.Snapshot = Wealth(671, copper: 671);
        h.Cash.OnInventoryChanged();

        Assert.Equal(new[] { "drop 2182 copper farthing", "i", "drop 671 copper farthing" }, h.AllSent);
    }

    [Fact]
    public void Discard_OnSettingsChange_DropsHeld()
    {
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.Feed("You picked up 100 gold pieces.");
        h.Snapshot = Wealth(100_000, gold: 100);
        h.Sent.Clear();

        h.Settings.GoldPolicy = CashPolicy.Discard;
        h.Cash.OnSettingsChanged();

        List<string> lines = h.Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();
        Assert.Contains("drop 100 gold crown", lines);
    }

    // A currency flipped Collect→Discard must drop coin the character is
    // actually carrying — including a starting / carried-over balance the
    // session pickup tally never observed. Regression: 71 carried copper stayed
    // put after the flip because the audit consulted only the pickup tally
    // (zero for coin it never saw picked up) instead of the authoritative
    // inventory snapshot.
    [Fact]
    public void Discard_OnSettingsChange_DropsCarriedCoinFromSnapshot()
    {
        using Harness h = new();
        // Snapshot shows 71 copper on hand, but no CashPickedUp line ever ran.
        h.Snapshot = Wealth(71, copper: 71);

        h.Settings.CopperPolicy = CashPolicy.Discard;
        h.Cash.OnSettingsChanged();

        Assert.Contains("drop 71 copper farthing", h.AllSent);
    }

    // Discard drops name the coin in full too, so a bare-adjective drop can't
    // ditch a like-named item (a silver ring for silver-noble coins). One case
    // per denomination, driven off the carried-coin snapshot.
    [Theory]
    [InlineData("copper",   "drop 8 copper farthing")]
    [InlineData("silver",   "drop 8 silver noble")]
    [InlineData("gold",     "drop 8 gold crown")]
    [InlineData("platinum", "drop 8 platinum piece")]
    [InlineData("runic",    "drop 8 runic coin")]
    public void Discard_EmitsTwoWordCoinNoun(string currency, string expected)
    {
        using Harness h = new();
        h.Snapshot = currency switch
        {
            "copper"   => Wealth(8, copper: 8),
            "silver"   => Wealth(8, silver: 8),
            "gold"     => Wealth(8, gold: 8),
            "platinum" => Wealth(8, platinum: 8),
            _          => Wealth(8, runic: 8),
        };
        h.Settings.CopperPolicy = CashPolicy.Discard;
        h.Settings.SilverPolicy = CashPolicy.Discard;
        h.Settings.GoldPolicy = CashPolicy.Discard;
        h.Settings.PlatinumPolicy = CashPolicy.Discard;
        h.Settings.RunicPolicy = CashPolicy.Discard;
        h.Cash.OnSettingsChanged();

        Assert.Contains(expected, h.AllSent);
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
        h.Snapshot = Wealth(50, copper: 50);

        h.Cash.OnInventoryChanged();
        Assert.Empty(h.Sent);

        h.AutoGetCashEnabled = true;               // the switch is the only difference
        h.Cash.OnInventoryChanged();
        Assert.Equal(new[] { "drop 50 copper farthing" }, h.AllSent);
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

        // Silver is Collect → `get 56 silver noble`. Copper is Ignore.
        List<string> lines = h.Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();
        Assert.Contains("get 56 silver noble", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("get") && l.Contains("copper"));
    }

    // ----- A collect ceiling (an errand that wants a set sum) -------------

    private static List<string> Gets(Harness h) =>
        h.Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).Where(l => l.StartsWith("get")).ToList();

    // The train-funding stash stop took the whole revealed pile and left the
    // character at 100% encumbrance (report paradigm-20261001-222332). The errand now
    // holds the pickup at zero, reads the pile, and then asks for a set sum of it.
    [Fact]
    public void CollectLimit_Zero_HoldsThePile_ButRemembersWhatItShowed()
    {
        using Harness h = new();
        h.Settings.PlatinumPolicy = CashPolicy.Collect;
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.Settings.SilverPolicy = CashPolicy.Collect;
        h.Cash.SetCollectLimit(0);

        h.Feed("You notice 5 platinum pieces, 154 gold crowns, 6818 silver nobles here.");

        Assert.Empty(Gets(h));
        Assert.Equal(5 * 10_000 + 154 * 100 + 6818 * 10, h.Cash.SurveyedCopperUnderLimit);

        // The same pile shown again replaces the count; it isn't added twice.
        h.Feed("You notice 5 platinum pieces, 154 gold crowns, 6818 silver nobles here.");
        Assert.Equal(5 * 10_000 + 154 * 100 + 6818 * 10, h.Cash.SurveyedCopperUnderLimit);
    }

    [Fact]
    public void CollectSurveyed_TakesTheSum_DearestCoinFirst_AndLeavesTheRest()
    {
        using Harness h = new();
        h.Cash.SetCollectLimit(0);
        h.Feed("You notice 5 platinum pieces, 154 gold crowns, 6818 silver nobles here.");

        h.Cash.CollectSurveyed(25_050);

        // 25,050 copper rounds up to 3 platinum (30,000): the fewest, lightest coins
        // that cover it. The gold and silver stay where they are.
        Assert.StartsWith("get 3 platinum", Assert.Single(Gets(h)));
    }

    [Fact]
    public void CollectSurveyed_PileSmallerThanTheSum_SpillsIntoTheNextCoin()
    {
        using Harness h = new();
        h.Cash.SetCollectLimit(0);
        h.Feed("You notice 2 platinum pieces, 154 gold crowns here.");

        h.Cash.CollectSurveyed(25_050);

        List<string> gets = Gets(h);
        Assert.Equal(2, gets.Count);
        Assert.StartsWith("get 2 platinum", gets[0]);   // all there is: 20,000
        Assert.StartsWith("get 51 gold", gets[1]);      // the other 5,050, rounded up
    }

    // Drawing on our own stash for a bill isn't floor loot, so the per-coin Collect /
    // Discard / Ignore rules don't keep a coin back.
    [Fact]
    public void CollectSurveyed_TakesACoinWhateverItsPickupRule()
    {
        using Harness h = new();
        h.Settings.SilverPolicy = CashPolicy.Ignore;
        h.Cash.SetCollectLimit(0);
        h.Feed("You notice 80 silver nobles here.");

        h.Cash.CollectSurveyed(500);

        Assert.StartsWith("get 50 silver", Assert.Single(Gets(h)));
    }

    // A stash transfer reads this to tell a purse that should be banked first.
    [Fact]
    public void CoinLoad_IsTheCoinsHeldAndTheRoomTheWeightLimitsLeave()
    {
        using Harness h = new();
        Assert.Null(h.Cash.CoinLoad());                   // capacity unknown

        // 6,977 coins weigh 2,325; with 1,035 of gear that is the 3,360 maximum.
        h.Snapshot = Snap(0, 6955, 22, 0, 0, currentWeight: 3360, maxWeight: 3360);
        Assert.Equal((6977L, 0L), h.Cash.CoinLoad());

        // 300 coins weigh 100; 2,260 of weight is free, room for 6,780 more coins.
        h.Snapshot = Snap(0, 300, 0, 0, 0, currentWeight: 1100, maxWeight: 3360);
        Assert.Equal((300L, 6780L), h.Cash.CoinLoad());
    }

    [Fact]
    public void CollectLimit_Lifted_CollectsEverythingAgain()
    {
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.Cash.SetCollectLimit(0);
        h.Feed("You notice 40 gold crowns here.");
        Assert.Empty(Gets(h));

        h.Cash.SetCollectLimit(null);
        Assert.Equal(0, h.Cash.SurveyedCopperUnderLimit);
        h.Feed("You notice 41 gold crowns here.");
        Assert.Contains(Gets(h), l => l.StartsWith("get 41 gold"));
    }

    [Fact]
    public void YouNotice_CollectAfterCombat_HostileRevealsAfterCashLine_Defers()
    {
        // Report stock-20260730-193107: the room's "You notice ... here." cash line
        // is parsed BEFORE its "Also here: <monsters>" line, so at notice time no
        // hostile is known yet. With collect-after-combat on, the collect decision
        // is posted a tick; by then the Also-here has revealed the hostile, so the
        // get must be held — not sent into a room that's about to start combat.
        using Harness h = new() { DeferPosts = true };
        h.Settings.CopperPolicy = CashPolicy.Collect;
        h.Settings.CollectAfterCombatFinished = true;

        h.Feed("You notice 11 copper farthings here.");
        Assert.Empty(h.Sent);            // decision deferred to the post — nothing sent yet
        Assert.Single(h.Posts);

        h.HasHostiles = true;            // the room's Also-here revealed a hostile
        h.RunPosts();

        Assert.Empty(h.Sent);            // held for post-combat, no premature `get`
    }

    [Fact]
    public void YouNotice_CollectAfterCombat_ClearRoom_CollectsAfterPost()
    {
        // Companion to the above: when the post fires and no hostile revealed, the
        // room is genuinely clear, so the deferred decision collects normally.
        using Harness h = new() { DeferPosts = true };
        h.Settings.CopperPolicy = CashPolicy.Collect;
        h.Settings.CollectAfterCombatFinished = true;

        h.Feed("You notice 11 copper farthings here.");
        Assert.Empty(h.Sent);
        h.RunPosts();                    // no hostile → collect

        Assert.Single(h.Sent);
        Assert.Equal("get 11 copper farthing", h.LastSent);
    }

    [Fact]
    public void YouNotice_WithItems_SkipsItems_ParsesOnlyCash()
    {
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;

        h.Feed("You notice 5 gold sovereigns, a longsword, a potion of healing here.");

        Assert.Equal(new[] { "get 5 gold crown" }, h.AllSent);
    }

    [Fact]
    public void YouNotice_MultiLine_Wrap_StitchesAndParses()
    {
        // 80-col wrap mid-list, just like Also-Here. Requires the
        // LineExtractor buffer-path.
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.Settings.SilverPolicy = CashPolicy.Collect;

        ServerFeed feed = new();
        h.Cash.AttachLineExtractor(feed.Lines);

        feed.Line("You notice 5 gold sovereigns, 10 silver nobles, a longsword, a shield, a potion of");
        feed.Line("healing here.");

        Assert.Contains("get 5 gold crown", h.AllSent);
        Assert.Contains("get 10 silver noble", h.AllSent);
    }

    // A stacked item whose name starts with a denomination word ("2 gold key")
    // has the same "N <denom> ..." shape as a coin pile. With the item table
    // wired, the survey entry resolves to a real item, so it's NOT collected as
    // cash — no dispatch, no get.
    [Fact]
    public void YouNotice_DenominationNamedStackedItem_NotCollectedAsCash()
    {
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.KnownItem = KnownItems("gold key");   // table knows the item, not the coin noun

        h.Feed("You notice 2 gold key here.");

        Assert.Empty(h.Sent);
    }

    // The tiebreaker must not swallow a real coin pile: a "N <denom> ..." that
    // isn't in the item table still collects as cash.
    [Fact]
    public void YouNotice_CoinPile_NotInItemTable_StillCollected()
    {
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.KnownItem = KnownItems("gold key");   // "gold crown" absent → stays cash

        h.Feed("You notice 50 gold crowns here.");

        Assert.Equal("get 50 gold crown", h.LastSent);
    }

    // Report paradigm-20260811-065736: after a kill's corpse-drop is collected, a
    // post-combat `search` reveals a LARGER floor pile — the old name-only latch
    // skipped it ("already handled this room visit"), stranding the coins. The
    // amount-keyed latch re-collects when the reveal exceeds what we took, while a
    // re-render of the SAME pile stays a no-op (no re-collect spam).
    [Fact]
    public void SearchRevealsLargerPile_AfterCorpseCollect_ReCollects_ThenNoOpOnReRender()
    {
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;

        h.Feed("3 gold drop to the ground.");          // corpse-drop collected
        Assert.Equal("get 3 gold crown", h.LastSent);
        int afterCorpse = h.Sent.Count;

        h.Feed("You notice 5 gold crowns here.");       // search surfaces more → re-collect
        Assert.Equal("get 5 gold crown", h.LastSent);
        Assert.True(h.Sent.Count > afterCorpse, "a larger revealed pile must re-collect");

        int afterSearch = h.Sent.Count;
        h.Feed("You notice 5 gold crowns here.");       // same pile re-displayed → no-op
        Assert.Equal(afterSearch, h.Sent.Count);
    }

    // A denomination-named item mixed in with real coin: the item is skipped, the
    // coin is collected — the filter is per-entry, not all-or-nothing.
    [Fact]
    public void YouNotice_ItemAndCoin_CollectsOnlyCoin()
    {
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.KnownItem = KnownItems("gold key");

        h.Feed("You notice 2 gold key, 50 gold crowns here.");

        Assert.Contains("get 50 gold crown", h.AllSent);
    }

    // Test double for the item table: matches after the same article/count
    // stripping the real ItemNameStore.Normalize applies.
    private static Func<string, bool> KnownItems(params string[] names)
    {
        HashSet<string> set = new(names, StringComparer.OrdinalIgnoreCase);
        return entry =>
        {
            string[] words = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int skip = 0;
            if (words.Length > 0 && (words[0] is "a" or "an" or "the" or "some"
                    || int.TryParse(words[0], out _)))
                skip = 1;
            string key = string.Join(' ', words.Skip(skip));
            return set.Contains(key);
        };
    }

    [Fact]
    public void YouNotice_PeekSuppressed_NoCollect()
    {
        // A look-direction peek renders a full "You notice" survey for the
        // adjacent room; collecting from a room we never entered is the bug.
        using Harness h = new() { PeekSuppressed = true };
        h.Settings.SilverPolicy = CashPolicy.Collect;

        h.Feed("You notice 56 silver nobles here.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void YouNotice_PeekCleared_RealEntry_Collects()
    {
        using Harness h = new() { PeekSuppressed = true };
        h.Settings.SilverPolicy = CashPolicy.Collect;

        h.Feed("You notice 56 silver nobles here.");   // peeked — dropped
        Assert.Empty(h.Sent);

        h.PeekSuppressed = false;                       // walked in for real
        h.Feed("You notice 56 silver nobles here.");

        Assert.Contains("get 56 silver noble", h.AllSent);
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
        Assert.Equal("get 7 silver noble", h.LastSent);
    }

    [Fact]
    public void CorpseDrop_HonoursIgnorePolicy()
    {
        using Harness h = new();
        h.Settings.CopperPolicy = CashPolicy.Ignore;

        h.Feed("11 copper drop to the ground.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void CorpseDrop_AutoGetCashOff_NoDispatch()
    {
        using Harness h = new() { AutoGetCashEnabled = false };
        h.Settings.GoldPolicy = CashPolicy.Collect;

        h.Feed("25 gold drop to the ground.");

        Assert.Empty(h.Sent);
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
    }

    // ----- collect-after-combat defer --------------------------------

    // Toggle on + a live hostile: a corpse drop mid-fight must NOT get collected
    // between kills (that burns the next round's pre-attack). It queues instead.
    [Fact]
    public void CollectAfterCombat_DefersCorpseDropWhileHostiles()
    {
        using Harness h = new() { HasHostiles = true };
        h.Settings.SilverPolicy = CashPolicy.Collect;
        h.Settings.CollectAfterCombatFinished = true;

        h.Feed("7 silver drop to the ground.");

        Assert.Empty(h.Sent);                       // deferred, not collected
    }

    // Once the room clears (no engageable hostiles) the pending collect fires on the
    // room-entity observation — the "combat finished" signal. It re-displays the
    // room with a `look` rather than replaying the mid-fight amount; the re-display's
    // "You notice" drives the actual collect off the true ground total.
    [Fact]
    public void CollectAfterCombat_ReLooksOnRoomClear_ThenCollectsFromDisplay()
    {
        using Harness h = new() { HasHostiles = true };
        h.Settings.SilverPolicy = CashPolicy.Collect;
        h.Settings.CollectAfterCombatFinished = true;

        h.Feed("7 silver drop to the ground.");
        Assert.Empty(h.Sent);

        h.HasHostiles = false;                      // last hostile fell
        h.Cash.OnRoomObserved();
        Assert.Equal("", h.LastSent);               // bare CR re-display (honours brief mode), not a replay

        h.Feed("You notice 7 silver nobles here.");
        Assert.Contains("get 7 silver noble", h.AllSent);
    }

    // Room-entity observations fire mid-fight too (a new mob wanders in); the
    // queue must stay deferred while hostiles remain.
    [Fact]
    public void CollectAfterCombat_ObservationWhileHostiles_KeepsDeferred()
    {
        using Harness h = new() { HasHostiles = true };
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.Settings.CollectAfterCombatFinished = true;

        h.Feed("25 gold drop to the ground.");
        h.Cash.OnRoomObserved();                    // still fighting

        Assert.Empty(h.Sent);
    }

    // The reported spam: several coin drops across a fight, each re-observed on every
    // room re-render, used to flush as a flood of stacked gets (and Drop-smaller
    // cascades). Now the flush re-displays the room and collects the running ground
    // TOTAL once — not one get per drop.
    [Fact]
    public void CollectAfterCombat_ReLooksAndCollectsGroundTotalOnce()
    {
        using Harness h = new() { HasHostiles = true };
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.Settings.CollectAfterCombatFinished = true;

        h.Feed("10 gold drop to the ground.");
        h.Feed("5 gold drop to the ground.");
        Assert.Empty(h.Sent);

        h.HasHostiles = false;
        h.Cash.OnRoomObserved();
        Assert.Equal("", h.LastSent);               // one CR re-display, drops not replayed

        // The authoritative ground total drives ONE collect — not get 10 + get 5.
        h.Feed("You notice 15 gold crowns here.");
        Assert.Equal(new[] { "", "get 15 gold crown" }, h.AllSent);
    }

    // Room-display cash defers too (not just corpse drops) — the ground-survey
    // collect site routes through the same defer.
    [Fact]
    public void CollectAfterCombat_DefersRoomDisplayCash()
    {
        using Harness h = new() { HasHostiles = true };
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.Settings.CollectAfterCombatFinished = true;

        h.Feed("There are 50 gold pieces here.");
        Assert.Empty(h.Sent);

        h.HasHostiles = false;
        h.Cash.OnRoomObserved();
        Assert.Equal("", h.LastSent);               // CR re-display first

        h.Feed("You notice 50 gold crowns here.");
        Assert.Contains("get 50 gold crown", h.AllSent);
    }

    // Leaving the room before it cleared abandons the deferred coin — it belonged
    // to the room we walked out of, so a later clear-signal collects nothing.
    [Fact]
    public void CollectAfterCombat_RoomChange_DiscardsDeferred()
    {
        using Harness h = new() { HasHostiles = true };
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.Settings.CollectAfterCombatFinished = true;

        h.Feed("25 gold drop to the ground.");
        h.Cash.OnRoomChanged();                     // walked away

        h.HasHostiles = false;
        h.Cash.OnRoomObserved();
        Assert.Empty(h.Sent);
    }

    // Toggle OFF is the default behaviour: collect immediately even mid-fight.
    [Fact]
    public void CollectAfterCombat_Off_CollectsImmediatelyMidFight()
    {
        using Harness h = new() { HasHostiles = true };
        h.Settings.SilverPolicy = CashPolicy.Collect;
        h.Settings.CollectAfterCombatFinished = false;

        h.Feed("7 silver drop to the ground.");

        Assert.Equal("get 7 silver noble", h.LastSent);
    }

    // Toggle on but no hostiles (cleared room, or non-combat pickup): collect
    // immediately — nothing to wait for.
    [Fact]
    public void CollectAfterCombat_OnButNoHostiles_CollectsImmediately()
    {
        using Harness h = new() { HasHostiles = false };
        h.Settings.SilverPolicy = CashPolicy.Collect;
        h.Settings.CollectAfterCombatFinished = true;

        h.Feed("7 silver drop to the ground.");

        Assert.Equal("get 7 silver noble", h.LastSent);
    }

    // ----- post-combat re-look is third-party-safe -------------------

    // Someone else grabbed the pile before our post-combat re-look. Because we
    // collect from the re-display (not the amounts seen mid-fight), a vanished pile
    // just yields no cash line → no get — never the doomed "get 7" the old
    // stored-amount flush would have fired against a pile that's no longer there.
    [Fact]
    public void CollectAfterCombat_ReLookShowsNoCash_CollectsNothing()
    {
        using Harness h = new() { HasHostiles = true };
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.Settings.CollectAfterCombatFinished = true;

        h.Feed("7 gold drop to the ground.");
        h.HasHostiles = false;
        h.Cash.OnRoomObserved();
        Assert.Equal("", h.LastSent);                     // CR re-display

        h.Feed("You notice a rusty dagger here.");        // pile gone — no cash on the ground
        Assert.Equal(new[] { "" }, h.AllSent);            // just the CR re-display, no stale get
    }

    // ----- decide-once-per-pile latch --------------------------------

    // The 143020 spam: at the gate with drop-smaller-for-larger, the room re-renders
    // the same "You notice" several times post-combat. Each used to re-run the swap
    // (drop copper / get silver) on a pile already taken. Now it's collected once.
    [Fact]
    public void CollectAfterCombat_SamePileReRendered_SwapsOnce()
    {
        using Harness h = new();   // no hostiles → immediate collect path (posts run inline)
        h.Settings.SilverPolicy = CashPolicy.Collect;
        h.Settings.CollectAfterCombatFinished = true;
        h.Settings.SkipCollectIfMakesLight = true;      // at the gate → swap, not free pickup
        h.Settings.DropSmallerForLarger = true;
        h.Snapshot = Snap(500, 0, 0, 0, 0, currentWeight: 200, maxWeight: 1000);   // hold copper to swap, over Light cap

        h.Feed("You notice 4 silver nobles here.");     // 1st render
        h.Feed("You notice 4 silver nobles here.");     // re-render
        h.Feed("You notice 4 silver nobles here.");     // re-render

        Assert.Equal(1, h.AllSent.Count(s => s == "get 4 silver noble"));
        Assert.Equal(1, h.AllSent.Count(s => s == "drop 4 copper farthing"));
    }

    // A fresh corpse-drop of a currency re-arms collection even after an earlier pile
    // of it was taken this visit (a new mob wandered in and dropped more).
    [Fact]
    public void CollectAfterCombat_NewCorpseDrop_ReCollectsAfterEarlierPile()
    {
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.Settings.CollectAfterCombatFinished = true;

        h.Feed("You notice 5 gold sovereigns here.");
        Assert.Contains("get 5 gold crown", h.AllSent);
        h.Sent.Clear();

        h.Feed("You notice 5 gold sovereigns here.");   // same pile re-rendered → skipped
        Assert.Empty(h.Sent);

        h.Feed("3 gold drop to the ground.");           // new coin → re-arm → collect
        Assert.Contains("get 3 gold crown", h.AllSent);
    }

    // Room change clears the latch so the next room's identical-looking pile collects.
    [Fact]
    public void CollectAfterCombat_RoomChange_ResetsCollectLatch()
    {
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.Settings.CollectAfterCombatFinished = true;

        h.Feed("You notice 5 gold sovereigns here.");
        Assert.Contains("get 5 gold crown", h.AllSent);
        h.Sent.Clear();

        h.Feed("You notice 5 gold sovereigns here.");   // re-render → skip
        Assert.Empty(h.Sent);

        h.Cash.OnRoomChanged();                          // walked into a new room

        h.Feed("You notice 5 gold sovereigns here.");   // fresh room → collect again
        Assert.Contains("get 5 gold crown", h.AllSent);
    }

    // ----- encumbrance gate + cascade --------------------------------

    // Build a snapshot with given per-coin counts + a numeric
    // encumbrance reading; TotalCopperValue is irrelevant to the gate so
    // it's left zero.
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

    // Build a holdings snapshot for the auto-deposit gates:
    // wealthValue is the game's Wealth: figure
    // (TotalCopperValue); the coin counts drive TotalCoinCount. Encumbrance
    // is irrelevant to the gates so it's left empty.
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

        Assert.Equal("get 1000 gold crown", h.LastSent);
    }

    [Fact]
    public void Collect_AtMaxWeight_NoFlag_SkipsDoomedGet()
    {
        // 100% weight with NO SkipCollectIfMakes flag: the hard MaxWeight cap is
        // always on now, so a `get` we can't hold is never sent (previously the
        // no-flag path was ungated and fired doomed gets — report 20260730-124104).
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.Snapshot = Snap(0, 0, 0, 0, 0, currentWeight: 1000, maxWeight: 1000);

        h.Feed("There are 100 gold pieces here.");

        // No `get`; one `i` checks the weight estimate behind the refusal.
        Assert.Equal(new[] { "i" }, h.AllSent);
    }

    // The weight behind a refusal is a running estimate. One `i` per run of refusals
    // checks it, and a pickup that goes through starts a new run.
    [Fact]
    public void Collect_RefusedForWeight_ChecksTheEstimateOncePerRun()
    {
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.Snapshot = Snap(0, 0, 0, 0, 0, currentWeight: 1000, maxWeight: 1000);

        h.Feed("There are 100 gold pieces here.");
        h.Feed("There are 100 gold pieces here.");
        h.Feed("There are 100 gold pieces here.");
        Assert.Equal(new[] { "i" }, h.AllSent);

        // The `i` showed the real weight: the next pile is picked up.
        h.Settings.SilverPolicy = CashPolicy.Collect;
        h.Snapshot = Snap(0, 0, 0, 0, 0, currentWeight: 300, maxWeight: 1000);
        h.Feed("There are 100 silver pieces here.");
        Assert.Equal("get 100 silver noble", h.LastSent);
    }

    // A train's fee leaves the purse by value only, so the next coin decision
    // re-reads the inventory to settle which coins went.
    [Theory]
    [InlineData("You hand over 69200 copper farthings to train to the next level!")]
    [InlineData("You hand over 1 gold crown and you receive training to attain level 3.")]
    public void Trained_NextCoinDecisionReReadsInventory(string trainLine)
    {
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.Snapshot = Snap(0, 0, 0, 0, 0, currentWeight: 300, maxWeight: 1000);

        h.Feed(trainLine);
        Assert.Empty(h.Sent);                 // not while the trainer's screen may be up

        h.Feed("There are 100 gold pieces here.");
        Assert.Equal(new[] { "i", "get 100 gold crown" }, h.AllSent);
    }

    // A refused stash means the coin counts are stale, same as a refused drop.
    [Fact]
    public void RefusedStash_ReReadsInventory()
    {
        using Harness h = new();
        h.Feed("You don't have 6975 silver noble to hide!");
        Assert.Equal(new[] { "i" }, h.AllSent);
    }

    [Fact]
    public void Collect_AtMaxWeight_OverDepositThreshold_ArmsDeposit()
    {
        // Full AND wealth over the deposit threshold: suppressing the doomed get
        // must re-check the auto-deposit so the bank reroute arms — otherwise a
        // full character loops forever, never depositing (report 20260730-125716,
        // coupled to the at-max-weight get suppression above).
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.Settings.AutoDepositIfWealthExceeds = 100;
        h.Settings.BankRoomKey = "bank";
        h.Snapshot = new InventorySnapshot(
            new CurrencyHoldings(0, 0, 0, 0, 0, 150),   // TotalCopperValue 150 > 100
            new EncumbranceReading(1000, 1000, 0, EncumbranceLevel.None),
            Array.Empty<EquippedItem>(),
            Array.Empty<string>(),
            DateTimeOffset.UtcNow);

        h.Feed("There are 100 gold pieces here.");

        Assert.Equal(new[] { "i" }, h.AllSent);   // doomed get suppressed; the estimate is checked once
        Assert.Single(h.AutoDeposits);        // ...and the deposit re-check armed
        Assert.Equal(150, h.AutoDeposits[0]);
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

        Assert.Equal("get 507 copper farthing", h.LastSent);
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

        Assert.Equal(new[] { "i" }, h.AllSent);   // no `get`; the estimate is checked once
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

        Assert.Equal(new[] { "drop 93 copper farthing", "get 300 gold crown" }, h.AllSent);
    }

    // "Drop coin to make room for Auto-sell items": three coins to the weight unit,
    // cheapest first, none dearer than the chosen coin.
    [Fact]
    public void DropCoinForWeight_DropsCheapestFirst_UpToTheChosenCoin()
    {
        using Harness h = new();
        h.Settings.DropCoinForSellItems = true;
        h.Settings.DropCoinForSellItemsUpTo = CoinDenomination.Silver;
        h.Snapshot = Snap(30, 500, 9, 0, 0, currentWeight: 600, maxWeight: 1000);

        Assert.True(h.Cash.TryDropCoinForWeight(40, "dagger"));

        Assert.Equal(new[] { "drop 30 copper farthing", "drop 90 silver noble" }, h.AllSent);
    }

    [Fact]
    public void DropCoinForWeight_NotEnoughUpToTheChosenCoin_DropsNothing()
    {
        using Harness h = new();
        h.Settings.DropCoinForSellItems = true;
        h.Settings.DropCoinForSellItemsUpTo = CoinDenomination.Silver;
        h.Snapshot = Snap(30, 60, 5000, 0, 0, currentWeight: 1700, maxWeight: 2400);

        Assert.False(h.Cash.TryDropCoinForWeight(40, "dagger"));   // 120 needed, 90 droppable

        Assert.Empty(h.AllSent);
    }

    [Fact]
    public void DropCoinForWeight_Off_DropsNothing()
    {
        using Harness h = new();
        h.Snapshot = Snap(0, 500, 0, 0, 0, currentWeight: 600, maxWeight: 1000);

        Assert.False(h.Cash.TryDropCoinForWeight(40, "dagger"));

        Assert.Empty(h.AllSent);
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
        Assert.Equal("get 507 copper farthing", h.LastSent);

        h.Feed("There are 1000 copper pieces here.");
        Assert.Single(h.Sent);   // second batch gated out by the in-flight projection
    }

    // Report paradigm-20261007-111929: a boss died, the coin get went out on the
    // drop line, and the game echoed it without running it (the boss's death spell
    // leaves the room unable to act until it runs out). The room is drawn again
    // once it has; the coins are still listed and are asked for once more.
    [Fact]
    public void GetThrownAwayByADeathStall_IsAskedAgain_WhenTheCoinsAreStillThere()
    {
        using Harness h = new();
        DateTime now = new(2026, 10, 7, 11, 19, 23, DateTimeKind.Utc);
        h.UnansweredNow = () => now;

        h.Feed("5 platinum drop to the ground.");
        int afterFirst = h.AllSent.Count(c => c.StartsWith("get "));
        Assert.Equal(1, afterFirst);
        h.Cash.NoteDeathStall();

        now = now.AddSeconds(2);
        h.Feed("You notice 5 platinum pieces here.");

        Assert.Equal(2, h.AllSent.Count(c => c.StartsWith("get ")));

        // Once more only: a pile that still won't come is left.
        now = now.AddSeconds(2);
        h.Feed("You notice 5 platinum pieces here.");
        Assert.Equal(2, h.AllSent.Count(c => c.StartsWith("get ")));
    }

    // With no stall, an unanswered get was refused for a reason of its own; the
    // re-drawn pile stays handled.
    [Fact]
    public void UnansweredGet_WithNoDeathStall_IsNotAskedAgain()
    {
        using Harness h = new();
        DateTime now = new(2026, 10, 7, 11, 19, 23, DateTimeKind.Utc);
        h.UnansweredNow = () => now;

        h.Feed("5 platinum drop to the ground.");
        now = now.AddSeconds(2);
        h.Feed("You notice 5 platinum pieces here.");

        Assert.Equal(1, h.AllSent.Count(c => c.StartsWith("get ")));
    }
}
