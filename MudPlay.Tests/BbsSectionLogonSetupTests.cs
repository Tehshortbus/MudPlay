using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.ViewModels.Settings;
using Xunit;

namespace MudPlay.Tests;

// The Settings BBS tab's logon steps, the board's own and shared by every character
// on it (issue #1219), or one character's own when checked "only for this
// character" (Paradigm accounts pick a character slot each). Picking a realm's game data set fills in the setup that data
// was exported for, Anomaly, Paradigm or Stock, only where nothing is set yet; the
// Connection header's Apply setup puts one in over what's there; another board's
// steps can be imported.
//
// The boards are saved under the scratch data root and the game data sets under its
// game-data folder; all are deleted in Dispose.
[Collection(BbsSectionCollection.Name)]
public sealed class BbsSectionLogonSetupTests : IDisposable
{
    private readonly string _tag = Path.GetRandomFileName().Replace(".", "");
    private readonly string _board;
    private readonly string _otherBoard;
    private readonly string _anomalySet;
    private readonly string _paradigmSet;
    private readonly ProfileService _profile;
    private BbsSectionViewModel _vm;

    public BbsSectionLogonSetupTests()
    {
        ScratchRoot.Require();
        _board = $"!mudplay-test-logonsetup-{_tag}";
        _otherBoard = $"!mudplay-test-logonsetup-other-{_tag}";
        _anomalySet = WriteSet("anomaly", "Anomaly");
        _paradigmSet = WriteSet("paradigm", "Paradigm");
        BbsProfileStore store = new();
        store.Save(new BbsProfile { Name = _board, Realms = { new RealmProfile { Name = "Main" } } });
        _profile = TestProfiles.Service();
        _profile.LoadDefaultProfile(onBbs: _board);
        _vm = NewTab();
    }

    // The tab as the Settings window opens it, on the board.
    private BbsSectionViewModel NewTab()
    {
        BbsSectionViewModel vm = new(new BbsProfileStore(), _profile, new PasswordProtector(), new DisplayConfig(),
            new SettingsService(), new GameDataCache());
        vm.SelectedBbsName = _board;
        return vm;
    }

    public void Dispose()
    {
        _vm.Dispose();
        new BbsProfileStore().Delete(_board);
        new BbsProfileStore().Delete(_otherBoard);
        foreach (string set in new[] { _anomalySet, _paradigmSet })
        {
            try { Directory.Delete(AppPaths.GameDataSetDir(set), recursive: true); }
            catch (IOException) { /* best-effort cleanup of a scratch folder */ }
        }
    }

    private string WriteSet(string kind, string custom)
    {
        string set = $"!mudplay-test-{kind}-{_tag}";
        string dir = AppPaths.GameDataSetDir(set);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Info.json"), $$"""[ { "Custom": "{{custom}}", "Legit": 2 } ]""");
        return set;
    }

    private IEnumerable<string> Sends() => _vm.LogonSteps.Select(s => s.Send).ToList();

    [Fact]
    public void ANewBoard_PickingAnAnomalySet_FillsTheHostPortAndItsLogonSteps()
    {
        Assert.Equal(["{username}", "{password}", "m"], Sends());

        _vm.RealmGameDataSet = _anomalySet;

        Assert.Equal("playanomalymud.com", _vm.Host);
        Assert.Equal(2324, _vm.Port);
        Assert.Equal(["{username}", "{password}", "1"], Sends());
    }

    [Fact]
    public void AHostTypedAndStepsEdited_StayWhenASetIsPicked()
    {
        _vm.Host = "my.board.example";
        _vm.LogonSteps[2].Send = "g";

        _vm.RealmGameDataSet = _paradigmSet;

        Assert.Equal("my.board.example", _vm.Host);
        Assert.Equal(["{username}", "{password}", "g"], Sends());
    }

    [Fact]
    public void ApplySetup_PutsItInOverWhatsThere_AndStockLeavesTheHostAlone()
    {
        _vm.Host = "my.board.example";
        _vm.LogonSteps[2].Send = "g";

        _vm.ApplyLogonSetupCommand.Execute("Paradigm");

        Assert.Equal("paramud.mudinfo.net", _vm.Host);
        Assert.Equal(2323, _vm.Port);
        Assert.Equal(["{username}", "{password}", "P", "1"], Sends());

        _vm.ApplyLogonSetupCommand.Execute("Stock");

        Assert.Equal("paramud.mudinfo.net", _vm.Host);
        Assert.Equal(["{username}", "{password}", "m"], Sends());
        _vm.Apply();
        Assert.Equal("paramud.mudinfo.net", new BbsProfileStore().Get(_board)!.Host);
    }

    // Steps saved as the client filled them in show the setup installed data calls
    // for; steps the player applied by hand are saved as theirs and stay.
    [Fact]
    public void SavedStepsTheClientFilledIn_GiveWayToTheRealmsData_AndAppliedOnesStay()
    {
        BbsProfileStore store = new();
        BbsProfile board = store.Get(_board)!;
        board.LogonSteps = BuiltInBbses.Stock.CopyLogonSteps();
        board.AutoLogonSetup = "Stock";
        board.Realms[0].ActiveGameDataSet = _anomalySet;
        store.Save(board);
        _vm.Dispose();
        _vm = NewTab();

        Assert.Equal(["{username}", "{password}", "1"], Sends());

        _vm.ApplyLogonSetupCommand.Execute("Stock");
        _vm.Apply();
        Assert.Null(new BbsProfileStore().Get(_board)!.AutoLogonSetup);
        _vm.Dispose();
        _vm = NewTab();

        Assert.Equal(["{username}", "{password}", "m"], Sends());
    }

    // The steps are the board's: set with one character loaded, they're what the
    // next character on the board shows and logs on with, while each keeps its own
    // username; nothing of them is written onto the character.
    [Fact]
    public void LogonStepsSetWithOneCharacter_AreTheNextCharactersOnTheBoard()
    {
        _profile.SaveAs(_board, "Alpha");
        _vm.LogonSteps[2].Send = "g";
        _vm.Username = "alpha";
        _vm.Apply();

        BbsProfile saved = new BbsProfileStore().Get(_board)!;
        Assert.Equal(["{username}", "{password}", "g"], saved.LogonSteps!.Select(st => st.Send));
        Assert.Null(saved.AutoLogonSetup);
        Assert.DoesNotContain("MenuNavSteps", File.ReadAllText(AppPaths.CharacterProfileFile(_board, "Alpha")));

        _profile.CreateProfile(_board, "Beta");
        _profile.Load(_board, "Beta");

        Assert.Equal(["{username}", "{password}", "g"], Sends());
        Assert.Equal("", _vm.Username);
    }

    // One account's characters answering the board's menus differently (a character
    // slot each): the one checked "only for this character" logs on with its own
    // steps, the board's stay as they are for the rest, and unchecking drops its own.
    [Fact]
    public void ACharactersOwnSteps_AreItsAlone_AndUncheckingGoesBackToTheBoards()
    {
        _profile.SaveAs(_board, "Alpha");
        _vm.LogonSteps[2].Send = "g";
        _vm.Apply();
        _profile.CreateProfile(_board, "Beta");
        _profile.Load(_board, "Beta");

        _vm.LogonStepsForCharacter = true;
        _vm.AddLogonStepCommand.Execute(null);
        _vm.LogonSteps[3].WaitForPattern = "Character Selection";
        _vm.LogonSteps[3].Send = "2";
        _vm.Apply();

        Assert.Equal(["{username}", "{password}", "g"], new BbsProfileStore().Get(_board)!.LogonSteps!.Select(st => st.Send));
        _profile.Load(_board, "Alpha");
        Assert.False(_vm.LogonStepsForCharacter);
        Assert.Equal(["{username}", "{password}", "g"], Sends());
        Assert.Null(_profile.Current!.BbsCredentials?.GetValueOrDefault(_board)?.LogonSteps);
        _profile.Load(_board, "Beta");
        Assert.True(_vm.LogonStepsForCharacter);
        Assert.Equal(["{username}", "{password}", "g", "2"], Sends());
        Assert.Equal("2", _profile.Current!.BbsCredentials![_board].LogonSteps![^1].Send);

        _vm.LogonStepsForCharacter = false;
        Assert.Equal(["{username}", "{password}", "g"], Sends());
        _vm.Apply();
        Assert.Null(_profile.Current!.BbsCredentials![_board].LogonSteps);
        Assert.Equal(3, new BbsProfileStore().Get(_board)!.LogonSteps!.Count);
    }

    [Fact]
    public void AnotherBoardsSteps_AreOfferedForImport_AndReplaceTheseOnImport()
    {
        new BbsProfileStore().Save(new BbsProfile
        {
            Name = _otherBoard,
            LogonSteps = [new() { WaitForPattern = "Login:", Send = "{username}" }, new() { WaitForPattern = "Menu:", Send = "x" }],
        });
        BbsProfileStore store = new();
        BbsProfile editing = store.Get(_board)!;
        editing.LogonSteps = BuiltInBbses.Stock.CopyLogonSteps();
        store.Save(editing);
        _vm.Dispose();
        _vm = NewTab();

        LogonStepsImportOption offered = Assert.Single(_vm.ImportSourceOptions, o => o.BbsName == _otherBoard);
        Assert.DoesNotContain(_vm.ImportSourceOptions, o => o.BbsName == _board);
        _vm.SelectedImportSource = offered;
        _vm.ImportLogonStepsCommand.Execute(null);
        _vm.Apply();

        Assert.Equal(["{username}", "x"], new BbsProfileStore().Get(_board)!.LogonSteps!.Select(st => st.Send));
    }
}
