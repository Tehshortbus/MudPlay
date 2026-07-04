using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Quests;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// Modeless editor for the active set's quest overlay ({set}/quests.json): per
// quest the user sets a display name, show/hide visibility, and amplifying step
// markdown over the crawler's auto-draft. Every crawled quest is listed — hidden
// ones included, so they can be un-hidden. The user can also Add a custom quest
// the crawl never finds (a self-contained manual row in the reserved
// QuestDefinition.ManualFlagBase range — Delete removes it) and Block a crawled
// quest a set surfaces spuriously (a journal-suppressing flag that keeps the row
// here so it can be un-blocked). Save writes the overlay as a delta (untouched
// seed/default rows aren't frozen in, manual rows verbatim) via QuestStore.Save;
// Cancel / title-bar X discard. Standard edit-window contract: Save commits,
// X / Cancel discards.
public sealed partial class QuestEditorViewModel : ObservableObject, IDialogViewModel<bool>
{
    public event Action<bool>? CloseRequested;

    private readonly QuestStore _quests;

    // Every crawled quest in crawl order (flag, then band level), editable.
    public ObservableCollection<QuestEditRowViewModel> Quests { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private QuestEditRowViewModel? _selectedQuest;

    // False when the active set crawls no quests — drives the empty-state hint.
    public bool HasQuests => Quests.Count > 0;

    // True when a quest is selected — gates the detail pane.
    public bool HasSelection => SelectedQuest is not null;

    // gameData: active set, source of the crawl + item names. quests: overlay store
    // the edits persist to. classId: character class number for class-resolved
    // bonus labels, or null for the no-class default.
    public QuestEditorViewModel(GameDataCache gameData, QuestStore quests, int? classId)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(quests);
        _quests = quests;

        Quests.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasQuests));

        foreach (CrawledQuest q in QuestCrawler.Crawl(gameData, classId))
        {
            QuestDefinition def = quests.Resolve(q.Flag, q.Step);
            Quests.Add(new QuestEditRowViewModel(
                q.Flag, q.Step,
                QuestTextFormatter.FallbackTitle(q),
                string.Join("\n", QuestTextFormatter.StepLines(gameData, q)),
                QuestTextFormatter.Awards(gameData, q),
                QuestTextFormatter.Bonuses(q.Bonuses),
                QuestTextFormatter.Level(q.RequiredLevel),
                q.RequiredLevel,
                QuestTextFormatter.Requirements(gameData, q),
                def.Name,
                def.Visible,
                def.Steps ?? string.Empty,
                def.Rewards ?? string.Empty,
                def.RequiredLevel)
            { Blocked = def.Blocked });
        }

        // User-added quests the crawl never produces, listed after the crawled ones.
        foreach (QuestDefinition def in quests.ManualQuests())
            Quests.Add(BuildManualRow(def));

        SelectedQuest = Quests.FirstOrDefault();
    }

    // Add a blank custom quest at the next free manual flag and select it for editing.
    [RelayCommand]
    private void AddQuest()
    {
        int flag = QuestDefinition.ManualFlagBase;
        foreach (QuestEditRowViewModel row in Quests)
            if (row.IsManual && row.Flag >= flag) flag = row.Flag + 1;

        QuestEditRowViewModel added = BuildManualRow(new QuestDefinition(flag, 0));
        Quests.Add(added);
        SelectedQuest = added;
    }

    // Delete the selected manual quest (crawled quests are blocked, not deleted). Keeps a
    // neighbouring row selected so the detail pane doesn't blank out mid-edit.
    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedQuest is not { IsManual: true } row) return;
        int index = Quests.IndexOf(row);
        Quests.Remove(row);
        SelectedQuest = Quests.Count == 0 ? null : Quests[Math.Min(index, Quests.Count - 1)];
    }

    // A master-list row for a manual quest: every crawl-baseline field is empty, so the
    // editable boxes pre-fill straight from the definition and persist verbatim.
    private static QuestEditRowViewModel BuildManualRow(QuestDefinition def) =>
        new(def.Flag, def.Step,
            fallbackLabel: "(custom quest)",
            autoSteps: string.Empty,
            autoRewards: string.Empty,
            bonusText: string.Empty,
            levelText: QuestTextFormatter.Level(def.RequiredLevel ?? 0),
            autoRequiredLevel: 0,
            requirementsText: string.Empty,
            name: def.Name,
            visible: def.Visible,
            steps: def.Steps ?? string.Empty,
            rewards: def.Rewards ?? string.Empty,
            requiredLevel: def.RequiredLevel)
        { Blocked = def.Blocked };

    [RelayCommand]
    private void Save()
    {
        _quests.Save(Quests.Select(q => q.ToDefinition()));
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);
}
