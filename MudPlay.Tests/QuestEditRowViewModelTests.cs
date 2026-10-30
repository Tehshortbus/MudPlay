using System.Collections.Generic;
using MudPlay.Models.Profile;
using MudPlay.ViewModels.CharacterWorkshop;
using Xunit;

namespace MudPlay.Tests;

// The Quest editor row's two eligibility-facing controls: the "Show in quest journal"
// checkbox (ShowInJournal) whose backing field flips with eligibility, and the
// "Restrict to classes" checklist (ClassOptions) that persists as ClassRestrict.
public sealed class QuestEditRowViewModelTests
{
    // Builds a crawled-quest row (flag < ManualFlagBase) so ToDefinition runs the
    // delta-diff path; name/steps/rewards match their baselines so only the eligibility
    // fields under test carry through.
    private static QuestEditRowViewModel Row(bool ineligible, bool showIfIneligible = false,
                                             bool visible = true, IReadOnlyList<ClassRestrictOption>? options = null) =>
        new(50, 1, "Fallback", autoSteps: "", autoRewards: "", bonusText: "",
            levelText: "", autoRequiredLevel: 0, requirementsText: "",
            name: "Fallback", visible: visible, steps: "", rewards: "",
            requiredLevel: null, ineligible: ineligible, showIfIneligible: showIfIneligible,
            classOptions: options);

    // The draft is redrawn when the tier before it gets the realm's name with the editor
    // open: steps still at the draft follow it and save as untouched; steps the user
    // wrote stay as written.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Redraft_MovesUntouchedStepsWithTheDraft_AndLeavesTheUsersOwn(bool edited)
    {
        QuestEditRowViewModel row = new(50, 2, "Good 2", autoSteps: "-Complete Good 1 first-\nsay hi", autoRewards: "",
            bonusText: "", levelText: "", autoRequiredLevel: 0, requirementsText: "",
            name: "Good 2", visible: true, steps: "", rewards: "", requiredLevel: null);
        if (edited) row.Steps = "my own notes";

        row.Redraft("-Complete 1st Alignment Quest first-\nsay hi");

        Assert.Equal(edited ? "my own notes" : "-Complete 1st Alignment Quest first-\nsay hi", row.Steps);
        Assert.Equal(edited ? "my own notes" : null, row.ToDefinition().Steps);
    }

    [Fact]
    public void ShowInJournal_EligibleQuest_MapsToVisibleHide()
    {
        QuestEditRowViewModel row = Row(ineligible: false, visible: true);
        Assert.True(row.ShowInJournal);          // eligible + visible → shown

        row.ShowInJournal = false;               // per-taste hide
        Assert.False(row.Visible);
        Assert.False(row.ToDefinition().Visible);
        Assert.False(row.ShowIfIneligibleOverride);   // untouched for an eligible quest
    }

    [Fact]
    public void ShowInJournal_IneligibleQuest_StartsUnchecked_ThenMapsToShowAnyway()
    {
        // A "Cannot complete" quest opens unchecked even though its stored Visible is true.
        QuestEditRowViewModel row = Row(ineligible: true, showIfIneligible: false, visible: true);
        Assert.False(row.ShowInJournal);

        row.ShowInJournal = true;                // opt back in
        Assert.True(row.ShowInJournal);
        // The opt-in reads back for per-character persistence; Visible (the per-set field)
        // is left alone so the two never clobber each other.
        Assert.True(row.ShowIfIneligibleOverride);
        Assert.True(row.ToDefinition().Visible);
    }

    [Fact]
    public void ClassOptions_TickedClasses_PersistAsClassRestrict()
    {
        var options = new List<ClassRestrictOption>
        {
            new(3, "Witchhunter", false),
            new(8, "Priest", false),
        };
        QuestEditRowViewModel row = Row(ineligible: false, options: options);

        Assert.Equal("Any class", row.ClassRestrictSummary);
        Assert.Null(row.SelectedClassNumbers());
        Assert.Null(row.ToDefinition().ClassRestrict);

        options[0].IsSelected = true;

        Assert.Equal("Witchhunter", row.ClassRestrictSummary);
        Assert.Equal(new List<int> { 3 }, row.SelectedClassNumbers());
        Assert.Equal(new List<int> { 3 }, row.ToDefinition().ClassRestrict);
    }

    [Fact]
    public void ClassOptions_PreSelected_SummarizeAndPersist()
    {
        var options = new List<ClassRestrictOption>
        {
            new(3, "Witchhunter", true),
            new(8, "Priest", true),
        };
        QuestEditRowViewModel row = Row(ineligible: false, options: options);

        Assert.Equal("Witchhunter, Priest", row.ClassRestrictSummary);
        Assert.Equal(new List<int> { 3, 8 }, row.SelectedClassNumbers());
    }

    // A crawled row whose complete-value box is exercised: prefilled from the crawl, and
    // diffed on save so an unchanged value collapses but an edit persists.
    private static QuestEditRowViewModel CompleteRow(int? autoComplete, int? overrideValue = null) =>
        new(50, 1, "Fallback", autoSteps: "", autoRewards: "", bonusText: "",
            levelText: "", autoRequiredLevel: 0, requirementsText: "",
            name: "Fallback", visible: true, steps: "", rewards: "",
            requiredLevel: null, ineligible: false, showIfIneligible: false,
            classOptions: null, autoCompleteValue: autoComplete, completeValueOverride: overrideValue);

    [Fact]
    public void CompleteValue_PrefillsFromCrawl_AndCollapsesWhenUnchanged()
    {
        QuestEditRowViewModel row = CompleteRow(autoComplete: 9);
        Assert.Equal(9, row.CompleteValueInput);            // prefilled from the crawl
        Assert.Null(row.ToDefinition().CompleteValueOverride); // still equal to baseline → no delta
    }

    [Fact]
    public void CompleteValue_EditedValuePersistsAsOverride()
    {
        QuestEditRowViewModel row = CompleteRow(autoComplete: 9);
        row.CompleteValueInput = 12;                        // correct the crawl's guess
        Assert.Equal(12, row.ToDefinition().CompleteValueOverride);
    }

    [Fact]
    public void CompleteValue_SuppliedWhenCrawlHadNone()
    {
        // Crawl derived nothing (undetectable); the user fills one in and it persists.
        QuestEditRowViewModel row = CompleteRow(autoComplete: null);
        Assert.Null(row.CompleteValueInput);
        row.CompleteValueInput = 4;
        Assert.Equal(4, row.ToDefinition().CompleteValueOverride);
    }
}
