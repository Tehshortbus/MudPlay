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
        Assert.Equal("get all gold", h.LastSent);
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
        Assert.Equal("get all gold", h.LastSent);
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
}
