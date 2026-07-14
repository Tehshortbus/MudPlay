using FujinTerm.Game.Cash;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Phase 12 — <see cref="TransactionHistoryTracker"/> ledger. An injected clock
/// stamps each entry deterministically; the tests pin the rendered detail
/// strings (bank vs. stash, item grouping, coin denominations), the
/// ignore-empty guards, the cap eviction, and the <c>Changed</c> signal.
/// </summary>
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

        TransactionEntry e = Assert.Single(t.Snapshot());
        Assert.Equal(TransactionKind.Stash, e.Kind);
        Assert.Equal("Hid a torch, 400 gold, 40 platinum", e.Detail);
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
        Assert.Equal("Hid 250,000 copper", Assert.Single(t.Snapshot()).Detail);
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
}
