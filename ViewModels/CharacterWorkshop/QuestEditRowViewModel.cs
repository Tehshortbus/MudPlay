using System;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// One editable quest in the <see cref="QuestEditorViewModel"/> master list. Holds
/// the user-owned overlay fields — <see cref="Name"/>, <see cref="Visible"/>,
/// <see cref="Steps"/> — as live two-way state, both pre-filled from the crawl
/// baseline (<see cref="FallbackLabel"/> / <see cref="AutoSteps"/>) so the boxes show
/// the auto-draft the moment the editor opens. <see cref="ToDefinition"/> diffs back
/// against that baseline so an untouched prefill is never frozen into the overlay.
/// Identity is the (<see cref="Flag"/>, <see cref="Step"/>) pair.
/// </summary>
public sealed partial class QuestEditRowViewModel : ObservableObject
{
    /// <summary>Quest-flag ability id (the overlay key's flag half).</summary>
    public int Flag { get; }

    /// <summary>Band level for a multi-part quest; <c>0</c> for a single-part one.</summary>
    public int Step { get; }

    /// <summary>Auto-draft title — pre-fills <see cref="Name"/> and is the delta baseline for it.</summary>
    public string FallbackLabel { get; }

    /// <summary>The crawler's drafted steps — pre-fills <see cref="Steps"/> and is its delta baseline.</summary>
    public string AutoSteps { get; }

    /// <summary>The crawler's inferred award label — pre-fills <see cref="Rewards"/> and is its delta baseline.</summary>
    public string AutoRewards { get; }

    /// <summary>Class-resolved permanent bonus summary; empty when the quest grants none.</summary>
    public string BonusText { get; }
    public bool HasBonus => BonusText.Length > 0;

    /// <summary>Level-gate label; empty when ungated.</summary>
    public string LevelText { get; }
    public bool HasLevel => LevelText.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListLabel))]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListLabel))]
    private bool _visible;

    [ObservableProperty] private string _steps;

    [ObservableProperty] private string _rewards;

    public QuestEditRowViewModel(int flag, int step, string fallbackLabel,
                                 string autoSteps, string autoRewards, string bonusText,
                                 string levelText, string name, bool visible,
                                 string steps, string rewards)
    {
        Flag = flag;
        Step = step;
        FallbackLabel = fallbackLabel;
        AutoSteps = autoSteps;
        AutoRewards = autoRewards;
        BonusText = bonusText;
        LevelText = levelText;
        // Prefill the editable boxes from the crawl baseline so the user starts from the
        // auto-draft rather than a blank field; a saved overlay value (if any) wins.
        _name = string.IsNullOrWhiteSpace(name) ? fallbackLabel : name;
        _visible = visible;
        _steps = string.IsNullOrEmpty(steps) ? autoSteps : steps;
        _rewards = string.IsNullOrEmpty(rewards) ? autoRewards : rewards;
    }

    /// <summary>Left-list label: the current name (or the auto-draft fallback), suffixed when hidden.</summary>
    public string ListLabel
    {
        get
        {
            string baseName = string.IsNullOrWhiteSpace(Name) ? FallbackLabel : Name;
            return Visible ? baseName : $"{baseName}  (hidden)";
        }
    }

    /// <summary>
    /// Materialize the current edits into a persistable definition, diffed against the
    /// crawl baseline: a name still equal to the fallback, steps still equal to the
    /// auto-draft, or rewards still equal to the inferred award, collapse to empty/null
    /// so an untouched prefill isn't frozen into the overlay
    /// (<see cref="QuestStore.Save"/> then drops the redundant row entirely).
    /// </summary>
    public QuestDefinition ToDefinition()
    {
        string name = (Name ?? string.Empty).Trim();
        if (string.Equals(name, FallbackLabel, StringComparison.Ordinal)) name = string.Empty;

        string? steps = string.IsNullOrWhiteSpace(Steps) ? null : Steps;
        if (steps is not null && string.Equals(steps.Trim(), AutoSteps.Trim(), StringComparison.Ordinal))
            steps = null;

        string? rewards = string.IsNullOrWhiteSpace(Rewards) ? null : Rewards;
        if (rewards is not null && string.Equals(rewards.Trim(), AutoRewards.Trim(), StringComparison.Ordinal))
            rewards = null;

        return new QuestDefinition(Flag, Step, name, Visible, steps, rewards);
    }
}
