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

        db.RecordObservation("Forged", null, null, "Lawful", "Magebane", "Mudd Life Crisis", null, now);

        Assert.Single(db.Players);
        PlayerRecord r = db.Players[0];
        Assert.Equal("Forged", r.GivenName);
        Assert.Equal(string.Empty, r.FamilyName);
        Assert.Equal("Lawful",   r.Alignment);
        Assert.Equal("Magebane", r.Title);
        Assert.Equal("Mudd Life Crisis", r.Gang);
        Assert.Equal(now, r.FirstSeenUtc);
        Assert.Equal(now, r.LastSeenUtc);
    }

    [Fact]
    public void RecordObservation_SplitsTwoWordName_IntoGivenAndFamily()
    {
        PlayerDatabase db = new();
        DateTime now = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordObservation("Forged Paradigm", null, null, null, "Magebane", null, null, now);

        Assert.Equal("Forged",   db.Players[0].GivenName);
        Assert.Equal("Paradigm", db.Players[0].FamilyName);
        Assert.Equal("Forged Paradigm", db.Players[0].DisplayName);
    }

    [Fact]
    public void RecordObservation_PreservesUserAuthoredFields_OnRefresh()
    {
        PlayerDatabase db = new();
        DateTime first = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime later = new(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc);

        db.RecordObservation("Forged", "Mage", "Elf", "Good", "the Wise", null, null, first);
        db.EditNotes("Forged", "trusted healer");
        db.EditRecord("Forged", db.Players[0] with
        {
            RemoteControls       = PlayerRemoteControls.QueryHealthStatus | PlayerRemoteControls.RequestInvite,
            InviteToPartyIfSeen  = true,
            JoinPartyIfInvited   = true,
            DontAutoDelete       = true,
        });

        db.RecordObservation("Forged", "Archmage", "Elf", "Good", null, "Mudd Life Crisis", null, later);

        Assert.Equal("Archmage", db.Players[0].Class);
        Assert.Equal("trusted healer", db.Players[0].Notes);
        Assert.True(db.Players[0].RemoteControls.HasFlag(PlayerRemoteControls.QueryHealthStatus));
        Assert.True(db.Players[0].RemoteControls.HasFlag(PlayerRemoteControls.RequestInvite));
        Assert.True(db.Players[0].InviteToPartyIfSeen);
        Assert.True(db.Players[0].JoinPartyIfInvited);
        Assert.True(db.Players[0].DontAutoDelete);
        Assert.Equal(first, db.Players[0].FirstSeenUtc);
        Assert.Equal(later, db.Players[0].LastSeenUtc);
        Assert.Equal("Mudd Life Crisis", db.Players[0].Gang);
    }

    [Fact]
    public void RecordObservation_LeavesExistingFields_WhenNewObservationNull()
    {
        PlayerDatabase db = new();
        DateTime now = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordObservation("Forged", "Mage", "Elf", "Good", "the Wise", null, null, now);
        db.RecordObservation("Forged", null, null, null, null, null, null, now.AddDays(1));

        Assert.Equal("Mage", db.Players[0].Class);
        Assert.Equal("the Wise", db.Players[0].Title);
    }

    [Fact]
    public void PurgeStale_DropsRowsOlderThanCutoff()
    {
        PlayerDatabase db = new();
        DateTime now = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordObservation("Recent", null, null, null, null, null, null, now.AddDays(-10));
        db.RecordObservation("Ancient", null, null, null, null, null, null, now.AddDays(-120));

        int removed = db.PurgeStale(days: 90, nowUtc: now);

        Assert.Equal(1, removed);
        Assert.Single(db.Players);
        Assert.Equal("Recent", db.Players[0].GivenName);
    }

    [Fact]
    public void PurgeStale_SkipsRecords_FlaggedDontAutoDelete()
    {
        PlayerDatabase db = new();
        DateTime now = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordObservation("Keeper", null, null, null, null, null, null, now.AddDays(-365));
        db.EditRecord("Keeper", db.Players[0] with { DontAutoDelete = true });

        int removed = db.PurgeStale(days: 90, nowUtc: now);

        Assert.Equal(0, removed);
        Assert.Single(db.Players);
    }

    [Fact]
    public void EditNotes_ReturnsFalse_WhenUnknown()
    {
        PlayerDatabase db = new();
        Assert.False(db.EditNotes("Ghost", "anything"));
    }

    [Fact]
    public void SplitName_HandlesEdgeCases()
    {
        Assert.Equal(("Forged", string.Empty),       PlayerRecord.SplitName("Forged"));
        Assert.Equal(("Forged", "Paradigm"),         PlayerRecord.SplitName("Forged Paradigm"));
        Assert.Equal(("Forged", "Paradigm of Doom"), PlayerRecord.SplitName("Forged Paradigm of Doom"));
        Assert.Equal((string.Empty, string.Empty),   PlayerRecord.SplitName(""));
        Assert.Equal((string.Empty, string.Empty),   PlayerRecord.SplitName("   "));
        Assert.Equal((string.Empty, string.Empty),   PlayerRecord.SplitName(null));
    }
}
