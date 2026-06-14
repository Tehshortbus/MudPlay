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
        // Per-denomination holdings the stash plan reads. Seed before
        // ExecuteStash to model what an `i` parse would have produced.
        public InventorySnapshot Snapshot { get; set; } = InventorySnapshot.Empty;
        public List<(RoomKey Room, IReadOnlyList<(string Currency, long Amount)> Dispatch)> Executed { get; } = new();

        public Harness()
        {
            Profile.LoadBlank();
            Stash = new StashRoomManager(Profile,
                readCash: () => CashSettings,
                getSnapshot: () => Snapshot,
                isEnabled: () => AutoGetCashEnabled,
                log: Log);
            Stash.SetWireSender(b => Sent.Add(b));
            Stash.StashExecuted += (r, d) => Executed.Add((r, d));
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
    /// counts; wealth value is irrelevant to the stash plan so it's 0.</summary>
    private static InventorySnapshot Coins(
        int copper = 0, int silver = 0, int gold = 0, int platinum = 0, int runic = 0)
    {
        return new InventorySnapshot(
            new CurrencyHoldings(copper, silver, gold, platinum, runic, 0),
            EncumbranceReading.Empty,
            Array.Empty<EquippedItem>(),
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
        Assert.Equal(2, h.Executed[0].Dispatch.Count);
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
}
