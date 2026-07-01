using System.Text;
using FujinTerm.Game.Cash;
using FujinTerm.Game.Inventory;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.E follow-up — <see cref="StashRoomManager"/> on-entry stash
/// dispatch driven by user-marked rooms from
/// <see cref="CharacterProfile.StashRooms"/> + per-currency
/// keep-on-hand rules on <see cref="CashSettings"/>. Held amounts come
/// from the authoritative <see cref="InventorySnapshot"/> (the
/// <c>i</c>-seeded, delta-tracked holdings) — not a local pickup tally,
/// which would undercount the starting balance.
/// </summary>
public sealed class StashRoomManagerTests
{
    private sealed class Harness : IDisposable
    {
        public LogService Log { get; } = new();
        public ProfileService Profile { get; } = new();
        public StashRoomManager Stash { get; }
        public List<byte[]> Sent { get; } = new();
        public CashSettings CashSettings { get; set; } = new();
        public bool AutoGetCashEnabled { get; set; } = true;
        // Per-denomination holdings + carried items the stash plan reads.
        // Seed before ExecuteStash to model what an `i` parse would have
        // produced.
        public InventorySnapshot Snapshot { get; set; } = InventorySnapshot.Empty;
        // Carried entries whose resolver returns the same name (i.e. the
        // item is flagged AutoStash). Anything not here resolves to null.
        public HashSet<string> AutoStashItems { get; } = new();
        public List<StashRoomManager.StashDispatch> Executed { get; } = new();

        public Harness()
        {
            Profile.LoadBlank();
            Stash = new StashRoomManager(Profile,
                readCash: () => CashSettings,
                getSnapshot: () => Snapshot,
                resolveAutoStashItem: entry => AutoStashItems.Contains(entry) ? entry : null,
                isEnabled: () => AutoGetCashEnabled,
                log: Log);
            Stash.SetWireSender(b => Sent.Add(b));
            Stash.StashExecuted += d => Executed.Add(d);
        }

        public void MarkRoomAsStash(int map, int room)
        {
            CharacterProfile p = Profile.Current!;
            p.StashRooms ??= new List<RoomRef>();
            p.StashRooms.Add(new RoomRef(map, room));
        }

        public IEnumerable<string> SentLines() =>
            Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'));

        public void Dispose() => Stash.Dispose();
    }

    /// <summary>Holdings snapshot with the given per-denomination coin
    /// counts and optional carried items; wealth value is irrelevant to
    /// the stash plan so it's 0.</summary>
    private static InventorySnapshot Coins(
        int copper = 0, int silver = 0, int gold = 0, int platinum = 0, int runic = 0,
        params string[] carried)
    {
        return new InventorySnapshot(
            new CurrencyHoldings(copper, silver, gold, platinum, runic, 0),
            EncumbranceReading.Empty,
            Array.Empty<EquippedItem>(),
            carried,
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Enter_MatchingRoom_DumpsAll_WhenNoKeep()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.Snapshot = Coins(gold: 500);

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Single(h.Sent);
        Assert.Equal("hide 500 gold", h.SentLines().First());
    }

    [Fact]
    public void Enter_MatchingRoom_KeepsConfiguredAmount()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.CashSettings.KeepGoldOnHand = 100;
        h.Snapshot = Coins(gold: 500);

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Single(h.Sent);
        Assert.Equal("hide 400 gold", h.SentLines().First());
    }

    [Fact]
    public void Enter_HeldAtOrBelowKeep_NoDispatch()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.CashSettings.KeepGoldOnHand = 100;
        h.Snapshot = Coins(gold: 80);

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Enter_NonMatchingRoom_NoDispatch()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.Snapshot = Coins(gold: 500);

        h.Stash.ExecuteStash(new RoomKey(2, 99));

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Enter_MultipleCurrencies_DispatchesEach()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.CashSettings.KeepGoldOnHand     = 100;
        h.CashSettings.KeepPlatinumOnHand = 10;
        h.Snapshot = Coins(gold: 300, platinum: 50);

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Equal(2, h.Sent.Count);
        List<string> lines = h.SentLines().ToList();
        Assert.Contains("hide 200 gold", lines);
        Assert.Contains("hide 40 platinum", lines);
        Assert.Equal(2, h.Executed[0].Currencies.Count);
    }

    [Fact]
    public void AutoGetCashOff_NoDispatch()
    {
        using Harness h = new() { AutoGetCashEnabled = false };
        h.MarkRoomAsStash(1, 42);
        h.Snapshot = Coins(gold: 100);

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void NoStashRoomsConfigured_NoDispatch()
    {
        using Harness h = new();
        h.Snapshot = Coins(gold: 100);

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void SecondVisit_AfterHoldingsDrop_NoReDispatch()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.CashSettings.KeepGoldOnHand = 100;
        h.Snapshot = Coins(gold: 500);

        h.Stash.ExecuteStash(new RoomKey(1, 42));
        Assert.Single(h.Sent);

        // After the server confirms the hide, the InventoryManager
        // snapshot drops to the kept floor — a re-entry finds nothing
        // above keep and stays quiet.
        h.Snapshot = Coins(gold: 100);
        h.Stash.ExecuteStash(new RoomKey(1, 42));
        Assert.Single(h.Sent);
    }

    // ===== item stashing (stash rooms hold cash AND items) =====

    [Fact]
    public void Enter_MatchingRoom_HidesFlaggedItem()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.Snapshot = Coins(carried: new[] { "a torch" });
        h.AutoStashItems.Add("a torch");

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Equal("hide a torch", Assert.Single(h.SentLines()));
        Assert.Equal(new[] { "a torch" }, h.Executed[0].Items);
    }

    [Fact]
    public void Enter_UnflaggedItem_Stays()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.Snapshot = Coins(carried: new[] { "a torch" });
        // Not flagged AutoStash — resolver returns null, nothing hidden.

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Enter_CashAndItems_BothDispatched()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.CashSettings.KeepGoldOnHand = 100;
        h.Snapshot = Coins(gold: 500, carried: new[] { "a torch", "a rusty dagger" });
        h.AutoStashItems.Add("a torch");
        h.AutoStashItems.Add("a rusty dagger");

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        List<string> lines = h.SentLines().ToList();
        Assert.Contains("hide 400 gold", lines);
        Assert.Contains("hide a torch", lines);
        Assert.Contains("hide a rusty dagger", lines);
        Assert.Single(h.Executed[0].Currencies);
        Assert.Equal(2, h.Executed[0].Items.Count);
    }

    [Fact]
    public void Enter_DuplicateFlaggedItem_HidesEach()
    {
        // MajorMUD lists each carried copy as its own token, so a stack of
        // three flagged torches yields three hides.
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.Snapshot = Coins(carried: new[] { "a torch", "a torch", "a torch" });
        h.AutoStashItems.Add("a torch");

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Equal(3, h.Sent.Count);
        Assert.Equal(3, h.Executed[0].Items.Count);
    }

    [Fact]
    public void ItemsOnly_NoExcessCash_StillFiresEvent()
    {
        // No coin above keep, but a flagged item present — the event
        // fires so transaction history records the item stash.
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.Snapshot = Coins(carried: new[] { "a torch" });
        h.AutoStashItems.Add("a torch");

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Single(h.Executed);
        Assert.Empty(h.Executed[0].Currencies);
        Assert.Equal(new[] { "a torch" }, h.Executed[0].Items);
    }

    [Fact]
    public void AutoGetCashOff_NoItemDispatch()
    {
        using Harness h = new() { AutoGetCashEnabled = false };
        h.MarkRoomAsStash(1, 42);
        h.Snapshot = Coins(carried: new[] { "a torch" });
        h.AutoStashItems.Add("a torch");

        h.Stash.ExecuteStash(new RoomKey(1, 42));

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Enter_NonMatchingRoom_NoItemDispatch()
    {
        using Harness h = new();
        h.MarkRoomAsStash(1, 42);
        h.Snapshot = Coins(carried: new[] { "a torch" });
        h.AutoStashItems.Add("a torch");

        h.Stash.ExecuteStash(new RoomKey(2, 99));

        Assert.Empty(h.Sent);
    }
}
