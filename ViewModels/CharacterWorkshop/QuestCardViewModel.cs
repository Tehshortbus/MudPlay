using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// One quest card in the Quest Status checklist. Collapsed it shows just the title,
// level gate, and a manual IsComplete checkbox; clicking the header
// (ToggleExpandCommand) reveals its detail — class-resolved bonus + award labels
// and a followable step checklist parsed from the quest's step markdown.
// Completion is one-way: the manual checkbox or ticking every step sets it; the
// section owns persistence and folds a complete quest's bonus into Character Info.
// Toggling the checkbox raises the supplied callback.
public sealed partial class QuestCardViewModel : ObservableObject
{
    private readonly Action<QuestCardViewModel> _onCompletionChanged;

    // Quest-flag ability id — half of the persisted completion identity.
    public int Flag { get; }

    // Band level for a multi-part quest; 0 for a single-part quest.
    public int Step { get; }

    // Display name resolved from the quest store (or a flag-derived fallback).
    public string Title { get; }

    // "Level 15" style gate, or empty when the quest imposes no level gate.
    public string RequiredLevelText { get; }

    // Numeric required level used to order the card in the list; 0 when ungated.
    public int RequiredLevel { get; }

    // Class-resolved permanent stat bonus summary; empty when the quest grants none.
    public string BonusText { get; }

    // Keeper-item award summary; empty when the quest awards no keeper item.
    public string AwardText { get; }

    // Class / race restriction the crawl found; empty when the quest is open to all.
    public string RequirementsText { get; }

    // True when the character's known class or race is excluded from this quest's
    // crawled restriction set — a hard "can't take it" the header surfaces as a
    // "Cannot complete" badge. False when the quest is open to the character, or
    // their class/race is unknown (we only flag a provable exclusion).
    public bool Ineligible { get; }

    // True when BonusText is non-empty.
    public bool HasBonus { get; }

    // True when AwardText is non-empty.
    public bool HasAward { get; }

    // True when RequirementsText is non-empty.
    public bool HasRequirements { get; }

    // Ordered followable step + label rows parsed from the quest's step markdown; empty when it drafts none.
    public ObservableCollection<QuestStepRowViewModel> Steps { get; }

    // True when this card has a followable step checklist to show.
    public bool HasSteps => Steps.Count > 0;

    // True when the card has any detail (requirements / bonus / award / steps) worth expanding to.
    public bool CanExpand => HasRequirements || HasBonus || HasAward || HasSteps;

    // Whether the quest counts as done for this character — applies its bonus.
    [ObservableProperty] private bool _isComplete;

    // Whether the detail pane (bonus / award / steps) is revealed below the header.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph))]
    private bool _isExpanded;

    // Disclosure chevron reflecting IsExpanded.
    public string ExpandGlyph => IsExpanded ? "▾" : "▸";

    public QuestCardViewModel(
        int flag,
        int step,
        string title,
        int requiredLevel,
        string requiredLevelText,
        string bonusText,
        string awardText,
        string requirementsText,
        bool ineligible,
        bool isComplete,
        ObservableCollection<QuestStepRowViewModel> steps,
        Action<QuestCardViewModel> onCompletionChanged)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(requiredLevelText);
        ArgumentNullException.ThrowIfNull(bonusText);
        ArgumentNullException.ThrowIfNull(awardText);
        ArgumentNullException.ThrowIfNull(requirementsText);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(onCompletionChanged);

        Flag = flag;
        Step = step;
        Title = title;
        RequiredLevel = requiredLevel;
        RequiredLevelText = requiredLevelText;
        BonusText = bonusText;
        AwardText = awardText;
        RequirementsText = requirementsText;
        Ineligible = ineligible;
        HasBonus = bonusText.Length > 0;
        HasAward = awardText.Length > 0;
        HasRequirements = requirementsText.Length > 0;
        Steps = steps;
        _isComplete = isComplete;
        _onCompletionChanged = onCompletionChanged;
    }

    partial void OnIsCompleteChanged(bool value) => _onCompletionChanged(this);

    // Header click: reveal / collapse the detail pane (no-op when there's nothing to show).
    [RelayCommand]
    private void ToggleExpand()
    {
        if (CanExpand) IsExpanded = !IsExpanded;
    }
}
