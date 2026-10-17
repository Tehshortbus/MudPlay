using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MudPlay.Game.Inventory;
using MudPlay.Models.Settings;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins ItemChargeTracker's Paradigm charge model: a `look <item>` reply's "Uses
// remaining: N" is recorded against the carried item by number and PERSISTED on the
// profile; RemainingFor is recharge-adjusted (rechargeable restocks to max past
// cleanup, finite persists); an unknown charged item is auto-looked; and a `use`
// reconciles by re-looking (so a blocked use never mis-decrements).
public sealed class ItemChargeTrackerTests : IDisposable
{
    private const string ItemsJson = """
        [
          { "Number": 10, "Name": "gnarled wand",        "UseCount": 10, "Retain After Uses": 0 },
          { "Number": 20, "Name": "peasant cloak",       "UseCount": 5,  "Retain After Uses": 1 },
          { "Number": 30, "Name": "nexus spear",         "UseCount": -1, "Retain After Uses": 0 },
          { "Number": 40, "Name": "token of Silvermere", "UseCount": 0,  "Retain After Uses": 1 },
          { "Number": 41, "Name": "token of Arlysia",    "UseCount": 5,  "Retain After Uses": 1 },
          { "Number": 50, "Name": "scroll of blur",      "UseCount": 1,  "Retain After Uses": 0 },
          { "Number": 51, "Name": "blessed charm",       "UseCount": 1,  "Retain After Uses": 1 }
        ]
        """;

    private readonly string _root;
    private readonly GameDataCache _cache;
    private readonly ProfileService _profile;
    private readonly List<string> _carried = new();
    private readonly List<string> _sent = new();
    private readonly List<Action> _scheduled = new();
    private DateTimeOffset _clock = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);   // before the 21:00 cleanup
    private bool _paradigm = true;
    // Item number → the line a successful use prints. An item absent here has no use
    // line on record, so its uses are settled by a look.
    private readonly Dictionary<int, string> _useLines = new();
    private readonly ItemChargeTracker _tracker;

    public ItemChargeTrackerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-itemcharge-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), ItemsJson);
        _cache = new GameDataCache(_root);
        _cache.SwitchSet("alpha");
        _profile = TestProfiles.Service();
        _profile.LoadDefaultProfile();

        _tracker = new ItemChargeTracker(
            gameData: _cache,
            profile: _profile,
            heldItems: () => _carried,
            itemNumberOf: Number,
            onParadigm: () => _paradigm,
            cleanupConfig: () => new BossCleanupConfig(TimeSpan.FromHours(21), TimeZoneInfo.Utc),
            useConfirmLine: n => _useLines.TryGetValue(n, out string? text)
                ? line => line.Contains(text, StringComparison.Ordinal)
                : null,
            // Mirror production: SendGameCommand re-enters the outbound tap, so a look
            // sent by the tracker arms its own capture.
            sendLook: cmd => { _sent.Add(cmd); _tracker!.ObserveOutbound(Bytes(cmd)); },
            schedule: (_, a) => _scheduled.Add(a),
            now: () => _clock,
            log: null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private static int Number(string name) => name switch
    {
        "gnarled wand" => 10,
        "peasant cloak" => 20,
        "nexus spear" => 30,
        "token of Silvermere" => 40,
        "token of Arlysia" => 41,
        "scroll of blur" => 50,
        "blessed charm" => 51,
        _ => 0,
    };

    private static byte[] Bytes(string cmd) => Encoding.Latin1.GetBytes(cmd + "\r");
    private void Look(string cmd) => _tracker.ObserveOutbound(Bytes(cmd));
    private void Use(string cmd) => _tracker.ObserveOutbound(Bytes(cmd));
    private void Line(string text) => _tracker.HandleLine(text);
    // Fire the timers due "now" — one tick. Actions those fire in turn (e.g. a re-look's
    // reply window) land in the queue for a later FireTimers, mirroring real timer
    // ordering so a same-tick window-clear can't wipe a capture the reply just armed.
    private void FireTimers()
    {
        List<Action> due = new(_scheduled);
        _scheduled.Clear();
        foreach (Action a in due) a();
    }

    [Fact]
    public void Look_ThenUsesRemaining_RecordsAndPersists()
    {
        _carried.Add("gnarled wand");
        Look("look gnarled");                     // partial resolves to the wand
        Line("A crooked stick.");
        Line("Uses remaining: 7");
        Assert.Equal(7, _tracker.RemainingFor(10));
        Assert.Equal(7, _profile.Current!.ItemCharges![10].Remaining);   // persisted by number
    }

    [Fact]
    public void RechargeItem_RestocksToMax_AfterCleanup()
    {
        _carried.Add("peasant cloak");
        Look("look peasant");
        Line("Uses remaining: 2");
        Assert.Equal(2, _tracker.RemainingFor(20));

        _clock = new DateTimeOffset(2026, 1, 1, 22, 0, 0, TimeSpan.Zero);   // past the 21:00 cleanup
        Assert.Equal(5, _tracker.RemainingFor(20));                        // assumed restocked to max
    }

    [Fact]
    public void FiniteItem_CountPersists_AcrossCleanup()
    {
        _carried.Add("gnarled wand");
        Look("look gnarled");
        Line("Uses remaining: 4");
        _clock = new DateTimeOffset(2026, 1, 2, 22, 0, 0, TimeSpan.Zero);   // a day later, past cleanup
        Assert.Equal(4, _tracker.RemainingFor(10));                        // no recharge — stays spent
    }

    [Fact]
    public void UnknownChargedItem_IsAutoLooked()
    {
        _carried.Add("gnarled wand");
        _tracker.EnsureChargesKnown();
        Assert.Contains("look gnarled wand", _sent);
        Line("Uses remaining: 9");                // the auto-look armed capture; its reply lands
        Assert.Equal(9, _tracker.RemainingFor(10));

        _sent.Clear();
        FireTimers();                             // advance the queue
        _tracker.EnsureChargesKnown();            // now known — no second look
        Assert.DoesNotContain("look gnarled wand", _sent);
    }

    // Report paradigm-20260926-220239: buying learn-spell scrolls looked each one.
    [Fact]
    public void SingleUseConsumable_IsNotLooked_AndReadsOneCharge()
    {
        _carried.Add("scroll of blur");
        _tracker.EnsureChargesKnown();
        Use("use scroll of blur");
        FireTimers();

        Assert.Empty(_sent);
        Assert.Equal(1, _tracker.RemainingFor(50));
    }

    [Fact]
    public void SingleUseRechargeable_IsStillLooked()
    {
        _carried.Add("blessed charm");
        _tracker.EnsureChargesKnown();
        Assert.Contains("look blessed charm", _sent);
    }

    [Fact]
    public void InfiniteItem_IsNotAutoLooked()
    {
        _carried.Add("nexus spear");
        _tracker.EnsureChargesKnown();
        Assert.Empty(_sent);
    }

    [Fact]
    public void HeldToken_IsNotAutoLooked_TokenTrackerOwnsItsLoginLook()
    {
        // token of Arlysia is a CHARGED token (UseCount 5) — without the TokenCatalog
        // guard EnsureChargesKnown would queue it (IsLimitedUse is true), so this pins the
        // guard: only it keeps the token out of the auto-look. TokenTracker looks it instead.
        _carried.Add("token of Arlysia");
        _tracker.EnsureChargesKnown();
        Assert.Empty(_sent);

        Look("look token of Arlysia");            // TokenTracker's own look's reply still lands here
        Line("Uses remaining: 3");
        Assert.Equal(3, _tracker.RemainingFor(41));
    }

    [Fact]
    public void UsingAToken_DoesNotReLook_TokenTrackerOwnsIt()
    {
        _carried.Add("token of Arlysia");
        Look("look token of Arlysia"); Line("Uses remaining: 5");

        _sent.Clear();
        Use("use token of Arlysia");              // TokenTracker re-looks on use; this tracker must not
        FireTimers();
        Assert.Empty(_sent);
    }

    [Fact]
    public void Use_ReLooks_AndReconcilesToTheTrueCount()
    {
        _carried.Add("gnarled wand");
        Look("look gnarled");
        Line("Uses remaining: 5");

        _sent.Clear();
        Use("use gnarled wand");                  // schedules the reconciling re-look
        FireTimers();                             // fire the debounce → look sent (arms capture)
        Assert.Contains("look gnarled wand", _sent);
        Line("Uses remaining: 4");                // game says one fewer
        Assert.Equal(4, _tracker.RemainingFor(10));
    }

    [Fact]
    public void BlockedUse_DoesNotMisDecrement()
    {
        _carried.Add("gnarled wand");
        Look("look gnarled");
        Line("Uses remaining: 5");

        Use("use gnarled wand");                  // the use is blocked in-game
        FireTimers();
        Line("Uses remaining: 5");                // re-look shows the count unchanged
        Assert.Equal(5, _tracker.RemainingFor(10));
    }

    // Report paradigm-20261002-140153: a deck of cards drawn every round was looked at
    // after every draw. Once the count is known the use line counts it down.
    [Fact]
    public void KnownCount_UseLine_CountsDown_WithoutALook()
    {
        _carried.Add("gnarled wand");
        _useLines[10] = "You wave the gnarled wand";
        Look("look gnarled");
        Line("Uses remaining: 5");

        _sent.Clear();
        Use("use gnarled wand");
        Line("You wave the gnarled wand!");
        FireTimers();

        Assert.Equal(4, _tracker.RemainingFor(10));
        Assert.Empty(_sent);
    }

    [Fact]
    public void UseLine_CountsOnce_PerUse()
    {
        _carried.Add("gnarled wand");
        _useLines[10] = "You wave the gnarled wand";
        Look("look gnarled");
        Line("Uses remaining: 5");

        Use("use gnarled wand");
        Line("You wave the gnarled wand!");
        Line("You wave the gnarled wand!");     // someone else's, or a repaint

        Assert.Equal(4, _tracker.RemainingFor(10));
    }

    // A use takes the round's between-round cast slot; refused for that, it spends
    // no charge (user, 2026-10-02) and needs no look.
    [Fact]
    public void UseRefused_AlreadyCastThisRound_SpendsNothing_AndSendsNoLook()
    {
        _carried.Add("gnarled wand");
        _useLines[10] = "You wave the gnarled wand";
        Look("look gnarled");
        Line("Uses remaining: 5");

        _sent.Clear();
        Use("use gnarled wand");
        Line("You have already cast a spell this round!");
        FireTimers();

        Assert.Equal(5, _tracker.RemainingFor(10));
        Assert.Empty(_sent);
    }

    // Neither the use line nor a refusal we know: the count is not guessed at.
    [Fact]
    public void UseWithNoAnswerWeKnow_IsSettledByALook()
    {
        _carried.Add("gnarled wand");
        _useLines[10] = "You wave the gnarled wand";
        Look("look gnarled");
        Line("Uses remaining: 5");

        _sent.Clear();
        Use("use gnarled wand");
        Line("Nothing happens.");
        FireTimers();                             // the confirm window lapses → re-look scheduled
        FireTimers();                             // the re-look fires
        Assert.Contains("look gnarled wand", _sent);
        Line("Uses remaining: 5");
        Assert.Equal(5, _tracker.RemainingFor(10));
    }

    // The last charge is read from the game, which also notices the item gone.
    [Fact]
    public void LastCharge_IsLooked_NotCounted()
    {
        _carried.Add("gnarled wand");
        _useLines[10] = "You wave the gnarled wand";
        Look("look gnarled");
        Line("Uses remaining: 1");

        _sent.Clear();
        Use("use gnarled wand");
        Line("You wave the gnarled wand!");
        FireTimers();

        Assert.Contains("look gnarled wand", _sent);
    }

    [Fact]
    public void StackedEntry_ResolvesUnderLeadingCount_AndTracksTopCopy()
    {
        _carried.Add("2 gnarled wand");           // a stack — name stays singular under the count
        Look("look gnarled");                     // resolves the stack, strips the count
        Line("Uses remaining: 3");
        Assert.Equal(3, _tracker.RemainingFor(10));
        Assert.Equal(3, _tracker.RemainingForName("2 gnarled wand"));   // display resolves under the count
    }

    [Fact]
    public void DroppingTopCopy_ReLooks_ToNextCopysCount()
    {
        _carried.Add("2 gnarled wand");
        Look("look gnarled"); Line("Uses remaining: 5");   // the top copy

        _sent.Clear();
        _tracker.ObserveOutbound(Bytes("drop gnarled wand"));   // top copy dropped → next surfaces
        FireTimers();                              // debounce → re-look sends the singular name
        Assert.Contains("look gnarled wand", _sent);
        Line("Uses remaining: 8");                 // the next copy's charges
        Assert.Equal(8, _tracker.RemainingFor(10));
    }

    // Report paradigm-20260928-053429: dropping the only torch re-looked it, reading
    // the one on the floor. With no copy left, the count is forgotten instead, and
    // the next one picked up is looked afresh.
    [Fact]
    public void DroppingLastCopy_ForgetsItsCharges_InsteadOfLooking()
    {
        _carried.Add("gnarled wand");
        Look("look gnarled"); Line("Uses remaining: 5");
        _tracker.EnsureChargesKnown();

        _sent.Clear();
        _tracker.ObserveOutbound(Bytes("drop gnarled"));
        _carried.Clear();                          // "You dropped gnarled wand."
        FireTimers();

        Assert.Empty(_sent);
        Assert.Null(_tracker.RemainingFor(10));

        _carried.Add("gnarled wand");              // picked up again
        _tracker.EnsureChargesKnown();
        Assert.Contains("look gnarled wand", _sent);
    }

    [Fact]
    public void TokenLook_RecordsEvenWhenGameDataUseCountIsZero()
    {
        _carried.Add("token of Silvermere");
        Look("look token of Silvermere");         // the shape TokenTracker sends on login
        Line("Uses remaining: 3");
        Assert.Equal(3, _tracker.RemainingFor(40));
    }

    [Fact]
    public void OffParadigm_IsInert()
    {
        _paradigm = false;
        _carried.Add("gnarled wand");
        _tracker.EnsureChargesKnown();
        Look("look gnarled");
        Line("Uses remaining: 5");
        Assert.Empty(_sent);
        Assert.Null(_tracker.RemainingFor(10));
    }

    [Fact]
    public void ResetSession_KeepsPersistedCounts()
    {
        _carried.Add("gnarled wand");
        Look("look gnarled");
        Line("Uses remaining: 6");
        _tracker.ResetSession();
        Assert.Equal(6, _tracker.RemainingFor(10));   // store lives on the profile
    }
}
