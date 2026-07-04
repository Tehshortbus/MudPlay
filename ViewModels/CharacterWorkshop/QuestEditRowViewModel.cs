using System;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// One editable quest in the QuestEditorViewModel master list. Holds the user-owned
// overlay fields — Name, Visible, Steps — as live two-way state, both pre-filled
// from the crawl baseline (FallbackLabel / AutoSteps) so the boxes show the
// auto-draft the moment the editor opens. ToDefinition diffs back against that
// baseline so an untouched prefill is never frozen into the overlay. Identity is
// the (Flag, Step) pair.
public sealed partial class QuestEditRowViewModel : ObservableObject
{
    // Quest-flag ability id (the overlay key's flag half).
    public int Flag { get; }

    // Band level for a multi-part quest; 0 for a single-part one.
    public int Step { get; }

    // True when this is a user-added quest (no crawl backing) — fields persist verbatim and the row can be deleted.
    public bool IsManual => QuestDefinition.IsManual(Flag);

    // True for a crawled quest — eligible for blocking rather than deletion.
    public bool IsCrawled => !IsManual;

    // Auto-draft title — pre-fills Name and is the delta baseline for it.
    public string FallbackLabel { get; }

    // The crawler's drafted steps — pre-fills Steps and is its delta baseline.
    public string AutoSteps { get; }

    // The crawler's inferred award label — pre-fills Rewards and is its delta baseline.
    public string AutoRewards { get; }

    // Class-resolved permanent bonus summary; empty when the quest grants none.
    public string BonusText { get; }
    public bool HasBonus => BonusText.Length > 0;

    // Level-gate label; empty when ungated.
    public string LevelText { get; }
    public bool HasLevel => LevelText.Length > 0;

    // The crawler's inferred level gate — pre-fills RequiredLevelInput and is its delta baseline (0 when ungated / not found).
    public int AutoRequiredLevel { get; }

    // Class / race restriction the crawl found; empty when the quest is open to all.
    public string RequirementsText { get; }
    public bool HasRequirements => RequirementsText.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListLabel))]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListLabel))]
    private bool _visible;

    // Live block flag bound to the editor's "Block" toggle (crawled quests only). When
    // set the quest is suppressed from the journal entirely as a false positive; the row
    // stays in the editor so it can be un-blocked.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListLabel))]
    private bool _blocked;

    [ObservableProperty] private string _steps;

    [ObservableProperty] private string _rewards;

    // Live required-level override bound to the editor's spinner. null means
    // "no override" (the box is empty) and falls back to the crawled gate; any value
    // (including 0 to force ungated) persists as a user override.
    [ObservableProperty] private int? _requiredLevelInput;

    public QuestEditRowViewModel(int flag, int step, string fallbackLabel,
                                 string autoSteps, string autoRewards, string bonusText,
                                 string levelText, int autoRequiredLevel, string requirementsText,
                                 string name, bool visible, string steps, string rewards,
                                 int? requiredLevel)
    {
        Flag = flag;
        Step = step;
        FallbackLabel = fallbackLabel;
        AutoSteps = autoSteps;
        AutoRewards = autoRewards;
        BonusText = bonusText;
        LevelText = levelText;
        AutoRequiredLevel = autoRequiredLevel;
        RequirementsText = requirementsText;
        // Prefill the editable boxes from the crawl baseline so the user starts from the
        // auto-draft rather than a blank field; a saved overlay value (if any) wins.
        _name = string.IsNullOrWhiteSpace(name) ? fallbackLabel : name;
        _visible = visible;
        _steps = string.IsNullOrEmpty(steps) ? autoSteps : steps;
        _rewards = string.IsNullOrEmpty(rewards) ? autoRewards : rewards;
        // Show the crawled level when there's one to correct, blank when the crawl found
        // none — so an empty box always reads as "no override".
        _requiredLevelInput = requiredLevel ?? (autoRequiredLevel > 0 ? autoRequiredLevel : null);
    }

    // Left-list label: the current name (or the auto-draft fallback), suffixed when blocked / hidden.
    public string ListLabel
    {
        get
        {
            string baseName = string.IsNullOrWhiteSpace(Name) ? FallbackLabel : Name;
            if (Blocked) return $"{baseName}  (blocked)";
            return Visible ? baseName : $"{baseName}  (hidden)";
        }
    }

    // Materialize the current edits into a persistable definition, diffed against the
    // crawl baseline: a name still equal to the fallback, steps still equal to the
    // auto-draft, or rewards still equal to the inferred award, collapse to empty/null
    // so an untouched prefill isn't frozen into the overlay (QuestStore.Save then drops
    // the redundant row entirely).
    public QuestDefinition ToDefinition()
    {
        // A manual quest has no crawl baseline to diff against — it's self-contained, so its
        // fields persist verbatim (QuestStore keeps the row unless it's wholly blank).
        if (IsManual)
            return new QuestDefinition(
                Flag, Step, (Name ?? string.Empty).Trim(), Visible,
                string.IsNullOrWhiteSpace(Steps) ? null : Steps,
                string.IsNullOrWhiteSpace(Rewards) ? null : Rewards,
                RequiredLevelInput);

        string name = (Name ?? string.Empty).Trim();
        if (string.Equals(name, FallbackLabel, StringComparison.Ordinal)) name = string.Empty;

        string? steps = string.IsNullOrWhiteSpace(Steps) ? null : Steps;
        if (steps is not null && string.Equals(steps.Trim(), AutoSteps.Trim(), StringComparison.Ordinal))
            steps = null;

        string? rewards = string.IsNullOrWhiteSpace(Rewards) ? null : Rewards;
        if (rewards is not null && string.Equals(rewards.Trim(), AutoRewards.Trim(), StringComparison.Ordinal))
            rewards = null;

        // An empty box (no override) or one still showing the crawled level isn't a user
        // delta, so it collapses to null rather than freezing into the overlay; a value
        // that differs (incl. 0 to force "ungated") persists as an override.
        int? requiredLevel = RequiredLevelInput;
        if (requiredLevel is null || requiredLevel == AutoRequiredLevel) requiredLevel = null;

        return new QuestDefinition(Flag, Step, name, Visible, steps, rewards, requiredLevel, Blocked);
    }
}
