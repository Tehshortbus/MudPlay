using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MudPlay.Game.Inventory;
using MudPlay.Models.Settings;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins ItemUseCountTracker's stock charge-counting: a `use` is counted only when the
// item's use-spell caster message CONFIRMS it (a bonked / blocked use prints no such
// line and burns nothing); remaining = max − used; a RECHARGEABLE item restocks to max
// once the cleanup boundary passes; a FINITE item's count survives it; the counter is
// inert on Paradigm; an infinite item reports no charges; and an item with no resolvable
// use-spell message falls back to counting on send.
public sealed class ItemUseCountTrackerTests : IDisposable
{
    private const string ItemsJson = """
        [
          { "Number": 10, "Name": "gnarled wand",  "UseCount": 10, "Retain After Uses": 0 },
          { "Number": 20, "Name": "peasant cloak", "UseCount": 5,  "Retain After Uses": 1 },
          { "Number": 30, "Name": "nexus spear",   "UseCount": -1, "Retain After Uses": 0 },
          { "Number": 40, "Name": "plain rod",     "UseCount": 3,  "Retain After Uses": 0 }
        ]
        """;

    // The use-spell caster message for each item that casts on use; item 40 has none.
    private const string WandMsg  = "The gnarled wand crackles with power.";
    private const string CloakMsg = "You feel protected.";

    private readonly string _root;
    private readonly GameDataCache _cache;
    private readonly ProfileService _profile;
    private readonly List<string> _carried = new();
    private readonly List<Action> _scheduled = new();
    private DateTimeOffset _clock = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);   // before the 21:00 cleanup
    private bool _stock = true;
    private readonly ItemUseCountTracker _tracker;

    public ItemUseCountTrackerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-usecount-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), ItemsJson);
        _cache = new GameDataCache(_root);
        _cache.SwitchSet("alpha");
        _profile = TestProfiles.Service();
        _profile.LoadDefaultProfile();

        _tracker = new ItemUseCountTracker(
            gameData: _cache,
            heldItems: () => _carried,
            itemNumberOf: Number,
            onStock: () => _stock,
            cleanupConfig: () => new BossCleanupConfig(TimeSpan.FromHours(21), TimeZoneInfo.Utc),
            profile: _profile,
            useConfirmLine: n => n switch
            {
                10 => line => line.Contains(WandMsg, StringComparison.OrdinalIgnoreCase),
                20 => line => line.Contains(CloakMsg, StringComparison.OrdinalIgnoreCase),
                _ => (Func<string, bool>?)null,   // item 40 has no resolvable use-spell message
            },
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
        "plain rod" => 40,
        _ => 0,
    };

    private void Use(string item) => _tracker.ObserveOutbound(Encoding.Latin1.GetBytes($"use {item}\r"));
    private void Line(string text) => _tracker.HandleLine(text);
    private void FireTimers() { foreach (Action a in new List<Action>(_scheduled)) a(); _scheduled.Clear(); }

    [Fact]
    public void ConfirmedUse_Decrements()
    {
        _carried.Add("gnarled wand");
        Assert.Equal(10, _tracker.RemainingFor(10));   // full before any use
        Use("gnarled wand"); Line(WandMsg);
        Use("gnarled wand"); Line(WandMsg);
        Use("gnarled wand"); Line(WandMsg);
        Assert.Equal(7, _tracker.RemainingFor(10));
    }

    [Fact]
    public void BonkedUse_DoesNotCount()
    {
        _carried.Add("gnarled wand");
        Use("gnarled wand");
        Line("You must wait to do that.");   // a bonk — not the item's cast message
        FireTimers();                          // window lapses, unconfirmed
        Assert.Equal(10, _tracker.RemainingFor(10));   // nothing burned
    }

    [Fact]
    public void UnconfirmedUse_TimesOut_WithoutCounting()
    {
        _carried.Add("gnarled wand");
        Use("gnarled wand");
        FireTimers();                          // no cast message ever arrived
        Line(WandMsg);                         // arrives after the window — too late
        Assert.Equal(10, _tracker.RemainingFor(10));
    }

    [Fact]
    public void RechargeItem_RestocksToMax_AfterCleanup()
    {
        _carried.Add("peasant cloak");
        Use("peasant cloak"); Line(CloakMsg);
        Use("peasant cloak"); Line(CloakMsg);
        Assert.Equal(3, _tracker.RemainingFor(20));     // 5 − 2

        _clock = new DateTimeOffset(2026, 1, 1, 22, 0, 0, TimeSpan.Zero);   // past the 21:00 cleanup
        Assert.Equal(5, _tracker.RemainingFor(20));     // restocked
    }

    [Fact]
    public void FiniteItem_CountPersists_AcrossCleanup()
    {
        _carried.Add("gnarled wand");
        Use("gnarled wand"); Line(WandMsg);
        Use("gnarled wand"); Line(WandMsg);
        Use("gnarled wand"); Line(WandMsg);
        Assert.Equal(7, _tracker.RemainingFor(10));

        _clock = new DateTimeOffset(2026, 1, 2, 22, 0, 0, TimeSpan.Zero);   // a day later, past cleanup
        Assert.Equal(7, _tracker.RemainingFor(10));     // no recharge — stays spent
    }

    [Fact]
    public void OnParadigm_DoesNotCount()
    {
        _carried.Add("gnarled wand");
        _stock = false;                          // Paradigm reads charges from the look reply
        Use("gnarled wand"); Line(WandMsg);
        Assert.Equal(10, _tracker.RemainingFor(10));    // uncounted
    }

    [Fact]
    public void InfiniteItem_ReportsNoCharges()
    {
        _carried.Add("nexus spear");
        Use("nexus spear");
        Assert.Null(_tracker.RemainingFor(30));
    }

    [Fact]
    public void NoResolvableMessage_FallsBackToCountOnSend()
    {
        _carried.Add("plain rod");
        Use("plain rod");                        // no matcher for #40 → counts on send
        Assert.Equal(2, _tracker.RemainingFor(40));
    }

    [Fact]
    public void StackedEntry_ResolvesUnderLeadingCount()
    {
        _carried.Add("2 gnarled wand");           // a stack — name singular under the count
        Use("gnarled wand"); Line(WandMsg);
        Assert.Equal(9, _tracker.RemainingFor(10));
    }

    [Fact]
    public void FiniteStack_AssumesNextCopyFull_WhenTopEmpties()
    {
        _carried.Add("2 gnarled wand");
        for (int i = 0; i < 10; i++) { Use("gnarled wand"); Line(WandMsg); }   // empty the top copy
        Assert.Equal(10, _tracker.RemainingFor(10));   // next copy assumed full (fresh drop = max)
    }

    [Fact]
    public void UseWithTarget_StillResolvesAndConfirms()
    {
        _carried.Add("gnarled wand");
        _tracker.ObserveOutbound(Encoding.Latin1.GetBytes("use gnarled wand orc\r"));   // item + target
        Line(WandMsg);
        Assert.Equal(9, _tracker.RemainingFor(10));
    }
}
