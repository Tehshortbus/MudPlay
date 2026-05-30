using FujinTerm.Models.GameData;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class PlayerDatabaseTests
{
    [Fact]
    public void RecordObservation_CreatesNewRecord_WhenNameNotKnown()
    {
        PlayerDatabase db = new();
        DateTime now = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordObservation("Forged", "Mage", "Elf", "Good", "the Wise", now);

        Assert.Single(db.Players);
        Assert.Equal("Mage", db.Players[0].Class);
        Assert.Equal(now, db.Players[0].FirstSeenUtc);
        Assert.Equal(now, db.Players[0].LastSeenUtc);
    }

    [Fact]
    public void RecordObservation_PreservesNotesAndPermissions_OnRefresh()
    {
        PlayerDatabase db = new();
        DateTime first = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime later = new(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc);

        db.RecordObservation("Forged", "Mage", "Elf", "Good", "the Wise", first);
        db.EditNotes("Forged", "trusted healer");
        db.EditPermissions("Forged", new PlayerPermissions(AllowQuery: true, AllowControl: false));

        db.RecordObservation("Forged", "Archmage", "Elf", "Good", null, later);

        Assert.Equal("Archmage", db.Players[0].Class);
        Assert.Equal("trusted healer", db.Players[0].Notes);
        Assert.True(db.Players[0].Permissions.AllowQuery);
        Assert.False(db.Players[0].Permissions.AllowControl);
        Assert.Equal(first, db.Players[0].FirstSeenUtc);
        Assert.Equal(later, db.Players[0].LastSeenUtc);
    }

    [Fact]
    public void RecordObservation_LeavesExistingFields_WhenNewObservationNull()
    {
        PlayerDatabase db = new();
        DateTime now = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordObservation("Forged", "Mage", "Elf", "Good", "the Wise", now);
        db.RecordObservation("Forged", null, null, null, null, now.AddDays(1));

        Assert.Equal("Mage", db.Players[0].Class);
        Assert.Equal("the Wise", db.Players[0].Title);
    }

    [Fact]
    public void PurgeStale_DropsRowsOlderThanCutoff()
    {
        PlayerDatabase db = new();
        DateTime now = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordObservation("Recent", null, null, null, null, now.AddDays(-10));
        db.RecordObservation("Ancient", null, null, null, null, now.AddDays(-120));

        int removed = db.PurgeStale(days: 90, nowUtc: now);

        Assert.Equal(1, removed);
        Assert.Single(db.Players);
        Assert.Equal("Recent", db.Players[0].Name);
    }

    [Fact]
    public void EditNotes_ReturnsFalse_WhenUnknown()
    {
        PlayerDatabase db = new();
        Assert.False(db.EditNotes("Ghost", "anything"));
    }
}
