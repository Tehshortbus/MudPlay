using System;
using System.IO;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.ViewModels.Settings;
using Xunit;

namespace MudPlay.Tests;

// The Settings BBS tab's live server data (GMCP) section shows only for a board
// whose server has completed a GMCP handshake (BbsProfile.GmcpOffered), and its
// row of "All" buttons sets every package's mode. The board is saved under the
// scratch data root and deleted in Dispose.
[Collection(BbsSectionCollection.Name)]
public sealed class BbsSectionGmcpSectionTests : IDisposable
{
    private readonly string _board = "!mudplay-test-gmcpsection-" + Path.GetRandomFileName();
    private readonly ProfileService _profile;

    public BbsSectionGmcpSectionTests()
    {
        ScratchRoot.Require();
        new BbsProfileStore().Save(new BbsProfile { Name = _board, Host = "a.example", Realms = { new RealmProfile { Name = "Main" } } });
        _profile = TestProfiles.Service();
        _profile.LoadDefaultProfile(onBbs: _board);
    }

    public void Dispose() => new BbsProfileStore().Delete(_board);

    private BbsSectionViewModel NewTab()
    {
        BbsSectionViewModel vm = new(new BbsProfileStore(), _profile, new PasswordProtector(), new DisplayConfig(),
            new SettingsService(), new GameDataCache());
        vm.SelectedBbsName = _board;
        return vm;
    }

    [Fact]
    public void ABoardWhoseServerNeverOfferedGmcp_HidesTheServerDataModes()
    {
        using BbsSectionViewModel vm = NewTab();

        Assert.False(vm.GmcpOffered);
    }

    // A handshake while the window is open marks the board on disk; the window's OK
    // saves its own copy of the board, which predates that, and must not undo it
    // (BbsProfileStore folds in what changed on disk since the copy was read).
    [Fact]
    public void AHandshakeWhileTheWindowIsOpen_SurvivesItsOk_AndShowsTheModesNextTime()
    {
        using (BbsSectionViewModel vm = NewTab())
        {
            BbsProfileStore store = new();
            BbsProfile board = store.Get(_board)!;
            board.GmcpOffered = true;
            store.Save(board);

            vm.GameEntryCommand = "F";
            vm.Apply();
        }

        Assert.True(new BbsProfileStore().Get(_board)!.GmcpOffered);
        using BbsSectionViewModel reopened = NewTab();
        Assert.True(reopened.GmcpOffered);
    }

    // The row above the package list sets every package's mode at once, and OK
    // saves them with the board.
    [Theory]
    [InlineData(Game.Gmcp.GmcpMode.Off)]
    [InlineData(Game.Gmcp.GmcpMode.Active)]
    public void AllPackages_SetsEveryModeAtOnce_AndOkSavesThem(Game.Gmcp.GmcpMode mode)
    {
        using (BbsSectionViewModel vm = NewTab())
        {
            vm.SetAllGmcpModesCommand.Execute(mode);
            Assert.All(vm.GmcpPackages, p => Assert.Equal(mode, p.Mode));
            vm.Apply();
        }

        BbsProfile saved = new BbsProfileStore().Get(_board)!;
        Assert.Equal(Game.Gmcp.GmcpIntegration.Packages.Length, saved.GmcpModes!.Count);
        Assert.All(saved.GmcpModes, m => Assert.Equal(mode, m.Value));
    }
}
