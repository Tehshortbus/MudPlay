using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// One quest card in the Quest Status checklist: a class-resolved quest (or one
/// band of a multi-part quest) with its level gate, bonus + award labels, a manual
/// <see cref="IsComplete"/> checkbox, and — for a single-part quest — an ordered
/// followable step checklist. Completion is one-way: the manual checkbox or ticking
/// every step sets it; the section owns persistence and folds a complete quest's
/// bonus into Character Info. Toggling the checkbox raises the supplied callback.
/// </summary>
public sealed partial class QuestCardViewModel : ObservableObject
{
    private readonly Action<QuestCardViewModel> _onCompletionChanged;

    /// <summary>Quest-flag ability id — half of the persisted completion identity.</summary>
    public int Flag { get; }

    /// <summary>Band level for a multi-part quest; <c>0</c> for a single-part quest.</summary>
    public int Step { get; }

    /// <summary>Display name resolved from the quest store (or a flag-derived fallback).</summary>
    public string Title { get; }

    /// <summary>"Level 15" style gate, or empty when the quest imposes no level gate.</summary>
    public string RequiredLevelText { get; }

    /// <summary>Class-resolved permanent stat bonus summary; empty when the quest grants none.</summary>
    public string BonusText { get; }

    /// <summary>Keeper-item award summary; empty when the quest awards no keeper item.</summary>
    public string AwardText { get; }

    /// <summary>True when <see cref="BonusText"/> is non-empty.</summary>
    public bool HasBonus { get; }

    /// <summary>True when <see cref="AwardText"/> is non-empty.</summary>
    public bool HasAward { get; }

    /// <summary>Ordered followable steps (single-part quests only); empty otherwise.</summary>
    public ObservableCollection<QuestStepRowViewModel> Steps { get; }

    /// <summary>True when this card has a followable step checklist to show.</summary>
    public bool HasSteps => Steps.Count > 0;

    /// <summary>Whether the quest counts as done for this character — applies its bonus.</summary>
    [ObservableProperty] private bool _isComplete;

    public QuestCardViewModel(
        int flag,
        int step,
        string title,
        string requiredLevelText,
        string bonusText,
        string awardText,
        bool isComplete,
        ObservableCollection<QuestStepRowViewModel> steps,
        Action<QuestCardViewModel> onCompletionChanged)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(requiredLevelText);
        ArgumentNullException.ThrowIfNull(bonusText);
        ArgumentNullException.ThrowIfNull(awardText);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(onCompletionChanged);

        Flag = flag;
        Step = step;
        Title = title;
        RequiredLevelText = requiredLevelText;
        BonusText = bonusText;
        AwardText = awardText;
        HasBonus = bonusText.Length > 0;
        HasAward = awardText.Length > 0;
        Steps = steps;
        _isComplete = isComplete;
        _onCompletionChanged = onCompletionChanged;
    }

    partial void OnIsCompleteChanged(bool value) => _onCompletionChanged(this);
}
