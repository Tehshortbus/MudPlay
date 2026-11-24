using System.IO;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Tables;
using Xunit;

namespace MudPlay.Tests;

// Remote permissions shared across a realm or a BBS: each client here is a running
// character, reading the same files through its own PlayerDatabase.
public sealed class PlayerPermissionScopeTests : IDisposable
{
    private const PlayerRemoteControls Query = PlayerRemoteControls.QueryLocation;
    private const PlayerRemoteControls Move = PlayerRemoteControls.MovePlayer;
    private static readonly DateTime Now = new(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _bbs = Path.Combine(AppPaths.BbsDir, "test-scope-" + Guid.NewGuid().ToString("N")[..12]);
    private string RealmA => Path.Combine(_bbs, "Realms", "A");
    private string RealmB => Path.Combine(_bbs, "Realms", "B");

    public PlayerPermissionScopeTests()
    {
        Directory.CreateDirectory(RealmA);
        Directory.CreateDirectory(RealmB);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_bbs)) Directory.Delete(_bbs, recursive: true); }
        catch (IOException) { /* best-effort cleanup of a scratch folder */ }
    }

    private PlayerDatabase Client(string realm)
    {
        ScratchRoot.Require();
        ProfileService profile = TestProfiles.Service();
        PlayerDatabase db = new(profile, () => realm, () => _bbs);
        profile.LoadDefaultProfile();
        db.RecordObservation("Bob Smith", null, null, null, null, null, null, Now);
        return db;
    }

    private static void Grant(PlayerDatabase db, PlayerRemoteControls controls) =>
        db.EditCustomization("Bob", db.Find("Bob")!.ToCustomization() with { RemoteControls = controls });

    private static PlayerRemoteControls Held(PlayerDatabase db) => db.Find("Bob")!.RemoteControls;

    [Fact]
    public void PerCharacter_AGrantStaysWithTheCharacterThatMadeIt()
    {
        PlayerDatabase first = Client(RealmA);
        PlayerDatabase second = Client(RealmA);

        Grant(first, Query);
        second.TakeInOutsideChanges();

        Assert.Equal(Query, Held(first));
        Assert.Equal(PlayerRemoteControls.None, Held(second));
    }

    [Fact]
    public void RealmWide_ReachesTheRealmsCharacters_AndNoOtherRealm()
    {
        PlayerDatabase first = Client(RealmA);
        PlayerDatabase sameRealm = Client(RealmA);
        PlayerDatabase otherRealm = Client(RealmB);

        first.SetPermissionScope(PlayerPermissionScope.Realm);
        Grant(first, Query);
        sameRealm.TakeInOutsideChanges();
        otherRealm.TakeInOutsideChanges();

        Assert.Equal(PlayerPermissionScope.Realm, sameRealm.PermissionScope);
        Assert.Equal(PlayerPermissionScope.Character, otherRealm.PermissionScope);
        Assert.Equal(Query, Held(sameRealm));
        Assert.Equal(PlayerRemoteControls.None, Held(otherRealm));
    }

    private static void Follow(params PlayerDatabase[] clients)
    {
        foreach (PlayerDatabase client in clients) client.TakeInOutsideChanges();
    }

    private static void AssertScopes(PlayerPermissionScope a1, PlayerPermissionScope a2, PlayerPermissionScope b1,
        PlayerDatabase first, PlayerDatabase second, PlayerDatabase third)
    {
        Follow(first, second, third);
        Assert.Equal(a1, first.PermissionScope);
        Assert.Equal(a2, second.PermissionScope);
        Assert.Equal(b1, third.PermissionScope);
    }

    [Fact]
    public void EachPick_ReachesOnlyTheCharactersItNames()
    {
        PlayerDatabase a1 = Client(RealmA);
        PlayerDatabase a2 = Client(RealmA);
        PlayerDatabase b1 = Client(RealmB);
        const PlayerPermissionScope Own = PlayerPermissionScope.Character;
        const PlayerPermissionScope Realm = PlayerPermissionScope.Realm;
        const PlayerPermissionScope Bbs = PlayerPermissionScope.Bbs;

        a1.SetPermissionScope(Realm);
        AssertScopes(Realm, Realm, Own, a1, a2, b1);

        a2.SetPermissionScope(Own);
        AssertScopes(Realm, Own, Own, a1, a2, b1);

        b1.SetPermissionScope(Bbs);
        AssertScopes(Bbs, Bbs, Bbs, a1, a2, b1);

        a1.SetPermissionScope(Realm);
        AssertScopes(Realm, Realm, Bbs, a1, a2, b1);

        b1.SetPermissionScope(Own);
        AssertScopes(Realm, Realm, Own, a1, a2, b1);
    }

    private string BbsName => Path.GetFileName(_bbs);

    private (PlayerDatabase Db, ProfileService Profile) Named(string realm, string name, string? bbsName = null)
    {
        ScratchRoot.Require();
        string bbs = bbsName ?? BbsName;
        ProfileService profile = TestProfiles.Service();
        PlayerDatabase db = new(profile, () => realm, () => AppPaths.BbsFolder(bbs));
        if (profile.Exists(bbs, name)) profile.Load(bbs, name);
        else
        {
            profile.LoadDefaultProfile();
            profile.SaveAs(bbs, name);
        }
        db.RecordObservation("Bob Smith", null, null, null, null, null, null, Now);
        return (db, profile);
    }

    [Fact]
    public void ACharactersOwnPick_SurvivesARestart()
    {
        PlayerDatabase other = Client(RealmA);
        other.SetPermissionScope(PlayerPermissionScope.Realm);
        (PlayerDatabase own, ProfileService ownProfile) = Named(RealmA, "Own");
        own.SetPermissionScope(PlayerPermissionScope.Character);
        ownProfile.Close();

        (PlayerDatabase restarted, _) = Named(RealmA, "Own");

        Assert.Equal(PlayerPermissionScope.Character, restarted.PermissionScope);
    }

    [Theory]
    [InlineData(PlayerPermissionScope.Realm)]
    [InlineData(PlayerPermissionScope.Bbs)]
    public void AWiderPick_ReachesACharacterThatWasNotRunning(PlayerPermissionScope wider)
    {
        PlayerDatabase picker = Client(RealmA);
        picker.SetPermissionScope(PlayerPermissionScope.Realm);
        (PlayerDatabase own, ProfileService ownProfile) = Named(RealmA, "Own");
        own.SetPermissionScope(PlayerPermissionScope.Character);
        ownProfile.Close();

        picker.SetPermissionScope(PlayerPermissionScope.Character);
        picker.SetPermissionScope(wider);
        (PlayerDatabase restarted, _) = Named(RealmA, "Own");

        Assert.Equal(wider, restarted.PermissionScope);
    }

    [Fact]
    public void AFirstUsedRealmList_StartsFromThePickersTable_NotEmpty()
    {
        PlayerDatabase a = Client(RealmA);
        a.SetPermissionScope(PlayerPermissionScope.Realm);
        Grant(a, Move);
        PlayerDatabase b = Client(RealmB);
        Grant(b, Query);

        b.SetPermissionScope(PlayerPermissionScope.Realm);

        Assert.Equal(Query, Held(b));
        a.TakeInOutsideChanges();
        Assert.Equal(Move, Held(a));
    }

    [Fact]
    public void ACopiedProfile_FollowsTheRealm_NotTheSourcesOwnPick()
    {
        PlayerDatabase other = Client(RealmA);
        other.SetPermissionScope(PlayerPermissionScope.Realm);
        (PlayerDatabase own, ProfileService ownProfile) = Named(RealmA, "Own");
        own.SetPermissionScope(PlayerPermissionScope.Character);

        ownProfile.CopyProfile(BbsName, "Own", "Copy");
        (PlayerDatabase copy, _) = Named(RealmA, "Copy");

        Assert.Equal(PlayerPermissionScope.Realm, copy.PermissionScope);
    }

    [Fact]
    public void AProfileMovedToAnotherBbs_FollowsThatBbs()
    {
        string otherName = BbsName + "-other";
        string otherBbs = AppPaths.BbsFolder(otherName);
        string otherRealm = Path.Combine(otherBbs, "Realms", "A");
        try
        {
            (PlayerDatabase there, _) = Named(otherRealm, "There", otherName);
            there.SetPermissionScope(PlayerPermissionScope.Bbs);
            Client(RealmA).SetPermissionScope(PlayerPermissionScope.Realm);
            (PlayerDatabase own, ProfileService ownProfile) = Named(RealmA, "Own");
            own.SetPermissionScope(PlayerPermissionScope.Character);
            ownProfile.Close();

            TestProfiles.Service().MoveProfile(BbsName, "Own", otherName, "Own");
            (PlayerDatabase moved, _) = Named(otherRealm, "Own", otherName);

            Assert.Equal(PlayerPermissionScope.Bbs, moved.PermissionScope);
        }
        finally
        {
            if (Directory.Exists(otherBbs)) Directory.Delete(otherBbs, recursive: true);
        }
    }

    [Fact]
    public void AnOlderBbsWideChoice_StillAppliesToEveryone_UntilSomeoneChooses()
    {
        File.WriteAllText(AppPaths.PlayerPermissionsFile(_bbs),
            """{ "Scope": 2, "Players": { "Bob": { "RemoteControls": 8 } } }""");
        PlayerDatabase a = Client(RealmA);
        PlayerDatabase b = Client(RealmB);
        Assert.Equal(PlayerPermissionScope.Bbs, a.PermissionScope);
        Assert.Equal(PlayerPermissionScope.Bbs, b.PermissionScope);

        a.SetPermissionScope(PlayerPermissionScope.Character);
        b.TakeInOutsideChanges();

        Assert.Equal(PlayerPermissionScope.Character, a.PermissionScope);
        Assert.Equal(PlayerPermissionScope.Bbs, b.PermissionScope);
    }

    [Fact]
    public void AnOlderRealmWideChoice_LeavesARealmWithNoListOnItsOwn()
    {
        File.WriteAllText(AppPaths.PlayerPermissionsFile(_bbs), """{ "Scope": 1 }""");
        File.WriteAllText(AppPaths.PlayerPermissionsFile(RealmA),
            """{ "Players": { "Bob": { "RemoteControls": 8 } } }""");

        PlayerDatabase withList = Client(RealmA);
        PlayerDatabase withoutList = Client(RealmB);

        Assert.Equal(PlayerPermissionScope.Realm, withList.PermissionScope);
        Assert.Equal(PlayerPermissionScope.Character, withoutList.PermissionScope);
    }

    [Fact]
    public void ANewPick_OutranksAnOlderRealmChoice_OnEveryRealm()
    {
        File.WriteAllText(AppPaths.PlayerPermissionsFile(_bbs), """{ "Scope": 1 }""");
        File.WriteAllText(AppPaths.PlayerPermissionsFile(RealmA),
            """{ "Players": { "Bob": { "RemoteControls": 8 } } }""");
        PlayerDatabase withList = Client(RealmA);
        PlayerDatabase withoutList = Client(RealmB);

        withoutList.SetPermissionScope(PlayerPermissionScope.Bbs);
        withList.TakeInOutsideChanges();

        Assert.Equal(PlayerPermissionScope.Bbs, withList.PermissionScope);
    }

    [Fact]
    public void BbsWide_ReachesEveryRealm_AndARevokeReachesThemToo()
    {
        PlayerDatabase first = Client(RealmA);
        PlayerDatabase otherRealm = Client(RealmB);

        first.SetPermissionScope(PlayerPermissionScope.Bbs);
        Grant(first, Query | Move);
        otherRealm.TakeInOutsideChanges();
        Assert.Equal(Query | Move, Held(otherRealm));

        Grant(first, Query);
        otherRealm.TakeInOutsideChanges();
        Assert.Equal(Query, Held(otherRealm));
    }

    [Fact]
    public void Widening_StartsFromTheTable_AndTheCharactersOwnComeBack()
    {
        PlayerDatabase first = Client(RealmA);
        Grant(first, Query);

        first.SetPermissionScope(PlayerPermissionScope.Bbs);
        Assert.Equal(Query, Held(first));
        Grant(first, Query | Move);

        first.SetPermissionScope(PlayerPermissionScope.Character);
        Assert.Equal(Query, Held(first));
    }

    [Fact]
    public void AListUsedBefore_KeepsItsRevoke_WhenTheScopeComesBack()
    {
        PlayerDatabase first = Client(RealmA);
        Grant(first, Query);
        first.SetPermissionScope(PlayerPermissionScope.Realm);
        Grant(first, PlayerRemoteControls.None);

        first.SetPermissionScope(PlayerPermissionScope.Character);
        Assert.Equal(Query, Held(first));
        first.SetPermissionScope(PlayerPermissionScope.Realm);

        Assert.Equal(PlayerRemoteControls.None, Held(first));
    }

    [Fact]
    public void WideningFromARealm_CarriesTheRealmsList_ToTheBbs()
    {
        PlayerDatabase first = Client(RealmA);
        first.SetPermissionScope(PlayerPermissionScope.Realm);
        Grant(first, Move);

        first.SetPermissionScope(PlayerPermissionScope.Bbs);

        Assert.Equal(Move, Held(first));
    }

    [Fact]
    public void TheRealmAndBbsLists_AreKeptApart()
    {
        PlayerDatabase first = Client(RealmA);
        first.SetPermissionScope(PlayerPermissionScope.Bbs);
        Grant(first, Move);
        first.SetPermissionScope(PlayerPermissionScope.Realm);
        Grant(first, Query);

        first.SetPermissionScope(PlayerPermissionScope.Bbs);
        Assert.Equal(Move, Held(first));
        first.SetPermissionScope(PlayerPermissionScope.Realm);
        Assert.Equal(Query, Held(first));
    }

    [Fact]
    public void ASpentDupe_IsSpentForEveryCharacterSharingThePermissions()
    {
        PlayerDatabase first = Client(RealmA);
        PlayerDatabase second = Client(RealmB);
        first.SetPermissionScope(PlayerPermissionScope.Bbs);
        Grant(first, PlayerRemoteControls.SysopCommands);

        first.EditCustomization("Bob", first.Find("Bob")!.ToCustomization() with { DupeUsedAtUtc = Now, DupedPlayer = "Alt" });
        second.TakeInOutsideChanges();

        Assert.Equal(Now, second.Find("Bob")!.DupeUsedAtUtc);
        Assert.Equal("Alt", second.Find("Bob")!.DupedPlayer);
    }

    [Fact]
    public void DontDelete_SetOnOneCharacter_KeepsThePlayerOnAnother()
    {
        PlayerDatabase first = Client(RealmA);
        PlayerDatabase otherRealm = Client(RealmB);
        first.SetPermissionScope(PlayerPermissionScope.Bbs);

        first.EditCustomization("Bob", first.Find("Bob")!.ToCustomization() with { DontAutoDelete = true });
        otherRealm.TakeInOutsideChanges();

        Assert.True(otherRealm.Find("Bob")!.DontAutoDelete);
        Assert.Equal(0, otherRealm.PurgeStale(1, Now.AddDays(10)));
        Assert.NotNull(otherRealm.Find("Bob"));
    }

    [Fact]
    public void DontDelete_FollowsTheScope_AndTheCharactersOwnComesBack()
    {
        PlayerDatabase first = Client(RealmA);
        first.EditCustomization("Bob", first.Find("Bob")!.ToCustomization() with { DontAutoDelete = true });

        first.SetPermissionScope(PlayerPermissionScope.Realm);
        Assert.True(first.Find("Bob")!.DontAutoDelete);
        first.EditCustomization("Bob", first.Find("Bob")!.ToCustomization() with { DontAutoDelete = false });
        Assert.False(first.Find("Bob")!.DontAutoDelete);
        Assert.Equal(1, first.PurgeStale(1, Now.AddDays(10)));

        first.RecordObservation("Bob Smith", null, null, null, null, null, null, Now);
        first.SetPermissionScope(PlayerPermissionScope.Character);
        Assert.True(first.Find("Bob")!.DontAutoDelete);
    }

    [Fact]
    public void ThePartyToggles_ReachTheCharactersSharing_AndTheCharactersOwnComeBack()
    {
        PlayerDatabase first = Client(RealmA);
        PlayerDatabase otherRealm = Client(RealmB);
        first.EditCustomization("Bob", first.Find("Bob")!.ToCustomization() with { JoinPartyIfInvited = true });

        first.SetPermissionScope(PlayerPermissionScope.Bbs);
        Assert.True(first.Find("Bob")!.JoinPartyIfInvited);
        first.EditCustomization("Bob", first.Find("Bob")!.ToCustomization() with
        {
            InviteToPartyIfSeen = true, JoinPartyIfInvited = false,
        });
        otherRealm.TakeInOutsideChanges();
        Assert.True(otherRealm.Find("Bob")!.InviteToPartyIfSeen);
        Assert.False(otherRealm.Find("Bob")!.JoinPartyIfInvited);

        first.SetPermissionScope(PlayerPermissionScope.Character);
        Assert.False(first.Find("Bob")!.InviteToPartyIfSeen);
        Assert.True(first.Find("Bob")!.JoinPartyIfInvited);
    }

    [Fact]
    public void AnInviteToggleAlone_IsEnoughToKeepAPlayerOnTheSharedList()
    {
        PlayerDatabase first = Client(RealmA);
        first.SetPermissionScope(PlayerPermissionScope.Realm);
        first.EditCustomization("Bob", first.Find("Bob")!.ToCustomization() with { InviteToPartyIfSeen = true });

        PlayerDatabase restarted = Client(RealmA);

        Assert.True(restarted.Find("Bob")!.InviteToPartyIfSeen);
    }

    [Fact]
    public void AJoinToggleAlone_IsEnoughToKeepAPlayerOnTheSharedList()
    {
        PlayerDatabase first = Client(RealmA);
        first.SetPermissionScope(PlayerPermissionScope.Realm);
        first.EditCustomization("Bob", first.Find("Bob")!.ToCustomization() with { JoinPartyIfInvited = true });

        PlayerDatabase restarted = Client(RealmA);

        Assert.True(restarted.Find("Bob")!.JoinPartyIfInvited);
    }

    [Fact]
    public void Notes_StayWithEachCharacter()
    {
        PlayerDatabase first = Client(RealmA);
        PlayerDatabase second = Client(RealmA);
        first.SetPermissionScope(PlayerPermissionScope.Realm);

        first.EditCustomization("Bob", first.Find("Bob")!.ToCustomization() with
        {
            RemoteControls = Query, Notes = "healer",
        });
        second.TakeInOutsideChanges();

        Assert.Equal(Query, Held(second));
        Assert.Equal("healer", first.Find("Bob")!.Notes);
        Assert.Null(second.Find("Bob")!.Notes);
    }

    [Fact]
    public void AFileWithAnUnknownScopeOrPermission_GrantsNothingExtra()
    {
        File.WriteAllText(AppPaths.PlayerPermissionsFile(_bbs),
            """{ "Scope": 7, "Players": { "Bob": { "RemoteControls": 1048576 } } }""");

        PlayerDatabase client = Client(RealmA);

        Assert.Equal(PlayerPermissionScope.Character, client.PermissionScope);
        client.SetPermissionScope(PlayerPermissionScope.Bbs);
        Assert.Equal(PlayerRemoteControls.None, Held(client));
    }

    [Fact]
    public void WithNoBbsResolved_ThereIsNothingToShareAcross()
    {
        PlayerDatabase db = new(new ProfileService(), () => null);

        db.SetPermissionScope(PlayerPermissionScope.Bbs);

        Assert.False(db.CanSharePermissions);
        Assert.Equal(PlayerPermissionScope.Character, db.PermissionScope);
    }

    [Fact]
    public void ThePlayersTab_FollowsAScopeChosenByAnotherClient()
    {
        PlayerDatabase first = Client(RealmA);
        PlayerDatabase second = Client(RealmB);
        using PlayersSectionViewModel tab = new(second);
        List<string?> raised = new();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        first.SetPermissionScope(PlayerPermissionScope.Bbs);
        second.TakeInOutsideChanges();

        Assert.Equal((int)PlayerPermissionScope.Bbs, tab.PermissionScopeIndex);
        Assert.Contains(nameof(PlayersSectionViewModel.PermissionScopeIndex), raised);
    }

    [Fact]
    public async Task ThePlayersTab_AppliesTheChosenScope()
    {
        PlayerDatabase client = Client(RealmA);
        using PlayersSectionViewModel tab = new(client);

        await tab.ChoosePermissionScopeAsync((int)PlayerPermissionScope.Realm);

        Assert.Equal(PlayerPermissionScope.Realm, client.PermissionScope);
    }
}
