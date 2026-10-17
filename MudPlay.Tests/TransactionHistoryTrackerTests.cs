using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Cash;
using Xunit;

namespace MudPlay.Tests;

// TransactionHistoryTracker ledger. An injected clock
// stamps each entry deterministically; the tests pin the rendered detail
// strings (bank vs. stash, item grouping, coin denominations), the
// ignore-empty guards, the cap eviction, and the Changed signal.
public sealed class TransactionHistoryTrackerTests
{
    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
        public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);
    }

    private static (TransactionHistoryTracker, Clock) Make()
    {
        Clock c = new();
        return (new TransactionHistoryTracker(() => c.Now), c);
    }

    [Fact]
    public void Fresh_Empty()
    {
        (TransactionHistoryTracker t, _) = Make();
        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void NoteBankDeposit_RecordsWealthDetail()
    {
        (TransactionHistoryTracker t, _) = Make();
        t.NoteBankDeposit(12_300);

        TransactionEntry e = Assert.Single(t.Snapshot());
        Assert.Equal(TransactionKind.Bank, e.Kind);
        Assert.Equal("Deposited 12,300 wealth", e.Detail);
    }

    [Fact]
    public void NoteBankDeposit_StoresLocation()
    {
        (TransactionHistoryTracker t, _) = Make();
        t.NoteBankDeposit(500, "Bank of Newhaven (1/42)");
        Assert.Equal("Bank of Newhaven (1/42)", Assert.Single(t.Snapshot()).Location);
    }

    [Fact]
    public void NoteStash_StoresLocation()
    {
        (TransactionHistoryTracker t, _) = Make();
        t.NoteStash(new[] { ("gold", 10L) }, Array.Empty<string>(), "Hollow Stump (3/7)");
        Assert.Equal("Hollow Stump (3/7)", Assert.Single(t.Snapshot()).Location);
    }

    [Fact]
    public void Note_LocationDefaultsNull_WhenUnknown()
    {
        (TransactionHistoryTracker t, _) = Make();
        t.NoteBankDeposit(500);
        Assert.Null(Assert.Single(t.Snapshot()).Location);
    }

    [Fact]
    public void NoteBankDeposit_IgnoresNonPositive()
    {
        (TransactionHistoryTracker t, _) = Make();
        t.NoteBankDeposit(0);
        t.NoteBankDeposit(-100);
        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void NoteStash_RendersItemsThenCoins()
    {
        (TransactionHistoryTracker t, _) = Make();
        t.NoteStash(
            new[] { ("gold", 400L), ("platinum", 40L) },
            new[] { "a torch" });

        // Items get their own row; the coin goes on the room's one coin row.
        IReadOnlyList<TransactionEntry> rows = t.Snapshot();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, e => Assert.Equal(TransactionKind.Stash, e.Kind));
        Assert.Equal("Hid a torch", rows[0].Detail);
        Assert.Equal("Last: 40 platinum, 400 gold - Total Stashes: 1 | Avg: 44 platinum | Total: 40 platinum, 400 gold (≈ 44 platinum)",
            rows[1].Detail);
    }

    [Fact]
    public void NoteStash_GroupsDuplicateItems()
    {
        (TransactionHistoryTracker t, _) = Make();
        t.NoteStash(
            Array.Empty<(string, long)>(),
            new[] { "a torch", "a torch", "a torch" });

        Assert.Equal("Hid a torch ×3", Assert.Single(t.Snapshot()).Detail);
    }

    [Fact]
    public void NoteStash_CoinsOnly_NoItems()
    {
        (TransactionHistoryTracker t, _) = Make();
        t.NoteStash(new[] { ("copper", 250_000L) }, Array.Empty<string>());
        Assert.Equal("Last: 250,000 copper - Total Stashes: 1 | Avg: 25 platinum | Total: 250,000 copper (≈ 25 platinum)",
            Assert.Single(t.Snapshot()).Detail);
    }

    // ----- one coin row per stash room ---------------------------------

    [Fact]
    public void CoinStash_SameRoom_IsOneRow_WithLastAverageAndTotal()
    {
        (TransactionHistoryTracker t, Clock c) = Make();
        const string room = "Hollow Stump (3/7)";
        var replaced = new List<(TransactionEntry Old, TransactionEntry New)>();
        t.EntryReplaced += (o, n) => replaced.Add((o, n));

        t.NoteStash(new[] { ("silver", 100L) }, Array.Empty<string>(), room);
        c.Advance(60);
        t.NoteStash(new[] { ("silver", 300L) }, Array.Empty<string>(), room);

        TransactionEntry e = Assert.Single(t.Snapshot());
        Assert.Equal(c.Now, e.Time);                      // the latest stash's time
        Assert.Equal("Last: 300 silver - Total Stashes: 2 | Avg: 20 gold | Total: 400 silver (≈ 40 gold)", e.Detail);
        Assert.Equal(room, e.Location);
        Assert.Equal(e, Assert.Single(replaced).New);
    }

    [Fact]
    public void CoinStash_EchoesOfOneVisit_ShareTheLastAmount()
    {
        // The game answers a mixed stash with one line per coin type, a moment apart.
        (TransactionHistoryTracker t, Clock c) = Make();
        t.NoteStash(new[] { ("gold", 1L) }, Array.Empty<string>(), "Stump (3/7)");
        c.Advance(1);
        t.NoteStash(new[] { ("silver", 115L) }, Array.Empty<string>(), "Stump (3/7)");

        Assert.Equal("Last: 1 gold, 115 silver - Total Stashes: 1 | Avg: 12 gold, 5 silver | Total: 1 gold, 115 silver (≈ 12.5 gold)",
            Assert.Single(t.Snapshot()).Detail);
    }

    // A stash transfer's load: Last is what was taken, Total what the search showed
    // left; the average stays the average hide.
    [Fact]
    public void StashWithdrawal_SetsLastToWhatWasTaken_AndTotalToWhatIsLeft()
    {
        (TransactionHistoryTracker t, Clock c) = Make();
        const string room = "Stump (3/7)";
        t.NoteStash(new[] { ("silver", 4000L) }, Array.Empty<string>(), room);
        c.Now += TimeSpan.FromMinutes(5);
        t.NoteStash(new[] { ("silver", 2000L) }, Array.Empty<string>(), room);
        c.Now += TimeSpan.FromMinutes(5);
        List<(TransactionEntry Old, TransactionEntry New)> replaced = new();
        t.EntryReplaced += (o, n) => replaced.Add((o, n));

        t.NoteStashWithdrawal(new[] { ("silver", 2500L) }, new[] { ("silver", 3500L) }, room);

        TransactionEntry e = Assert.Single(t.Snapshot());
        Assert.Equal("Last: took 2,500 silver - Total Stashes: 2 | Avg: 3 platinum | Total: 3,500 silver (≈ 3.5 platinum)", e.Detail);
        Assert.Equal(c.Now, e.Time);
        Assert.Equal(e, Assert.Single(replaced).New);
    }

    [Fact]
    public void StashWithdrawal_ThatEmptiesTheStash_ReadsNothing_AndAHideAfterStartsAfresh()
    {
        (TransactionHistoryTracker t, Clock c) = Make();
        const string room = "Stump (3/7)";
        t.NoteStash(new[] { ("silver", 4000L) }, Array.Empty<string>(), room);
        c.Now += TimeSpan.FromMinutes(5);

        t.NoteStashWithdrawal(new[] { ("silver", 4000L) }, Array.Empty<(string, long)>(), room);
        Assert.Equal("Last: took 4,000 silver - Total Stashes: 1 | Avg: 4 platinum | Total: nothing",
            Assert.Single(t.Snapshot()).Detail);

        // Hidden again within moments: a new visit, not more of the withdrawal.
        c.Now += TimeSpan.FromSeconds(2);
        t.NoteStash(new[] { ("gold", 10L) }, Array.Empty<string>(), room);
        Assert.Equal("Last: 10 gold - Total Stashes: 2 | Avg: 2 platinum, 5 gold | Total: 10 gold",
            Assert.Single(t.Snapshot()).Detail);
    }

    [Fact]
    public void StashWithdrawal_WithNothingTaken_OnlyCorrectsTheTotal()
    {
        (TransactionHistoryTracker t, Clock c) = Make();
        const string room = "Stump (3/7)";
        t.NoteStash(new[] { ("silver", 4000L) }, Array.Empty<string>(), room);
        c.Now += TimeSpan.FromMinutes(5);

        // The search found it empty: someone else drew on it.
        t.NoteStashWithdrawal(Array.Empty<(string, long)>(), Array.Empty<(string, long)>(), room);

        Assert.Equal("Last: 4,000 silver - Total Stashes: 1 | Avg: 4 platinum | Total: nothing",
            Assert.Single(t.Snapshot()).Detail);
    }

    [Fact]
    public void StashWithdrawal_FromARoomWithNoRow_AddsNone()
    {
        (TransactionHistoryTracker t, _) = Make();

        t.NoteStashWithdrawal(new[] { ("silver", 100L) }, new[] { ("silver", 50L) }, "Stump (3/7)");

        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void StashWithdrawal_Row_ComesBackFromTheSavedLogAsItWas()
    {
        (TransactionHistoryTracker t, Clock c) = Make();
        const string room = "Stump (3/7)";
        t.NoteStash(new[] { ("silver", 4000L) }, Array.Empty<string>(), room);
        c.Now += TimeSpan.FromMinutes(5);
        t.NoteStash(new[] { ("silver", 2000L) }, Array.Empty<string>(), room);
        c.Now += TimeSpan.FromMinutes(5);
        t.NoteStashWithdrawal(new[] { ("silver", 2500L) }, new[] { ("silver", 3500L) }, room);
        TransactionEntry saved = Assert.Single(t.Snapshot());

        (TransactionHistoryTracker back, Clock c2) = Make();
        back.Hydrate(new[] { saved });
        Assert.Equal(saved.Detail, Assert.Single(back.Snapshot()).Detail);

        // And it is still the room's one row: the next hide extends it.
        c2.Now = saved.Time + TimeSpan.FromHours(1);
        back.NoteStash(new[] { ("silver", 500L) }, Array.Empty<string>(), room);
        Assert.Equal("Last: 500 silver - Total Stashes: 3 | Avg: 2 platinum, 16 gold, 6 silver, 6 copper | Total: 4,000 silver (≈ 4 platinum)",
            Assert.Single(back.Snapshot()).Detail);
    }

    [Fact]
    public void CoinStash_EachRoomKeepsItsOwnRow_AndTheLatestMovesToTheEnd()
    {
        (TransactionHistoryTracker t, Clock c) = Make();
        t.NoteStash(new[] { ("gold", 5L) }, Array.Empty<string>(), "A (1/1)");
        c.Advance(60);
        t.NoteStash(new[] { ("gold", 7L) }, Array.Empty<string>(), "B (1/2)");
        c.Advance(60);
        t.NoteStash(new[] { ("gold", 1L) }, Array.Empty<string>(), "A (1/1)");

        IReadOnlyList<TransactionEntry> rows = t.Snapshot();
        Assert.Equal(new[] { "B (1/2)", "A (1/1)" }, rows.Select(r => r.Location));
        Assert.Equal("Last: 1 gold - Total Stashes: 2 | Avg: 3 gold | Total: 6 gold", rows[1].Detail);
    }

    [Fact]
    public void Hydrate_FoldsAnOlderLogsCoinRows_AndReadsARolledRowBack()
    {
        (TransactionHistoryTracker t, Clock c) = Make();
        DateTimeOffset t0 = c.Now;
        t.Hydrate(new[]
        {
            new TransactionEntry(t0, TransactionKind.Stash, "Hid 94 silver", "Stump (3/7)"),
            new TransactionEntry(t0.AddSeconds(1), TransactionKind.Stash, "Hid 1 gold", "Stump (3/7)"),
            new TransactionEntry(t0.AddMinutes(5), TransactionKind.Bank, "Deposited 500 wealth", "Bank (1/297)"),
            new TransactionEntry(t0.AddMinutes(9), TransactionKind.Stash, "Hid a torch", "Stump (3/7)"),
            new TransactionEntry(t0.AddMinutes(10), TransactionKind.Stash, "Hid 106 silver", "Stump (3/7)"),
        });

        IReadOnlyList<TransactionEntry> rows = t.Snapshot();
        Assert.Equal(3, rows.Count);
        Assert.Equal("Deposited 500 wealth", rows[0].Detail);
        Assert.Equal("Hid a torch", rows[1].Detail);
        // Two visits: 94 silver + 1 gold together, then 106 silver.
        Assert.Equal("Last: 106 silver - Total Stashes: 2 | Avg: 10 gold, 5 silver | Total: 1 gold, 200 silver (≈ 21 gold)", rows[2].Detail);

        // An earlier build's one-line roll-up reads back the same way.
        (TransactionHistoryTracker interim, _) = Make();
        interim.Hydrate(new[]
        {
            new TransactionEntry(t0, TransactionKind.Stash,
                "Hid 127 silver — avg 12 gold, 7 silver over 3 stashes — total 381 silver", "Stump (3/7)"),
        });
        Assert.Equal("Last: 127 silver - Total Stashes: 3 | Avg: 12 gold, 7 silver | Total: 381 silver (≈ 38.1 gold)",
            Assert.Single(interim.Snapshot()).Detail);

        // That row, loaded again, keeps counting from where it was.
        (TransactionHistoryTracker again, Clock c2) = Make();
        again.Hydrate(rows);
        c2.Now = t0.AddHours(1);
        again.NoteStash(new[] { ("silver", 100L) }, Array.Empty<string>(), "Stump (3/7)");
        Assert.Equal("Last: 100 silver - Total Stashes: 3 | Avg: 10 gold, 3 silver, 3 copper | Total: 1 gold, 300 silver (≈ 31 gold)",
            again.Snapshot()[^1].Detail);
    }

    [Fact]
    public void NoteStash_Empty_Ignored()
    {
        (TransactionHistoryTracker t, _) = Make();
        t.NoteStash(Array.Empty<(string, long)>(), Array.Empty<string>());
        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void Snapshot_IsOldestFirst_WithClockStamps()
    {
        (TransactionHistoryTracker t, Clock c) = Make();
        t.NoteBankDeposit(100);
        c.Advance(5);
        t.NoteBankDeposit(200);

        IReadOnlyList<TransactionEntry> snap = t.Snapshot();
        Assert.Equal(2, snap.Count);
        Assert.Equal("Deposited 100 wealth", snap[0].Detail);
        Assert.Equal("Deposited 200 wealth", snap[1].Detail);
        Assert.True(snap[1].Time > snap[0].Time);
    }

    [Fact]
    public void Add_EvictsOldest_PastCap()
    {
        (TransactionHistoryTracker t, _) = Make();
        for (int i = 1; i <= TransactionHistoryTracker.MaxEntries + 10; i++)
            t.NoteBankDeposit(i);

        IReadOnlyList<TransactionEntry> snap = t.Snapshot();
        Assert.Equal(TransactionHistoryTracker.MaxEntries, snap.Count);
        // Oldest 10 (amounts 1..10) evicted; the window now starts at 11.
        Assert.Equal("Deposited 11 wealth", snap[0].Detail);
    }

    [Fact]
    public void Reset_ClearsAndFiresChanged()
    {
        (TransactionHistoryTracker t, _) = Make();
        t.NoteBankDeposit(500);
        int changed = 0;
        t.Changed += () => changed++;

        t.Reset();

        Assert.Empty(t.Snapshot());
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Note_FiresChanged()
    {
        (TransactionHistoryTracker t, _) = Make();
        int changed = 0;
        t.Changed += () => changed++;

        t.NoteBankDeposit(100);
        t.NoteStash(new[] { ("gold", 10L) }, Array.Empty<string>());

        Assert.Equal(2, changed);
    }

    [Fact]
    public void Hydrate_ReplacesLedgerAndFiresChanged()
    {
        (TransactionHistoryTracker t, _) = Make();
        t.NoteBankDeposit(100);            // pre-existing row, should be replaced
        int changed = 0;
        t.Changed += () => changed++;

        TransactionEntry a = new(new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero),
            TransactionKind.Bank, "Deposited 900 wealth", "Bank (1/2)");
        TransactionEntry b = new(new DateTimeOffset(2026, 1, 1, 8, 5, 0, TimeSpan.Zero),
            TransactionKind.Stash, "Hid a torch", null);
        t.Hydrate(new[] { a, b });

        Assert.Equal(new[] { a, b }, t.Snapshot());
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Hydrate_DoesNotRePersist_ViaEntryAdded()
    {
        // Reloading disk history must not re-fire EntryAdded — that hook writes
        // back to the log and would double the persisted rows on every reconnect.
        (TransactionHistoryTracker t, _) = Make();
        int added = 0;
        t.EntryAdded += _ => added++;

        t.Hydrate(new[]
        {
            new TransactionEntry(DateTimeOffset.Now, TransactionKind.Bank, "Deposited 5 wealth", null),
        });

        Assert.Equal(0, added);
    }

    [Fact]
    public void Hydrate_TrimsToCap()
    {
        (TransactionHistoryTracker t, _) = Make();
        TransactionEntry[] many = new TransactionEntry[TransactionHistoryTracker.MaxEntries + 5];
        for (int i = 0; i < many.Length; i++)
            many[i] = new TransactionEntry(DateTimeOffset.Now, TransactionKind.Bank, $"Deposited {i} wealth", null);

        t.Hydrate(many);

        IReadOnlyList<TransactionEntry> snap = t.Snapshot();
        Assert.Equal(TransactionHistoryTracker.MaxEntries, snap.Count);
        // Oldest 5 dropped; the window starts at index 5.
        Assert.Equal("Deposited 5 wealth", snap[0].Detail);
    }

    // ----- shop visits --------------------------------------------------

    [Fact]
    public void Sales_AtOneShop_AreOneRowPerVisit()
    {
        (TransactionHistoryTracker t, Clock c) = Make();
        const string shop = "General Store (1/2324)";
        t.NoteSale("orc-head", 5, 1000, shop);
        c.Advance(3);
        t.NoteSale("club", 1, 250, shop);
        c.Advance(3);
        t.NoteSale("orc-head", 2, 400, shop);

        TransactionEntry e = Assert.Single(t.Snapshot());
        Assert.Equal(TransactionKind.Sold, e.Kind);
        Assert.Equal("Sold orc-head ×7, club for 16 gold, 5 silver", e.Detail);
        Assert.Equal(shop, e.Location);

        // A later visit, or a purchase, is its own row.
        c.Advance(600);
        t.NoteSale("club", 1, 250, shop);
        t.NotePurchase("lantern", 1, 396, shop);
        IReadOnlyList<TransactionEntry> rows = t.Snapshot();
        Assert.Equal(3, rows.Count);
        Assert.Equal("Sold club for 2 gold, 5 silver", rows[1].Detail);
        Assert.Equal(TransactionKind.Bought, rows[2].Kind);
        Assert.Equal("Bought lantern for 3 gold, 9 silver, 6 copper", rows[2].Detail);
    }

    // ----- keep marks ---------------------------------------------------

    [Fact]
    public void Keep_IsOnTheEntry_FollowsACoinRowUpdate_AndSurvivesAReload()
    {
        (TransactionHistoryTracker t, Clock c) = Make();
        var replaced = 0;
        t.EntryReplaced += (_, _) => replaced++;
        t.NoteStash(new[] { ("gold", 5L) }, Array.Empty<string>(), "A (1/1)");

        TransactionEntry kept = t.SetKeep(Assert.Single(t.Snapshot()), true);
        Assert.True(kept.Keep);
        Assert.Equal(1, replaced);                         // the saved log is rewritten

        c.Advance(60);
        t.NoteStash(new[] { ("gold", 1L) }, Array.Empty<string>(), "A (1/1)");
        Assert.True(Assert.Single(t.Snapshot()).Keep);     // the mark follows the room's row

        (TransactionHistoryTracker again, _) = Make();
        again.Hydrate(t.Snapshot());
        Assert.True(Assert.Single(again.Snapshot()).Keep);
    }

    // The Total line adds what the coins come to, in the highest coin it reaches —
    // and a row carrying that reads back the same.
    [Fact]
    public void CoinStash_TotalLine_AddsItsWorth()
    {
        (TransactionHistoryTracker t, Clock c) = Make();
        t.NoteStash(new[] { ("gold", 11L), ("silver", 8_617L) }, Array.Empty<string>(), "Tunnel (9/413)");
        string detail = Assert.Single(t.Snapshot()).Detail;
        Assert.EndsWith("Total: 11 gold, 8,617 silver (≈ 8.7 platinum)", detail);

        (TransactionHistoryTracker again, _) = Make();
        again.Hydrate(t.Snapshot());
        Assert.Equal(detail, Assert.Single(again.Snapshot()).Detail);

        // One pile of the coin the worth would be in: nothing to add.
        (TransactionHistoryTracker plain, _) = Make();
        plain.NoteStash(new[] { ("gold", 5L) }, Array.Empty<string>());
        Assert.EndsWith("Total: 5 gold", Assert.Single(plain.Snapshot()).Detail);
    }
}
