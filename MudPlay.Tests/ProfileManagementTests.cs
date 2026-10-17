using System.IO;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Disk-touching tests for the Profile Management primitives (CreateProfile /
// DeleteProfile / MoveProfile). Mirrors BbsRenameCascadeTests: writes under
// uniquely-tagged throwaway BBS folders in the real data root and removes them
// in Dispose. Deliberately does NOT exercise RecentProfileList.RekeyBbs — that
// path ends in SettingsService.Save(), which would overwrite the real
// global.json; its ref-rewriting is trivial and left to the smoke test.
public sealed class ProfileManagementTests : IDisposable
{
    private readonly string _bbsA;
    private readonly string _bbsB;

    public ProfileManagementTests()
    {
        string tag = Path.GetRandomFileName();
        _bbsA = "profmgr-test-a-" + tag;
        _bbsB = "profmgr-test-b-" + tag;
        new BbsProfileStore().Save(new BbsProfile { Name = _bbsA, Host = "example.org", Port = 23 });
        new BbsProfileStore().Save(new BbsProfile { Name = _bbsB, Host = "example.org", Port = 23 });
    }

    public void Dispose()
    {
        foreach (string bbs in new[] { _bbsA, _bbsB })
        {
            try
            {
                string folder = AppPaths.BbsFolder(bbs);
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void CreateProfile_WritesFile_LeavesCurrentNull_ThrowsOnClashAndBlank()
    {
        var svc = TestProfiles.Service();
        svc.CreateProfile(_bbsA, "Alice");

        Assert.True(File.Exists(AppPaths.CharacterProfileFile(_bbsA, "Alice")));
        Assert.Null(svc.Current);   // create does NOT load the new profile

        Assert.Throws<IOException>(() => svc.CreateProfile(_bbsA, "Alice"));   // clash
        Assert.Throws<System.ArgumentException>(() => svc.CreateProfile(_bbsA, "  "));
    }

    [Fact]
    public void DeleteProfile_NonCurrent_RemovesFolder_KeepsCurrent()
    {
        var svc = TestProfiles.Service();
        svc.CreateProfile(_bbsA, "Keep");
        svc.CreateProfile(_bbsA, "Doomed");
        svc.Load(_bbsA, "Keep");

        svc.DeleteProfile(_bbsA, "Doomed");

        Assert.False(File.Exists(AppPaths.CharacterProfileFile(_bbsA, "Doomed")));
        Assert.Equal("Keep", svc.CurrentProfileName);   // loaded profile untouched
        Assert.True(File.Exists(AppPaths.CharacterProfileFile(_bbsA, "Keep")));
    }

    [Fact]
    public void DeleteProfile_Current_FallsBackToDefault_WithoutResurrecting()
    {
        var svc = TestProfiles.Service();
        svc.CreateProfile(_bbsA, "Solo");
        svc.Load(_bbsA, "Solo");

        bool closed = false, loaded = false;
        svc.ProfileClosed += () => closed = true;
        svc.ProfileLoaded += _ => loaded = true;

        svc.DeleteProfile(_bbsA, "Solo");

        Assert.True(closed);                 // the loaded profile was closed first
        Assert.True(loaded);                 // then the default profile loaded in its place
        Assert.NotNull(svc.Current);         // session always has a live Current
        Assert.Null(svc.CurrentProfileName); // the default profile is unnamed
        Assert.False(File.Exists(AppPaths.CharacterProfileFile(_bbsA, "Solo"))); // not resurrected by an outgoing save
    }

    [Fact]
    public void CopyProfile_DuplicatesFolder_RewritesName_KeepsSource()
    {
        var svc = TestProfiles.Service();
        svc.CreateProfile(_bbsA, "Main");
        File.WriteAllText(Path.Combine(AppPaths.ProfileFolder(_bbsA, "Main"), "items_overrides.set.json"), "{}");
        File.WriteAllText(AppPaths.CharacterProfileFile(_bbsA, "Main") + ".bak", "{}");

        svc.CopyProfile(_bbsA, "Main", "Alt");

        Assert.True(File.Exists(AppPaths.CharacterProfileFile(_bbsA, "Main")));
        CharacterProfile copy = JsonStore.Load<CharacterProfile>(AppPaths.CharacterProfileFile(_bbsA, "Alt"))!;
        Assert.Equal("Alt", copy.Name);
        Assert.True(File.Exists(Path.Combine(AppPaths.ProfileFolder(_bbsA, "Alt"), "items_overrides.set.json")));
        Assert.False(File.Exists(AppPaths.CharacterProfileFile(_bbsA, "Alt") + ".bak"));
        Assert.Throws<IOException>(() => svc.CopyProfile(_bbsA, "Main", "Alt"));   // clash
    }

    // The copy is for a different character: what the source read off the game stays
    // behind, the settings come along.
    [Fact]
    public void CopyProfile_DropsReadState_MarksCopyUnverified()
    {
        var svc = TestProfiles.Service();
        svc.CreateProfile(_bbsA, "Priest60");
        string sourcePath = AppPaths.CharacterProfileFile(_bbsA, "Priest60");
        CharacterProfile source = JsonStore.Load<CharacterProfile>(sourcePath)!;
        source.LastKnownStats = new LastKnownStats { Level = 60, MaxHits = 480 };
        source.DefaultPoolBaseline = new DefaultPoolBaseline { MaxHp = 480, MaxMa = 300, Level = 60 };
        source.LearnedSpells = new List<string> { "minor healing" };
        source.DeathHistory = new List<DeathRecord> { new() };
        source.HeldAtDisconnect = new HeldAtDisconnect { Items = { new HeldItem("silver chalice", 1) } };
        source.StashedCopper = new Dictionary<string, long> { ["1/100"] = 5000 };
        source.GotoHistory = new List<string> { "silvermere bank" };
        source.Equipment = new EquipmentSettings { SwapToDefaultBeforeLairs = true };
        source.Equipment.Sets.Add(new EquipmentSet { Name = "Default" });
        JsonStore.Save(sourcePath, source);

        svc.CopyProfile(_bbsA, "Priest60", "Priest40");

        CharacterProfile copy = JsonStore.Load<CharacterProfile>(AppPaths.CharacterProfileFile(_bbsA, "Priest40"))!;
        Assert.Null(copy.LastKnownStats);
        Assert.Null(copy.DefaultPoolBaseline);
        Assert.True(copy.StateUnverified);
        Assert.Empty(copy.Equipment!.Sets);
        Assert.True(copy.Equipment.SwapToDefaultBeforeLairs);
        Assert.Null(copy.LearnedSpells);
        Assert.Null(copy.DeathHistory);
        Assert.Null(copy.HeldAtDisconnect);
        Assert.Null(copy.StashedCopper);
        Assert.Single(copy.GotoHistory!);
        CharacterProfile kept = JsonStore.Load<CharacterProfile>(sourcePath)!;
        Assert.Equal(480, kept.DefaultPoolBaseline!.MaxHp);
        Assert.False(kept.StateUnverified);
        Assert.Single(kept.Equipment!.Sets);
        Assert.Single(kept.DeathHistory!);
        Assert.Equal(5000, kept.StashedCopper!["1/100"]);
    }

    [Fact]
    public void LoadDefaultProfile_OnBbs_PinsTheDraft_WithoutNamingIt()
    {
        var svc = TestProfiles.Service();

        svc.LoadDefaultProfile(_bbsB);

        Assert.Equal(_bbsB, svc.CurrentBbsName);
        Assert.Null(svc.CurrentProfileName);
    }

    [Fact]
    public void MoveProfile_SameBbsRename_NonCurrent_MovesFolder_RewritesName()
    {
        var svc = TestProfiles.Service();
        svc.CreateProfile(_bbsA, "OldName");

        svc.MoveProfile(_bbsA, "OldName", _bbsA, "NewName");

        Assert.False(File.Exists(AppPaths.CharacterProfileFile(_bbsA, "OldName")));
        CharacterProfile moved = JsonStore.Load<CharacterProfile>(AppPaths.CharacterProfileFile(_bbsA, "NewName"))!;
        Assert.Equal("NewName", moved.Name);
    }

    [Fact]
    public void MoveProfile_DifferentBbs_Assign_MovesFolder()
    {
        var svc = TestProfiles.Service();
        svc.CreateProfile(_bbsA, "Wanderer");

        svc.MoveProfile(_bbsA, "Wanderer", _bbsB, "Wanderer");

        Assert.False(File.Exists(AppPaths.CharacterProfileFile(_bbsA, "Wanderer")));
        Assert.True(File.Exists(AppPaths.CharacterProfileFile(_bbsB, "Wanderer")));
    }

    [Fact]
    public void MoveProfile_Current_ReHomes_UpdatesLoadedIdentity()
    {
        var svc = TestProfiles.Service();
        svc.CreateProfile(_bbsA, "Live");
        svc.Load(_bbsA, "Live");

        svc.MoveProfile(_bbsA, "Live", _bbsB, "Live");

        Assert.Equal(_bbsB, svc.CurrentBbsName);
        Assert.Equal("Live", svc.CurrentProfileName);
        Assert.True(File.Exists(AppPaths.CharacterProfileFile(_bbsB, "Live")));
        Assert.False(File.Exists(AppPaths.CharacterProfileFile(_bbsA, "Live")));
    }

    [Fact]
    public void MoveProfile_ThrowsOnDestinationClash()
    {
        var svc = TestProfiles.Service();
        svc.CreateProfile(_bbsA, "One");
        svc.CreateProfile(_bbsB, "One");

        Assert.Throws<IOException>(() => svc.MoveProfile(_bbsA, "One", _bbsB, "One"));
    }

    // ----- ProfileRenamed: the loaded profile known by another pair ----------

    [Fact]
    public void ProfileRenamed_TellsThePairTheLoadedProfileHad_WhenItIsRenamedOrMoved()
    {
        var svc = TestProfiles.Service();
        svc.CreateProfile(_bbsA, "first");
        svc.Load(_bbsA, "first");
        var had = new List<(string? Bbs, string? Name)>();
        svc.ProfileRenamed += (bbs, name) => had.Add((bbs, name));

        svc.MoveProfile(_bbsA, "first", _bbsA, "second");
        svc.MoveProfile(_bbsA, "second", _bbsB, "second");

        Assert.Equal(new (string?, string?)[] { (_bbsA, "first"), (_bbsA, "second") }, had);
        Assert.Equal(_bbsB, svc.CurrentBbsName);
        Assert.Equal("second", svc.CurrentProfileName);
    }

    [Fact]
    public void ProfileRenamed_Tells_WhenTheDefaultProfileIsGivenAName()
    {
        var svc = TestProfiles.Service();
        svc.LoadDefaultProfile();
        var had = new List<(string? Bbs, string? Name)>();
        svc.ProfileRenamed += (bbs, name) => had.Add((bbs, name));

        svc.SaveAs(_bbsA, "named");

        (string? bbs, string? name) = Assert.Single(had);
        Assert.Null(name);
        Assert.Null(bbs);
        Assert.Equal("named", svc.CurrentProfileName);
    }

    [Fact]
    public void ProfileRenamed_IsNotForALoad_ASaveUnderTheSameName_OrAProfileThatIsNotLoaded()
    {
        var svc = TestProfiles.Service();
        svc.CreateProfile(_bbsA, "loaded");
        svc.CreateProfile(_bbsA, "other");
        int told = 0;
        svc.ProfileRenamed += (_, _) => told++;

        svc.Load(_bbsA, "loaded");
        svc.SaveAs(_bbsA, "loaded");
        svc.MoveProfile(_bbsA, "other", _bbsB, "other");
        svc.Load(_bbsB, "other");

        Assert.Equal(0, told);
    }

    // What tells a copy from a rename, for whoever keeps things under a profile's
    // name: after Save As from a named profile the one it was is still there.
    [Fact]
    public void SaveAsFromANamedProfile_LeavesThatProfileInPlace_WhereARenameDoesNot()
    {
        var svc = TestProfiles.Service();
        svc.CreateProfile(_bbsA, "original");
        svc.Load(_bbsA, "original");
        var copies = new List<bool>();
        svc.ProfileRenamed += (bbs, name) => copies.Add(svc.LoadedIsACopyOf(bbs, name));

        svc.SaveAs(_bbsA, "copy");
        svc.MoveProfile(_bbsA, "copy", _bbsA, "renamed");

        Assert.Equal(new[] { true, false }, copies);
    }

    // A rename that only changes capitals moves the folder wherever folders are told
    // apart by them, so it is told like any other rename, and is not a copy.
    [Fact]
    public void ARenameThatOnlyChangesCapitals_IsToldAsARename()
    {
        // Where they aren't, the destination "already exists" and there is no such rename.
        if (Directory.Exists(AppPaths.BbsFolder(_bbsA.ToUpperInvariant()))) return;
        var svc = TestProfiles.Service();
        svc.CreateProfile(_bbsA, "walker");
        svc.Load(_bbsA, "walker");
        var told = new List<(string? Name, bool Copy)>();
        svc.ProfileRenamed += (bbs, name) => told.Add((name, svc.LoadedIsACopyOf(bbs, name)));

        svc.MoveProfile(_bbsA, "walker", _bbsA, "Walker");

        Assert.Equal(new[] { ((string?)"walker", false) }, told);
        Assert.Equal("Walker", svc.CurrentProfileName);
    }

    // ----- Profiles that are not loaded, and ones that are gone ----------

    [Fact]
    public void AStoredProfileMovedOrRenamed_TellsWhereItWasAndIs_AndTheLoadedOneOnlyThroughProfileRenamed()
    {
        var svc = TestProfiles.Service();
        svc.CreateProfile(_bbsA, "loaded");
        svc.CreateProfile(_bbsA, "stored");
        svc.Load(_bbsA, "loaded");
        var moved = new List<(ProfileRef Was, ProfileRef Now)>();
        svc.StoredProfileMoved += (was, now) => moved.Add((was, now));

        svc.MoveProfile(_bbsA, "stored", _bbsA, "renamed");
        svc.MoveProfile(_bbsA, "renamed", _bbsB, "renamed");
        svc.MoveProfile(_bbsA, "loaded", _bbsB, "loaded");

        Assert.Equal(new[]
        {
            (new ProfileRef(_bbsA, "stored"), new ProfileRef(_bbsA, "renamed")),
            (new ProfileRef(_bbsA, "renamed"), new ProfileRef(_bbsB, "renamed")),
        }, moved);
    }

    [Fact]
    public void ADeletedProfile_LoadedOrNot_TellsWhichItWas_AndACopyTellsNothing()
    {
        var svc = TestProfiles.Service();
        svc.CreateProfile(_bbsA, "loaded");
        svc.CreateProfile(_bbsA, "stored");
        svc.Load(_bbsA, "loaded");
        var deleted = new List<ProfileRef>();
        int others = 0;
        svc.ProfileDeleted += deleted.Add;
        svc.StoredProfileMoved += (_, _) => others++;
        svc.ProfileRenamed += (_, _) => others++;

        svc.CopyProfile(_bbsA, "stored", "copy");
        Assert.Empty(deleted);

        svc.DeleteProfile(_bbsA, "stored");
        svc.DeleteProfile(_bbsA, "loaded");

        Assert.Equal(new[] { new ProfileRef(_bbsA, "stored"), new ProfileRef(_bbsA, "loaded") }, deleted);
        Assert.Equal(0, others);
    }

    [Fact]
    public void ARenamedBbs_TellsOfTheLoadedProfileFirst_ThenOfTheBbs_AndARemovedOneIsToldWhenNotified()
    {
        var store = new BbsProfileStore();
        var svc = TestProfiles.Service();
        svc.CreateProfile(_bbsA, "loaded");
        svc.CreateProfile(_bbsA, "stored");
        svc.Load(_bbsA, "loaded");
        string renamed = _bbsA + "-renamed";
        var told = new List<string>();
        svc.ProfileRenamed += (bbs, name) => told.Add($"profile {bbs}/{name} -> {svc.CurrentBbsName}/{svc.CurrentProfileName}");
        svc.BbsRenamed += (was, now) => told.Add($"bbs {was} -> {now}");
        svc.BbsDeleted += bbs => told.Add($"bbs {bbs} deleted");

        store.Rename(_bbsA, renamed);
        svc.RenameBbs(_bbsA, renamed);
        svc.Close();
        store.Delete(renamed);
        svc.NotifyBbsDeleted(renamed);

        Assert.Equal(new[]
        {
            $"profile {_bbsA}/loaded -> {renamed}/loaded",
            $"bbs {_bbsA} -> {renamed}",
            $"bbs {renamed} deleted",
        }, told);
    }
}
