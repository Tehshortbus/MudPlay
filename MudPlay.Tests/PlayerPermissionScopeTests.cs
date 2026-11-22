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

        Assert.Equal(PlayerPermissionScope.Realm, otherRealm.PermissionScope);
        Assert.Equal(Query, Held(sameRealm));
        Assert.Equal(PlayerRemoteControls.None, Held(otherRealm));
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
    public void WhatIsNotAPermission_StaysWithEachCharacter()
    {
        PlayerDatabase first = Client(RealmA);
        PlayerDatabase second = Client(RealmA);
        first.SetPermissionScope(PlayerPermissionScope.Realm);

        first.EditCustomization("Bob", first.Find("Bob")!.ToCustomization() with
        {
            RemoteControls = Query, Notes = "healer", JoinPartyIfInvited = true,
        });
        second.TakeInOutsideChanges();

        Assert.Equal(Query, Held(second));
        Assert.Null(second.Find("Bob")!.Notes);
        Assert.False(second.Find("Bob")!.JoinPartyIfInvited);
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
