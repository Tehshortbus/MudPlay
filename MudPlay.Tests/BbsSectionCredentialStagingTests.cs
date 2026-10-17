using System;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.ViewModels.Settings;
using Xunit;

namespace MudPlay.Tests;

// Pins the multi-board commit contract on the Settings BBS tab: a credential edit
// made on one board has to survive clicking to another and still land on Apply.
// The tab used to commit only whichever board was selected when OK was pressed,
// so the first board's username / password / logon steps were dropped silently
// while its host and port — staged per board — were kept.
//
// The two BBS records are saved under the scratch data root and deleted again in a
// finally. The character is the unnamed default-profile draft, which keeps credentials in memory
// and makes Save a no-op, so no character profile is touched on disk.
[Collection(BbsSectionCollection.Name)]
public sealed class BbsSectionCredentialStagingTests
{
    [Fact]
    public void EditsOnTwoBoards_BothCommitOnApply()
    {
        Fixture f = Fixture.Create();
        try
        {
            f.Vm.SelectedBbsName = f.BbsA;
            f.Vm.Username = "alpha";
            f.Vm.SelectedBbsName = f.BbsB;   // the switch that used to discard "alpha"
            f.Vm.Username = "beta";

            f.Vm.Apply();

            Assert.Equal("alpha", f.CommittedUsername(f.BbsA));
            Assert.Equal("beta", f.CommittedUsername(f.BbsB));
        }
        finally { f.Dispose(); }
    }

    // Coming back to a board mid-edit has to show what's staged, not what's on the
    // profile. Reloading from the profile would blank the pending username AND
    // re-stage that blank over the good copy on the next switch — a second way to
    // lose the edit, reachable by nothing more than clicking A → B → A.
    [Fact]
    public void ReturningToAnEditedBoard_ShowsTheStagedValue()
    {
        Fixture f = Fixture.Create();
        try
        {
            f.Vm.SelectedBbsName = f.BbsA;
            f.Vm.Username = "alpha";
            f.Vm.SelectedBbsName = f.BbsB;
            f.Vm.Username = "beta";

            f.Vm.SelectedBbsName = f.BbsA;
            Assert.Equal("alpha", f.Vm.Username);

            f.Vm.Apply();
            Assert.Equal("alpha", f.CommittedUsername(f.BbsA));
            Assert.Equal("beta", f.CommittedUsername(f.BbsB));
        }
        finally { f.Dispose(); }
    }

    // Cancel covers every board touched during the visit, not just the visible one.
    [Fact]
    public void Discard_DropsEditsOnBoardsNoLongerSelected()
    {
        Fixture f = Fixture.Create();
        try
        {
            f.Vm.SelectedBbsName = f.BbsA;
            f.Vm.Username = "alpha";
            f.Vm.SelectedBbsName = f.BbsB;

            f.Vm.Discard();
            f.Vm.Apply();   // a later OK must not resurrect the discarded edit

            Assert.Null(f.CommittedUsername(f.BbsA));
        }
        finally { f.Dispose(); }
    }

    private sealed class Fixture : IDisposable
    {
        public required string BbsA { get; init; }
        public required string BbsB { get; init; }
        public required BbsProfileStore Store { get; init; }
        public required ProfileService Profile { get; init; }
        public required PasswordProtector Passwords { get; init; }
        public required BbsSectionViewModel Vm { get; init; }

        public static Fixture Create()
        {
            ScratchRoot.Require();
            string a = $"mudplay-test-a-{Guid.NewGuid():N}";
            string b = $"mudplay-test-b-{Guid.NewGuid():N}";
            BbsProfileStore store = new();
            store.Save(new BbsProfile { Name = a, Host = "a.example", Port = 23 });
            store.Save(new BbsProfile { Name = b, Host = "b.example", Port = 23 });

            // Started on board A, so the view model opens on this test's board: one
            // it opened on would be written back on Apply, and another test's board,
            // deleted by its owner meanwhile, would be made again.
            ProfileService profile = TestProfiles.Service();
            profile.LoadDefaultProfile(onBbs: a);

            PasswordProtector passwords = new();
            return new Fixture
            {
                BbsA = a,
                BbsB = b,
                Store = store,
                Profile = profile,
                Passwords = passwords,
                Vm = new BbsSectionViewModel(store, profile, passwords, new DisplayConfig(), new SettingsService()),
            };
        }

        // The username as it now sits on the character profile, decrypted. Null
        // when no credential was written for that board at all.
        public string? CommittedUsername(string bbs)
        {
            if (Profile.Current?.BbsCredentials is not { } creds) return null;
            if (!creds.TryGetValue(bbs, out BbsCredentials? cred)) return null;
            return cred.EncryptedUsername is { } enc ? Passwords.Unprotect(enc) : null;
        }

        public void Dispose()
        {
            Vm.Dispose();
            Store.Delete(BbsA);
            Store.Delete(BbsB);
        }
    }
}
