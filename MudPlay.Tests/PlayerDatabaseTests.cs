using System.Collections.Generic;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class PlayerDatabaseTests
{
    [Fact]
    public void RecordObservation_CreatesMergedRecord_WhenNameNotKnown()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime now = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordObservation("Forged", null, null, "Lawful", "Magebane", "Mudd Life Crisis", null, now);

        Assert.Single(db.Players);
        PlayerRecord r = db.Players[0];
        Assert.Equal("Forged",   r.GivenName);
        Assert.Equal(string.Empty, r.FamilyName);
        Assert.Equal("Lawful",   r.Alignment);
        Assert.Equal("Magebane", r.Title);
        Assert.Equal("Mudd Life Crisis", r.Gang);
        Assert.Equal(now, r.FirstSeenUtc);
        Assert.Equal(now, r.LastSeenUtc);
        // No profile loaded — customisation slice stays at its defaults.
        Assert.Equal(PlayerRemoteControls.None, r.RemoteControls);
        Assert.False(r.InviteToPartyIfSeen);
    }

    [Fact]
    public void RecordObservation_SplitsTwoWordName_IntoGivenAndFamily()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime now = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordObservation("Forged Paradigm", null, null, null, "Magebane", null, null, now);

        Assert.Equal("Forged",          db.Players[0].GivenName);
        Assert.Equal("Paradigm",        db.Players[0].FamilyName);
        Assert.Equal("Forged Paradigm", db.Players[0].DisplayName);
    }

    [Fact]
    public void EditCustomization_AppliesToMergedView_WithoutTouchingObservation()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime first = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime later = new(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc);

        db.RecordObservation("Forged", "Mage", "Elf", "Good", "the Wise", null, null, first);
        db.EditCustomization("Forged", new PlayerCustomization(
            RemoteControls:      PlayerRemoteControls.QueryHealthStatus | PlayerRemoteControls.RequestInvite,
            InviteToPartyIfSeen: true,
            JoinPartyIfInvited:  true,
            DontAutoDelete:      true,
            Notes:               "trusted healer"));

        // Refresh from server — observation fields move, customization stays.
        db.RecordObservation("Forged", "Archmage", "Elf", "Good", null, "Mudd Life Crisis", null, later);

        PlayerRecord r = db.Players[0];
        Assert.Equal("Archmage", r.Class);
        Assert.Equal("trusted healer", r.Notes);
        Assert.True(r.RemoteControls.HasFlag(PlayerRemoteControls.QueryHealthStatus));
        Assert.True(r.RemoteControls.HasFlag(PlayerRemoteControls.RequestInvite));
        Assert.True(r.InviteToPartyIfSeen);
        Assert.True(r.JoinPartyIfInvited);
        Assert.True(r.DontAutoDelete);
        Assert.Equal(first, r.FirstSeenUtc);
        Assert.Equal(later, r.LastSeenUtc);
        Assert.Equal("Mudd Life Crisis", r.Gang);
    }

    [Fact]
    public void EditCustomization_RemovesEntry_WhenAllFieldsDefault()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime now = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        db.RecordObservation("Forged", null, null, null, "Magebane", null, null, now);

        // Set a customisation, then revert to defaults.
        db.EditCustomization("Forged", new PlayerCustomization(DontAutoDelete: true));
        Assert.True(db.Players[0].DontAutoDelete);

        db.EditCustomization("Forged", new PlayerCustomization()); // all defaults
        Assert.False(db.Players[0].DontAutoDelete);
        Assert.False(db.Players[0].InviteToPartyIfSeen);
    }

    [Fact]
    public void RecordObservation_LeavesExistingFields_WhenNewObservationNull()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime now = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordObservation("Forged", "Mage", "Elf", "Good", "the Wise", null, null, now);
        db.RecordObservation("Forged", null, null, null, null, null, null, now.AddDays(1));

        Assert.Equal("Mage", db.Players[0].Class);
        Assert.Equal("the Wise", db.Players[0].Title);
    }

    [Fact]
    public void PurgeStale_DropsRowsOlderThanCutoff()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime now = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordObservation("Recent",  null, null, null, null, null, null, now.AddDays(-10));
        db.RecordObservation("Ancient", null, null, null, null, null, null, now.AddDays(-120));

        int removed = db.PurgeStale(days: 90, nowUtc: now);

        Assert.Equal(1, removed);
        Assert.Single(db.Players);
        Assert.Equal("Recent", db.Players[0].GivenName);
    }

    [Fact]
    public void PurgeStale_SkipsRecords_FlaggedDontAutoDelete()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime now = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordObservation("Keeper", null, null, null, null, null, null, now.AddDays(-365));
        db.EditCustomization("Keeper", new PlayerCustomization(DontAutoDelete: true));

        int removed = db.PurgeStale(days: 90, nowUtc: now);

        Assert.Equal(0, removed);
        Assert.Single(db.Players);
    }

    [Fact]
    public void SplitName_HandlesEdgeCases()
    {
        Assert.Equal(("Forged", string.Empty),       PlayerObservation.SplitName("Forged"));
        Assert.Equal(("Forged", "Paradigm"),         PlayerObservation.SplitName("Forged Paradigm"));
        Assert.Equal(("Forged", "Paradigm of Doom"), PlayerObservation.SplitName("Forged Paradigm of Doom"));
        Assert.Equal((string.Empty, string.Empty),   PlayerObservation.SplitName(""));
        Assert.Equal((string.Empty, string.Empty),   PlayerObservation.SplitName("   "));
        Assert.Equal((string.Empty, string.Empty),   PlayerObservation.SplitName(null));
    }

    // ===== Family-name rename merge (the Debbie Par / Debbie Schwartz bug) =====

    [Fact]
    public void RecordObservation_FamilyRename_MergesIntoExistingRecord()
    {
        // The bug: train-stats lets a player change family name without
        // losing identity. Re-observing under the new family must merge
        // into the existing row, not create a duplicate.
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime first = new(2026, 5, 30, 0, 0, 0, DateTimeKind.Utc);
        DateTime later = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordObservation("Debbie Schwartz", "Mage", "Elf", "Good", null, null, null, first);
        db.RecordObservation("Debbie Par",      null,   null,  null,   null, null, null, later);

        Assert.Single(db.Players);
        PlayerRecord r = db.Players[0];
        Assert.Equal("Debbie", r.GivenName);
        Assert.Equal("Par",    r.FamilyName);   // newest observation wins
        Assert.Equal("Mage",   r.Class);        // un-observed → preserved
        Assert.Equal("Elf",    r.Race);
        Assert.Equal("Good",   r.Alignment);
        Assert.Equal(first, r.FirstSeenUtc);    // "we've known this player since"
        Assert.Equal(later, r.LastSeenUtc);
    }

    [Fact]
    public void RecordObservation_FamilyRename_PreservesCustomization()
    {
        // The user's per-player flags / auto-party toggles / notes /
        // DontAutoDelete must travel with the player identity, not with
        // the display name. Family-rename can never reset them.
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime first = new(2026, 5, 30, 0, 0, 0, DateTimeKind.Utc);
        DateTime later = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordObservation("Debbie Schwartz", "Mage", null, null, null, null, null, first);
        db.EditCustomization("Debbie Schwartz", new PlayerCustomization(
            RemoteControls:      PlayerRemoteControls.QueryHealthStatus,
            InviteToPartyIfSeen: true,
            DontAutoDelete:      true,
            Notes:               "trusted healer"));

        db.RecordObservation("Debbie Par", null, null, null, null, null, null, later);

        Assert.Single(db.Players);
        PlayerRecord r = db.Players[0];
        Assert.Equal("Par", r.FamilyName);
        Assert.True(r.RemoteControls.HasFlag(PlayerRemoteControls.QueryHealthStatus));
        Assert.True(r.InviteToPartyIfSeen);
        Assert.True(r.DontAutoDelete);
        Assert.Equal("trusted healer", r.Notes);
    }

    [Fact]
    public void RecordObservation_SameGivenDifferentFamilies_CountsAsOnePlayer()
    {
        // Three consecutive observations of the same given name through
        // different families. All collapse to one row; the last family
        // is the one currently displayed.
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime t1 = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime t2 = new(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc);
        DateTime t3 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordObservation("Debbie OldFamily",    null, null, null, null, null, null, t1);
        db.RecordObservation("Debbie MidFamily",    null, null, null, null, null, null, t2);
        db.RecordObservation("Debbie LatestFamily", null, null, null, null, null, null, t3);

        Assert.Single(db.Players);
        Assert.Equal("LatestFamily", db.Players[0].FamilyName);
        Assert.Equal(t1, db.Players[0].FirstSeenUtc);
        Assert.Equal(t3, db.Players[0].LastSeenUtc);
    }

    [Fact]
    public void RecordObservation_DifferentGivens_StayDistinct()
    {
        // Same family-name "Clawful" appears on multiple given names in
        // the screenshot — these are real different players and must
        // NOT merge. Sanity check the given-name keying.
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordObservation("Furnagerie Clawful", null, null, null, null, null, null, now);
        db.RecordObservation("Gammi Clawful",      null, null, null, null, null, null, now);
        db.RecordObservation("Gampi Clawful",      null, null, null, null, null, null, now);

        Assert.Equal(3, db.Players.Count);
    }

    // ===== RecordLook equipment-snapshot merge =====

    [Fact]
    public void RecordLook_NewEquipmentSnapshot_ReplacesPrevious()
    {
        // Equipment is a snapshot of what they're wearing now — a later
        // look-observation fully replaces an earlier loadout, not merges
        // item-by-item. An old loadout combined with a new loadout would
        // misrepresent both moments.
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime t1 = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime t2 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        EquipmentItem oldHelm = new("head",   "Iron Helm");
        EquipmentItem newHelm = new("head",   "Mithril Helm");
        EquipmentItem newCape = new("back",   "Crimson Cape");

        db.RecordLook("Debbie", race: null, @class: null, gang: null,
            equipment: new[] { oldHelm }, nowUtc: t1);
        db.RecordLook("Debbie", race: null, @class: null, gang: null,
            equipment: new[] { newHelm, newCape }, nowUtc: t2);

        PlayerRecord r = Assert.Single(db.Players);
        Assert.NotNull(r.Equipment);
        Assert.Equal(2, r.Equipment!.Count);
        Assert.Contains(r.Equipment, e => e.ItemName == "Mithril Helm");
        Assert.Contains(r.Equipment, e => e.ItemName == "Crimson Cape");
        Assert.DoesNotContain(r.Equipment, e => e.ItemName == "Iron Helm");
    }

    [Fact]
    public void RecordLook_NullEquipment_KeepsPreviousSnapshot()
    {
        // A later observation that didn't see equipment (e.g. who only)
        // must not erase the prior loadout — that would be replacing an
        // observation with a non-observation.
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime t1 = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime t2 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        EquipmentItem helm = new("head", "Iron Helm");
        db.RecordLook("Debbie", race: null, @class: null, gang: null,
            equipment: new[] { helm }, nowUtc: t1);
        db.RecordLook("Debbie", race: null, @class: null, gang: null,
            equipment: null, nowUtc: t2);

        PlayerRecord r = Assert.Single(db.Players);
        Assert.NotNull(r.Equipment);
        Assert.Single(r.Equipment!);
        Assert.Equal("Iron Helm", r.Equipment![0].ItemName);
    }

    // ===== RecordLevel — exact level from an @level probe reply =====

    [Fact]
    public void RecordLevel_CreatesRecord_WhenPlayerUnknown()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordLevel("Bob", 42, now);

        PlayerRecord r = Assert.Single(db.Players);
        Assert.Equal("Bob", r.GivenName);
        Assert.Equal(42, r.Level);
        Assert.Equal(now, r.LevelAt);   // staleness stamp set alongside the level
        Assert.Equal(now, r.FirstSeenUtc);
        Assert.Equal(now, r.LastSeenUtc);
    }

    [Fact]
    public void RecordLevel_UpdatesLevelAndLastSeen_KeepingOtherFields()
    {
        // A probed level supersedes the title-derived range but must not
        // disturb the other observation fields — answering @level only
        // tells us the level and that they're present right now.
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime first = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime later = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordObservation("Bob", "Mage", "Elf", "Good", "Wizard", "Guild", null, first);
        db.RecordLevel("Bob", 51, later);

        PlayerRecord r = Assert.Single(db.Players);
        Assert.Equal(51, r.Level);
        Assert.Equal(later, r.LevelAt);        // re-stamped when the level updates
        Assert.Equal("Mage",   r.Class);
        Assert.Equal("Wizard", r.Title);
        Assert.Equal("Guild",  r.Gang);
        Assert.Equal(first, r.FirstSeenUtc);   // "known since" preserved
        Assert.Equal(later, r.LastSeenUtc);    // presence refreshed
    }

    [Fact]
    public void RecordLevel_DropsATitleWhoseBandExcludesTheReportedLevel()
    {
        // A rerolled character answers @level 1 while the record still carries the
        // old character's "Curate" (Priest 10-14): that band would otherwise outrank
        // the reading, so a manual @level changed nothing. A title that fits stays.
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime now = new(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);
        db.RecordObservation("Raijin", "Priest", null, null, "Curate", null, null, now);
        db.RecordObservation("Bob", "Priest", null, null, "Curate", null, null, now);

        db.RecordLevel("Raijin", 1, now);
        db.RecordLevel("Bob", 12, now);

        Assert.Null(db.Find("Raijin")!.Title);
        Assert.Equal("Curate", db.Find("Bob")!.Title);
    }

    [Fact]
    public void RecordStatedClass_NewClass_DropsTheOldCharactersObservations()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime now = new(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);
        db.RecordObservation("Raijin", "Priest", "Elf", "Good", "Curate", "asdf", null, now);
        db.RecordLevel("Raijin", 12, now);
        db.RecordVersion("Raijin", "MudPlay 3.74.0", now);

        Assert.Equal("Priest", db.RecordStatedClass("Raijin", "Mage"));

        PlayerRecord r = db.Find("Raijin")!;
        Assert.Equal("Mage", r.Class);
        Assert.Null(r.Title);
        Assert.Null(r.Level);
        Assert.Null(r.Race);
        Assert.Equal("asdf", r.Gang);                     // not character-specific
        Assert.Equal("MudPlay 3.74.0", r.Version);
        Assert.Null(db.RecordStatedClass("Raijin", "mage"));   // same class → no change
    }

    // ----- Relationship and PvP response (realm tier) -------------------

    [Fact]
    public void EveryPlayerStartsNeutral_WithNoResponseOfTheirOwn()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        db.RecordObservation("Raijin Storm", null, null, "Good", "Curate", null, null, DateTime.UtcNow);

        PlayerRecord r = db.Find("Raijin")!;
        Assert.Equal(PlayerRelationship.Neutral, r.Relationship);
        Assert.Null(r.PvpResponse);
    }

    [Fact]
    public void SetRelationship_StoresBoth_AndSaysWhenNothingChanged()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        db.RecordObservation("Raijin Storm", null, null, null, null, null, null, DateTime.UtcNow);

        Assert.True(db.SetRelationship("Raijin Storm", PlayerRelationship.Enemy, PvpAction.Flee));
        Assert.False(db.SetRelationship("Raijin", PlayerRelationship.Enemy, PvpAction.Flee));   // unchanged
        Assert.False(db.SetRelationship("Nobody", PlayerRelationship.Friend, null));           // no record

        PlayerRecord r = db.Find("Raijin")!;
        Assert.Equal(PlayerRelationship.Enemy, r.Relationship);
        Assert.Equal(PvpAction.Flee, r.PvpResponse);
    }

    [Fact]
    public void Relationship_OutlastsANewSighting_AndAClassChange()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime now = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
        db.RecordObservation("Raijin", "Priest", null, null, "Curate", null, null, now);
        db.SetRelationship("Raijin", PlayerRelationship.Friend, null);

        db.RecordObservation("Raijin Storm", null, null, "Good", "Curate", "asdf", null, now.AddHours(1));
        db.RecordStatedClass("Raijin", "Mage");

        Assert.Equal(PlayerRelationship.Friend, db.Find("Raijin")!.Relationship);
    }

    [Fact]
    public void PurgeStale_KeepsFriendsAndEnemies()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime old = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        db.RecordObservation("Stranger", null, null, null, null, null, null, old);
        db.RecordObservation("Ally", null, null, null, null, null, null, old);
        db.RecordObservation("Foe", null, null, null, null, null, null, old);
        db.SetRelationship("Ally", PlayerRelationship.Friend, null);
        db.SetRelationship("Foe", PlayerRelationship.Enemy, PvpAction.HangUp);

        Assert.Equal(1, db.PurgeStale(30, old.AddDays(90)));

        Assert.Null(db.Find("Stranger"));
        Assert.NotNull(db.Find("Ally"));
        Assert.NotNull(db.Find("Foe"));
    }

    [Fact]
    public void Relationship_IsSavedByName_AndReadsBackFromAnOlderRecordAsNeutral()
    {
        PlayerObservation enemy = new("Raijin", "Storm", null, null, null, null, null, null,
            DateTime.UtcNow, DateTime.UtcNow) { Relationship = PlayerRelationship.Enemy, PvpResponse = PvpAction.ChaseAttack };
        string json = System.Text.Json.JsonSerializer.Serialize(enemy, MudPlay.Services.JsonStore.Options);
        Assert.Contains("\"Enemy\"", json);
        Assert.Contains("\"ChaseAttack\"", json);
        PlayerObservation back = System.Text.Json.JsonSerializer.Deserialize<PlayerObservation>(json, MudPlay.Services.JsonStore.Options)!;
        Assert.Equal(PlayerRelationship.Enemy, back.Relationship);
        Assert.Equal(PvpAction.ChaseAttack, back.PvpResponse);

        // A record written before the field existed.
        const string older = """{"GivenName":"Raijin","FamilyName":"","FirstSeenUtc":"2026-01-01T00:00:00Z","LastSeenUtc":"2026-01-01T00:00:00Z"}""";
        PlayerObservation plain = System.Text.Json.JsonSerializer.Deserialize<PlayerObservation>(older, MudPlay.Services.JsonStore.Options)!;
        Assert.Equal(PlayerRelationship.Neutral, plain.Relationship);
        Assert.Null(plain.PvpResponse);
    }

    [Fact]
    public void RecordLevel_SplitsFullName_KeysOnGiven()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordLevel("Bob Ironhelm", 33, now);

        PlayerRecord r = Assert.Single(db.Players);
        Assert.Equal("Bob", r.GivenName);
        Assert.Equal(33, r.Level);
    }

    [Fact]
    public void RecordLevel_NonPositive_NoOp()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        db.RecordLevel("Bob", 0, DateTime.UtcNow);
        db.RecordLevel("Bob", -3, DateTime.UtcNow);
        Assert.Empty(db.Players);
    }

    // ===== RecordVersion — client version from an @version probe reply =====

    [Fact]
    public void RecordVersion_CreatesRecord_WhenPlayerUnknown()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordVersion("Bob", "MudPlay 2.37.0", now);

        PlayerRecord r = Assert.Single(db.Players);
        Assert.Equal("Bob", r.GivenName);
        Assert.Equal("MudPlay 2.37.0", r.Version);
        Assert.Equal(now, r.VersionAt);
        Assert.Equal(now, r.LastSeenUtc);
    }

    [Fact]
    public void RecordVersion_UpdatesVersion_KeepingOtherFields()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime first = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime later = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordObservation("Bob", "Mage", "Elf", "Good", "Wizard", "Guild", null, first);
        db.RecordVersion("Bob", "MegaMud 1.03u", later);

        PlayerRecord r = Assert.Single(db.Players);
        Assert.Equal("MegaMud 1.03u", r.Version);
        Assert.Equal("Mage", r.Class);
        Assert.Equal("Wizard", r.Title);
        Assert.Equal(first, r.FirstSeenUtc);
        Assert.Equal(later, r.LastSeenUtc);
    }

    [Fact]
    public void RecordVersion_Blank_NoOp()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        db.RecordVersion("Bob", "", DateTime.UtcNow);
        db.RecordVersion("Bob", "   ", DateTime.UtcNow);
        Assert.Empty(db.Players);
    }

    // ===== Party-day tracking (first party of the local day) =====

    [Fact]
    public void RecordPartied_RoundTripsLastPartiedUtc()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.Null(db.GetLastPartiedUtc("Bob"));   // never partied
        db.RecordPartied("Bob Ironhelm", now);       // keys on given name
        Assert.Equal(now, db.GetLastPartiedUtc("Bob"));
    }

    [Fact]
    public void RecordPartied_LeavesOtherFieldsIntact()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime first = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime party = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordObservation("Bob", "Mage", null, null, "Wizard", null, null, first);
        db.RecordPartied("Bob", party);

        PlayerRecord r = Assert.Single(db.Players);
        Assert.Equal(party, r.LastPartiedUtc);
        Assert.Equal("Mage", r.Class);
        Assert.Equal("Wizard", r.Title);
        Assert.Equal(first, r.FirstSeenUtc);
    }

    [Fact]
    public void RecordObservation_AfterLevel_PreservesLevel()
    {
        // A later who-observation carries no level; the probed level must
        // survive (with-expression leaves untouched fields intact).
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime t1 = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime t2 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordLevel("Bob", 42, t1);
        db.RecordObservation("Bob Ironhelm", null, null, "Good", "Wizard", null, null, t2);

        PlayerRecord r = Assert.Single(db.Players);
        Assert.Equal(42, r.Level);
        Assert.Equal("Wizard", r.Title);
    }

    [Fact]
    public void Find_UnknownPlayer_ReturnsNull()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        Assert.Null(db.Find("Nobody"));
    }

    [Fact]
    public void Find_ByGivenOrFullName_ReturnsMergedRecord()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        db.RecordObservation("Bob Ironhelm", "Mage", "Elf", "Good", "Wizard", null, null, now);
        db.RecordLevel("Bob", 51, now);

        PlayerRecord? byGiven = db.Find("Bob");
        PlayerRecord? byFull  = db.Find("Bob Ironhelm");

        Assert.NotNull(byGiven);
        Assert.Equal(51, byGiven!.Level);
        Assert.Equal("Wizard", byGiven.Title);
        Assert.NotNull(byFull);
        Assert.Equal("Bob", byFull!.GivenName);   // full name reduced to the given key
    }

    [Fact]
    public void Find_BlankName_ReturnsNull()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        Assert.Null(db.Find(""));
        Assert.Null(db.Find("   "));
    }

    // ===== Load-time migration from legacy display-name keyed files =====

    // ===== Manual Add / Remove (PR B) =====

    [Fact]
    public void AddManual_CreatesNewRecord_WhenGivenIsUnknown()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        bool isNew = db.AddManual("Debbie", "Par", now);

        Assert.True(isNew);
        PlayerRecord r = Assert.Single(db.Players);
        Assert.Equal("Debbie", r.GivenName);
        Assert.Equal("Par",    r.FamilyName);
        Assert.Equal(now, r.FirstSeenUtc);
        Assert.Equal(now, r.LastSeenUtc);
    }

    [Fact]
    public void AddManual_MergesIntoExisting_WhenGivenAlreadyTracked()
    {
        // Defensive: an Add against a duplicate given collapses through
        // the same sparse-merge as RecordObservation. The dialog blocks
        // this from happening via its CanSave validation, but the DB
        // itself stays consistent under direct callers.
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime first = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime later = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        db.RecordObservation("Debbie Schwartz", "Mage", "Elf", "Good", null, null, null, first);
        bool isNew = db.AddManual("Debbie", "Par", later);

        Assert.False(isNew);
        Assert.Single(db.Players);
        Assert.Equal("Par",  db.Players[0].FamilyName);
        Assert.Equal("Mage", db.Players[0].Class);   // sparse-merge preserves
        Assert.Equal(first,  db.Players[0].FirstSeenUtc);
    }

    [Fact]
    public void AddManual_EmptyGiven_NoOp()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        Assert.False(db.AddManual("", "Par", DateTime.UtcNow));
        Assert.False(db.AddManual("   ", "", DateTime.UtcNow));
        Assert.Empty(db.Players);
    }

    [Fact]
    public void RemoveByGivenName_DropsTheRow()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        db.RecordObservation("Debbie Par", null, null, null, null, null, null, now);
        db.RecordObservation("Helper Lastname", null, null, null, null, null, null, now);

        bool removed = db.RemoveByGivenName("Debbie");

        Assert.True(removed);
        PlayerRecord r = Assert.Single(db.Players);
        Assert.Equal("Helper", r.GivenName);
    }

    [Fact]
    public void RemoveByGivenName_AcceptsFullDisplayName()
    {
        // Convenience for callers passing the full "Given Family" string —
        // the helper splits internally and matches on the given token.
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        db.RecordObservation("Debbie Par", null, null, null, null, null, null, now);

        Assert.True(db.RemoveByGivenName("Debbie Par"));
        Assert.Empty(db.Players);
    }

    [Fact]
    public void RemoveByGivenName_PreservesCustomization_OnProfile()
    {
        // Removing the observation drops the row from Players, but the
        // customization stays attached. On the NEXT observation of the
        // same player (real or manual), the customization re-binds.
        PlayerDatabase db = new(new ProfileService(), () => null);
        DateTime now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        db.RecordObservation("Debbie", null, null, null, null, null, null, now);
        db.EditCustomization("Debbie", new PlayerCustomization(
            RemoteControls: PlayerRemoteControls.QueryHealthStatus,
            Notes:          "trusted"));

        db.RemoveByGivenName("Debbie");
        Assert.Empty(db.Players);

        // Re-observe → customization snaps back into place.
        db.RecordObservation("Debbie", null, null, null, null, null, null, now.AddDays(1));
        PlayerRecord r = Assert.Single(db.Players);
        Assert.True(r.RemoteControls.HasFlag(PlayerRemoteControls.QueryHealthStatus));
        Assert.Equal("trusted", r.Notes);
    }

    [Fact]
    public void RemoveByGivenName_UnknownGiven_NoOp()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);
        Assert.False(db.RemoveByGivenName("NeverSeen"));
        Assert.False(db.RemoveByGivenName(""));
        Assert.False(db.RemoveByGivenName("   "));
    }
}
