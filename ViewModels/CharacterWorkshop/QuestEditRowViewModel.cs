using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// One editable quest in the <see cref="QuestEditorViewModel"/> master list. Holds
/// the user-owned overlay fields — <see cref="Name"/>, <see cref="Visible"/>,
/// <see cref="Steps"/> — as live two-way state, alongside read-only crawl context
/// (<see cref="AutoSteps"/> / <see cref="BonusText"/> / <see cref="LevelText"/>) so
/// the user can see the auto-drafted baseline while writing amplifying markdown.
/// Identity is the (<see cref="Flag"/>, <see cref="Step"/>) pair.
/// </summary>
public sealed partial class QuestEditRowViewModel : ObservableObject
{
    /// <summary>Quest-flag ability id (the overlay key's flag half).</summary>
    public int Flag { get; }

    /// <summary>Band level for a multi-part quest; <c>0</c> for a single-part one.</summary>
    public int Step { get; }

    /// <summary>Auto-draft title shown in the list when <see cref="Name"/> is blank.</summary>
    public string FallbackLabel { get; }

    /// <summary>The crawler's drafted steps, joined for read-only reference.</summary>
    public string AutoSteps { get; }
    public bool HasAutoSteps => AutoSteps.Length > 0;

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

    public QuestEditRowViewModel(int flag, int step, string fallbackLabel,
                                 string autoSteps, string bonusText, string levelText,
                                 string name, bool visible, string steps)
    {
        Flag = flag;
        Step = step;
        FallbackLabel = fallbackLabel;
        AutoSteps = autoSteps;
        BonusText = bonusText;
        LevelText = levelText;
        _name = name;
        _visible = visible;
        _steps = steps;
    }

    /// <summary>Left-list label: the user's name (or the auto-draft fallback), suffixed when hidden.</summary>
    public string ListLabel
    {
        get
        {
            string baseName = string.IsNullOrWhiteSpace(Name) ? FallbackLabel : Name;
            return Visible ? baseName : $"{baseName}  (hidden)";
        }
    }

    /// <summary>Materialize the current edits into a persistable definition (empty steps → null).</summary>
    public QuestDefinition ToDefinition() =>
        new(Flag, Step,
            (Name ?? string.Empty).Trim(),
            Visible,
            string.IsNullOrWhiteSpace(Steps) ? null : Steps);
}
