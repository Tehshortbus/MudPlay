using MudPlay.Models.Settings;
using MudPlay.ViewModels.Guides;
using Xunit;

namespace MudPlay.Tests;

// Pins the first-run setup tour, now one Visual Guide among many: which steps are
// built from the missing prerequisites, Prev/Next navigation, live done-detection
// + auto-advance on Refresh, and the dismiss persistence.
public sealed class SetupGuideTests
{
    internal sealed class Flags
    {
        public bool GameData, Bbs, Character, Connected, Dismissed;
        // Unset: a character counts as loaded once one exists.
        public bool? Loaded;
        public GuideProgress Progress = new();
    }

    internal static GuideProbes Probes(Flags f) => new(
        HasGameData: () => f.GameData,
        HasBbs: () => f.Bbs,
        HasCharacter: () => f.Character,
        CharacterLoaded: () => f.Loaded ?? f.Character,
        IsConnected: () => f.Connected);

    internal static GuideViewModel Make(Flags f) =>
        new(GuideCatalogue.Build(Probes(f)), () => f.Progress, () => { }, () => f.Dismissed = true);

    // The step used to end at the add, and a new player went on playing the unnamed
    // default profile with Profile Management still open (user, 2026-10-14). It
    // ends when a named character is loaded, which Add on that profile now does.
    [Fact]
    public void AddACharacter_EndsWhenTheCharacterIsLoaded_NotWhenItIsAdded()
    {
        Flags f = new() { Bbs = true, Loaded = false };
        GuideViewModel vm = Make(f);
        vm.StartSetup();
        Assert.Equal("Add a character", vm.CurrentTitle);

        vm.NotifyActionDone(GuideActions.ProfileManagement);
        vm.NotifyActionDone(GuideActions.AddCharacter);
        f.Character = true;                                  // added, not loaded
        vm.NotifyActionDone(GuideActions.CharacterAdded);
        Assert.Equal("Add a character", vm.CurrentTitle);       // every line ticked, but nothing loaded yet

        f.Loaded = true;
        vm.Refresh();
        Assert.Equal("Import game data", vm.CurrentTitle);
    }

    private static (GuideViewModel vm, Flags f) Make()
    {
        Flags f = new();
        return (Make(f), f);
    }

    [Fact]
    public void Start_AllMissing_BuildsFourStepsFromMissingPlusConnect()
    {
        (GuideViewModel vm, _) = Make();
        vm.StartSetup();
        Assert.True(vm.IsActive);
        Assert.Equal("Add a BBS", vm.CurrentTitle);
        Assert.Equal("Step 1 of 4", vm.StepCounterText);
        Assert.False(vm.CanPrev);
    }

    [Fact]
    public void Start_OnlyGameDataMissing_SkipsSatisfiedPrereqSteps()
    {
        (GuideViewModel vm, Flags f) = Make();
        f.Bbs = true;
        f.Character = true;
        vm.StartSetup();
        // Game data step + Connect finish only.
        Assert.Equal("Import game data", vm.CurrentTitle);
        Assert.Equal("Step 1 of 2", vm.StepCounterText);
    }

    [Fact]
    public void Start_OnlyBbsMissing_StartsAtBbs()
    {
        (GuideViewModel vm, Flags f) = Make();
        f.GameData = true;
        f.Character = true;
        vm.StartSetup();
        Assert.Equal("Add a BBS", vm.CurrentTitle);
        Assert.Equal("Step 1 of 2", vm.StepCounterText);
    }

    [Fact]
    public void Start_NothingMissing_ShowsOnlyConnect()
    {
        (GuideViewModel vm, Flags f) = Make();
        f.GameData = f.Bbs = f.Character = true;
        Assert.False(SetupGuideTests.Probes(f).AnyPrerequisiteMissing);
        vm.StartSetup();
        Assert.Equal("Connect", vm.CurrentTitle);
        Assert.Equal("Step 1 of 1", vm.StepCounterText);
        Assert.True(vm.IsLastStep);
        Assert.Equal("Finish", vm.NextButtonText);
    }

    [Fact]
    public void NextAndPrev_WalkTheSteps()
    {
        (GuideViewModel vm, _) = Make();
        vm.StartSetup();
        Assert.Equal("Add a BBS", vm.CurrentTitle);
        vm.NextCommand.Execute(null);
        Assert.Equal("Add a character", vm.CurrentTitle);
        Assert.True(vm.CanPrev);
        vm.PrevCommand.Execute(null);
        Assert.Equal("Add a BBS", vm.CurrentTitle);
    }

    [Fact]
    public void Next_OnLastStep_FinishesAndDismisses()
    {
        (GuideViewModel vm, Flags f) = Make();
        f.GameData = f.Bbs = f.Character = true;   // only Connect
        vm.StartSetup();
        Assert.True(vm.IsLastStep);
        vm.NextCommand.Execute(null);
        Assert.False(vm.IsActive);
        Assert.True(f.Dismissed);
    }

    [Fact]
    public void StartDemo_ShowsAllStepsEvenWhenFullyConfigured()
    {
        (GuideViewModel vm, Flags f) = Make();
        f.GameData = f.Bbs = f.Character = f.Connected = true;   // nothing missing
        vm.StartDemo();
        Assert.True(vm.IsActive);
        Assert.Equal("Add a BBS", vm.CurrentTitle);       // never leads with Connect
        Assert.Equal("Step 1 of 4", vm.StepCounterText);
        Assert.False(vm.CurrentIsDone);                    // pretends not-done
    }

    [Fact]
    public void StartDemo_OpensAtFirstMissingSetupStep()
    {
        (GuideViewModel vm, Flags f) = Make();
        f.Bbs = true;   // has a BBS, missing character + game data
        vm.StartDemo();
        Assert.Equal("Add a character", vm.CurrentTitle);   // opens at what's missing
    }

    [Fact]
    public void StartDemo_NothingMissing_OpensAtBbs()
    {
        (GuideViewModel vm, Flags f) = Make();
        f.Bbs = f.Character = f.GameData = true;
        vm.StartDemo();
        Assert.Equal("Add a BBS", vm.CurrentTitle);   // lack nothing → walk from start
    }

    [Fact]
    public void StartDemo_FinishDoesNotPersistDismiss()
    {
        (GuideViewModel vm, Flags f) = Make();
        f.GameData = f.Bbs = f.Character = true;
        vm.StartDemo();
        vm.CloseCommand.Execute(null);
        Assert.False(vm.IsActive);
        Assert.False(f.Dismissed);   // a demo run must not suppress the real tour
    }

    [Fact]
    public void NotifyActionDone_TicksActionLine_AndAdvancesHighlight()
    {
        (GuideViewModel vm, _) = Make();   // all missing → starts at Add a BBS
        vm.StartSetup();
        var before = vm.CurrentLines;
        Assert.False(before[0].IsDone);
        Assert.True(before[0].IsCurrent);             // first line highlighted
        Assert.True(vm.HighlightProfileManagement);   // and the menu item glows

        vm.NotifyActionDone(GuideActions.ProfileManagement);

        var after = vm.CurrentLines;
        Assert.True(after[0].IsDone);                 // "File → Profile Management" ticked
        Assert.False(after[0].IsCurrent);
        Assert.True(after[1].IsCurrent);              // highlight moved to the next line
        Assert.False(vm.HighlightProfileManagement);  // menu-item highlight cleared
    }

    [Fact]
    public void Demo_ActionClick_StillTicksChecklist()
    {
        (GuideViewModel vm, Flags f) = Make();
        f.GameData = f.Bbs = f.Character = true;   // fully configured — demo still walks
        vm.StartDemo();
        Assert.False(vm.CurrentLines[0].IsDone);
        vm.NotifyActionDone(GuideActions.ProfileManagement);
        Assert.True(vm.CurrentLines[0].IsDone);      // action line ticks even in demo
        Assert.True(vm.CurrentLines[1].IsCurrent);   // highlight advances
    }

    [Fact]
    public void Demo_WalkingAllBbsActions_AutoAdvancesToCharacter()
    {
        (GuideViewModel vm, Flags f) = Make();
        f.GameData = f.Bbs = f.Character = true;   // demo on a configured install
        vm.StartDemo();
        Assert.Equal("Add a BBS", vm.CurrentTitle);
        vm.NotifyActionDone(GuideActions.ProfileManagement);
        vm.NotifyActionDone(GuideActions.AddBbs);
        vm.NotifyActionDone(GuideActions.BbsHostPort);
        vm.NotifyActionDone(GuideActions.BbsSaved);
        // All four BBS lines done → step complete → tour advances on its own.
        Assert.Equal("Add a character", vm.CurrentTitle);
        // Profile Management already clicked → that line pre-ticked, highlight on Add.
        Assert.True(vm.CurrentLines[0].IsDone);
        Assert.True(vm.CurrentLines[1].IsCurrent);
    }

    [Fact]
    public void NotifyActionDone_Ignored_ForUnrelatedAction()
    {
        (GuideViewModel vm, _) = Make();
        vm.StartSetup();                                   // Add a BBS (line 1 = Profile Management)
        vm.NotifyActionDone(GuideActions.ImportMdb);
        Assert.False(vm.CurrentLines[0].IsDone);       // unrelated action doesn't tick it
        Assert.True(vm.CurrentLines[0].IsCurrent);
    }

    [Fact]
    public void Skip_DismissesPermanently()
    {
        (GuideViewModel vm, Flags f) = Make();
        vm.StartSetup();
        vm.CloseCommand.Execute(null);
        Assert.False(vm.IsActive);
        Assert.True(f.Dismissed);
    }

    [Fact]
    public void Refresh_AutoAdvancesPastCompletedSteps()
    {
        (GuideViewModel vm, Flags f) = Make();
        vm.StartSetup();                       // at Add a BBS (step 1 of 4)
        f.Bbs = true;
        f.Character = true;
        vm.Refresh();
        // Both leading steps done → advanced to Import game data.
        Assert.Equal("Import game data", vm.CurrentTitle);
    }

    [Fact]
    public void Refresh_StopsAtLastStepEvenIfDone()
    {
        (GuideViewModel vm, Flags f) = Make();
        f.GameData = f.Bbs = f.Character = true;
        vm.StartSetup();                       // only Connect
        f.Connected = true;
        vm.Refresh();
        Assert.True(vm.IsLastStep);
        Assert.True(vm.CurrentIsDone);
        Assert.Equal("Finish", vm.NextButtonText);
    }
}
