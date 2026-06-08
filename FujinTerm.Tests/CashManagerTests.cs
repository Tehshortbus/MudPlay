using System.Text;
using FujinTerm.Game.Cash;
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
        public List<(string Currency, int Count, CashPolicy Policy)> Dispatches { get; } = new();
        public List<long> AutoDeposits { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Cash = new CashManager(Router,
                readSettings: () => Settings,
                isEnabled: () => AutoGetCashEnabled,
                log: Log);
            Cash.SetWireSender(b => Sent.Add(b));
            Cash.CashDispatched += (c, n, p) => Dispatches.Add((c, n, p));
            Cash.AutoDepositRequested += t => AutoDeposits.Add(t);
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

        public void Dispose() => Cash.Dispose();
    }

    // ----- per-currency policy dispatch -------------------------------

    [Fact]
    public void OnGround_Plural_CollectsViaGetAll()
    {
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;

        h.Feed("There are 50 gold pieces here.");

        Assert.Single(h.Sent);
        Assert.Equal("get gold", h.LastSent);
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
        Assert.Equal("get gold", h.LastSent);
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
    public void HeldGoldEquivalent_ComputesMultiplier()
    {
        using Harness h = new();
        h.Feed("You picked up 5 platinum pieces.");    // 5 * 100 = 500g
        h.Feed("You picked up 100 gold pieces.");      // 100 * 1 = 100g

        Assert.Equal(600, h.Cash.HeldGoldEquivalent);
    }

    [Fact]
    public void AutoDeposit_FiresWhenWealthExceedsThreshold()
    {
        using Harness h = new();
        h.Settings.AutoDepositIfWealthExceeds = 100;

        h.Feed("You picked up 150 gold pieces.");

        Assert.Single(h.AutoDeposits);
        Assert.Equal(150, h.AutoDeposits[0]);
    }

    [Fact]
    public void AutoDeposit_SingleShotPerCrossing()
    {
        using Harness h = new();
        h.Settings.AutoDepositIfWealthExceeds = 100;

        h.Feed("You picked up 150 gold pieces.");
        h.Feed("You picked up 50 gold pieces.");

        Assert.Single(h.AutoDeposits);
    }

    [Fact]
    public void AutoDeposit_ReArmsAfterDropBelowThreshold()
    {
        using Harness h = new();
        h.Settings.AutoDepositIfWealthExceeds = 100;

        h.Feed("You picked up 150 gold pieces.");
        Assert.Single(h.AutoDeposits);

        h.Feed("You dropped 120 gold pieces.");       // back to 30 — below threshold
        h.Feed("You picked up 100 gold pieces.");     // back to 130 — re-fires

        Assert.Equal(2, h.AutoDeposits.Count);
    }

    [Fact]
    public void AutoDeposit_ZeroThreshold_NoFire()
    {
        // 0 means disabled.
        using Harness h = new();
        h.Settings.AutoDepositIfWealthExceeds = 0;

        h.Feed("You picked up 1000 gold pieces.");

        Assert.Empty(h.AutoDeposits);
    }

    // ----- reset tallies ----------------------------------------------

    [Fact]
    public void ResetTallies_ClearsHeldCoin()
    {
        using Harness h = new();
        h.Feed("You picked up 50 gold pieces.");
        h.Cash.ResetTallies();

        Assert.Equal(0, h.Cash.HeldCoin("gold"));
        Assert.Equal(0, h.Cash.HeldGoldEquivalent);
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

    [Fact]
    public void Hidden_TriggersAutoDepositReArm()
    {
        // Wealth crossed threshold, fires deposit. Then we hide
        // enough coin to drop back under — next pickup that crosses
        // again should re-fire.
        using Harness h = new();
        h.Settings.AutoDepositIfWealthExceeds = 100;

        h.Feed("You picked up 150 gold pieces.");
        Assert.Single(h.AutoDeposits);

        h.Feed("You hid 100 gold pieces.");         // down to 50 — re-arms
        h.Feed("You picked up 100 gold pieces.");   // back to 150 — re-fires

        Assert.Equal(2, h.AutoDeposits.Count);
    }

    // ----- Discard auto-drop -----------------------------------------

    [Fact]
    public void Discard_PickedUpFlaggedCurrency_LogsButDoesNotSend()
    {
        // MajorMUD's specific-amount currency-drop syntax hasn't been
        // confirmed by the user, so the engine logs the discard
        // intent + doesn't send a (potentially-wrong) command.
        // Settings persist so the wire emit can land once the syntax
        // is verified.
        using Harness h = new();
        h.Settings.CopperPolicy = CashPolicy.Discard;

        h.Feed("You picked up 50 copper pieces.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Discard_OnSettingsChange_LogsButDoesNotSend()
    {
        using Harness h = new();
        h.Settings.GoldPolicy = CashPolicy.Collect;
        h.Feed("You picked up 100 gold pieces.");
        Assert.Equal(100, h.Cash.HeldCoin("gold"));
        h.Sent.Clear();

        h.Settings.GoldPolicy = CashPolicy.Discard;
        h.Cash.OnSettingsChanged();

        Assert.Empty(h.Sent);
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
        // User edits threshold while holding coin above the new value.
        // Without OnSettingsChanged() the trigger waits for the next
        // pickup line — could be a long time.
        using Harness h = new();
        h.Feed("You picked up 200 gold pieces.");
        Assert.Empty(h.AutoDeposits);                // no threshold yet

        h.Settings.AutoDepositIfWealthExceeds = 100;
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
        // Silver is Collect → `get silver`. Copper is Ignore.
        List<string> lines = h.Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();
        Assert.Contains("get silver", lines);
        Assert.DoesNotContain("get copper", lines);
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
