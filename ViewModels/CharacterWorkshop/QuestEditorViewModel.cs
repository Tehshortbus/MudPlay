using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Game.Quests;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.ViewModels.CharacterWorkshop;

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
public sealed partial class QuestEditorViewModel : ObservableObject, IDialogViewModel<bool>, IDisposable
{
    public event Action<bool>? CloseRequested;

    private readonly QuestStore _quests;
    private readonly GameDataCache _gameData;

    // Each crawled row's quest and drafted step lines, to redraw its draft from.
    private readonly Dictionary<QuestEditRowViewModel, (CrawledQuest Quest, IReadOnlyList<string> StepLines)> _drafted = new();

    // Every class in the active set (Number, Name), enumerated once — the source for
    // each row's "Restrict to classes" checklist.
    private readonly List<(int Number, string Name)> _allClasses = new();

    // The character's current show-anyway opt-ins keyed by (flag, step), seeded from the
    // journal VM's per-character QuestProgress. Read back after Save via each row's
    // ShowIfIneligibleOverride — this VM never writes the profile itself.
    private readonly IReadOnlyDictionary<(int Flag, int Step), bool> _showAnyway;

    // Every crawled quest in crawl order (flag, then band level), editable.
    public ObservableCollection<QuestEditRowViewModel> Quests { get; } = new();

    // The rows the master list shows: Quests narrowed by FilterText. Edits and Save
    // always work on Quests, so a quest filtered out of view keeps its changes.
    public ObservableCollection<QuestEditRowViewModel> VisibleQuests { get; } = new();

    // Typed into the box above the list; matches anywhere in a quest's name or its
    // flag label, ignoring case.
    [ObservableProperty]
    private string _filterText = string.Empty;

    partial void OnFilterTextChanged(string value) => RefreshVisibleQuests();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private QuestEditRowViewModel? _selectedQuest;

    // False when the active set crawls no quests — drives the empty-state hint.
    public bool HasQuests => Quests.Count > 0;

    // True when a quest is selected — gates the detail pane.
    public bool HasSelection => SelectedQuest is not null;

    // gameData: active set, source of the crawl + item names. quests: overlay store
    // the edits persist to. classId / raceId / align*: the current character's identity
    // and alignment-quest opt-ins, so each row can pre-compute whether this character is
    // ineligible (drives the "Show in quest journal" default). classId null = the no-class
    // default profile, which is never class-gated.
    public QuestEditorViewModel(GameDataCache gameData, QuestStore quests, int? classId,
                                int? raceId = null, bool alignGood = false,
                                bool alignNeutral = false, bool alignEvil = false,
                                IReadOnlyDictionary<(int Flag, int Step), bool>? showAnyway = null)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(quests);
        _quests = quests;
        _gameData = gameData;
        _showAnyway = showAnyway ?? new Dictionary<(int, int), bool>();

        Quests.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasQuests));

        // Enumerate the active set's classes once for every row's restriction checklist.
        if (gameData.GetTable("Classes") is { } classDoc)
            foreach (GameRow row in classDoc.Rows)
            {
                if (!row.TryGetProperty("Name", out GameValue n) || n.ValueKind != JsonValueKind.String) continue;
                string? name = n.GetString();
                int number = row.TryGetProperty("Number", out GameValue num)
                    && num.ValueKind == JsonValueKind.Number && num.TryGetInt32(out int v) ? v : 0;
                if (!string.IsNullOrEmpty(name) && number > 0) _allClasses.Add((number, name));
            }

        // Kill / ask-NPC target placement, resolved once for the whole draft pass so a
        // crawled quest's auto-draft step can link to the room the quest places its
        // target / NPC in; and the reverse item-acquisition index so a turn-in /
        // prerequisite step can point at where its item comes from.
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms =
            AppServices.Current?.RoomSearch?.QuestKillRooms();
        ItemSourceIndex? itemSources = AppServices.Current?.ItemSources;
        MonsterDropIndex? itemDrops = AppServices.Current?.MonsterDrops;
        foreach (CrawledQuest q in QuestCrawler.Crawl(gameData, classId))
        {
            QuestDefinition def = quests.Resolve(q.Flag, q.Step);
            bool ineligible = QuestEligibilityResolver.IsIneligible(
                q, classId, raceId, def.ClassRestrict, alignGood, alignNeutral, alignEvil);
            List<string> stepLines = QuestTextFormatter.StepLines(gameData, q, monsterRooms, itemSources, itemDrops).ToList();
            QuestEditRowViewModel row = new(
                q.Flag, q.Step,
                QuestTextFormatter.FallbackTitle(q),
                Draft(q, stepLines),
                QuestTextFormatter.Awards(gameData, q),
                QuestTextFormatter.Bonuses(q.Bonuses),
                QuestTextFormatter.Level(q.RequiredLevel),
                q.RequiredLevel,
                QuestTextFormatter.Requirements(gameData, q),
                def.Name,
                def.Visible,
                def.Steps ?? string.Empty,
                def.Rewards ?? string.Empty,
                def.RequiredLevel,
                ineligible,
                _showAnyway.GetValueOrDefault((q.Flag, q.Step)),
                BuildClassOptions(def.ClassRestrict),
                autoCompleteValue: q.CompleteValue,
                completeValueOverride: def.CompleteValueOverride)
            { Blocked = def.Blocked };
            Quests.Add(row);
            _drafted[row] = (q, stepLines);
        }
        // A later tier's draft opens with the earlier tier's name, which the realm can
        // send while the editor is open (QuestStore.Reloaded).
        _quests.Reloaded += Redraft;

        // User-added quests the crawl never produces, listed after the crawled ones.
        foreach (QuestDefinition def in quests.ManualQuests())
            Quests.Add(BuildManualRow(def));

        // Hooked after the initial fill so the list is built once, not per row.
        Quests.CollectionChanged += (_, _) => RefreshVisibleQuests();
        RefreshVisibleQuests();
    }

    // Rebuild the shown rows. A row isn't re-tested as its name is edited, so the
    // quest being renamed can't drop out of the list mid-keystroke. The selection
    // stays when it is still shown; otherwise the first match takes it.
    private void RefreshVisibleQuests()
    {
        QuestEditRowViewModel? keep = SelectedQuest;
        string filter = FilterText.Trim();
        List<QuestEditRowViewModel> shown = Quests
            .Where(q => filter.Length == 0
                || q.ListLabel.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || q.FallbackLabel.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        VisibleQuests.Clear();
        foreach (QuestEditRowViewModel q in shown) VisibleQuests.Add(q);
        SelectedQuest = keep is not null && shown.Contains(keep) ? keep : shown.FirstOrDefault();
    }

    // Add a blank custom quest at the next free manual flag and select it for editing.
    [RelayCommand]
    private void AddQuest()
    {
        int flag = QuestDefinition.ManualFlagBase;
        foreach (QuestEditRowViewModel row in Quests)
            if (row.IsManual && row.Flag >= flag) flag = row.Flag + 1;

        QuestEditRowViewModel added = BuildManualRow(new QuestDefinition(flag, 0));
        // A blank quest matches no filter: clear it so the new row is in view.
        FilterText = string.Empty;
        Quests.Add(added);
        SelectedQuest = added;
    }

    // Delete the selected manual quest (crawled quests are blocked, not deleted). Keeps a
    // neighbouring row selected so the detail pane doesn't blank out mid-edit.
    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedQuest is not { IsManual: true } row) return;
        int index = VisibleQuests.IndexOf(row);
        Quests.Remove(row);
        SelectedQuest = VisibleQuests.Count == 0 ? null : VisibleQuests[Math.Clamp(index, 0, VisibleQuests.Count - 1)];
    }

    // A master-list row for a manual quest: every crawl-baseline field is empty, so the
    // editable boxes pre-fill straight from the definition and persist verbatim.
    private QuestEditRowViewModel BuildManualRow(QuestDefinition def) =>
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
            requiredLevel: def.RequiredLevel,
            ineligible: false,
            showIfIneligible: _showAnyway.GetValueOrDefault((def.Flag, def.Step)),
            classOptions: BuildClassOptions(def.ClassRestrict),
            autoCompleteValue: null,
            completeValueOverride: def.CompleteValueOverride)
        { Blocked = def.Blocked };

    // A fresh checklist of every class for one row, ticking the ones already in this
    // quest's ClassRestrict. Each row gets its own option instances so their checked
    // state stays independent.
    private IReadOnlyList<ClassRestrictOption> BuildClassOptions(List<int>? restrict)
    {
        HashSet<int>? selected = restrict is { Count: > 0 } ? new HashSet<int>(restrict) : null;
        var options = new List<ClassRestrictOption>(_allClasses.Count);
        foreach ((int Number, string Name) c in _allClasses)
            options.Add(new ClassRestrictOption(c.Number, c.Name, selected?.Contains(c.Number) == true));
        return options;
    }

    [RelayCommand]
    private void Save()
    {
        _quests.Save(Quests.Select(row => row.ToDefinition()));
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    // A crawled quest's drafted steps: the "-Complete <tier> first-" line naming the
    // tier before it, then its own step lines.
    private string Draft(CrawledQuest q, IReadOnlyList<string> stepLines) =>
        string.Join("\n", new[] { QuestTextFormatter.PriorTierLine(_gameData, q, c => QuestTextFormatter.Title(_quests, c)) }
            .OfType<string>()
            .Concat(stepLines));

    private void Redraft()
    {
        foreach ((QuestEditRowViewModel row, (CrawledQuest quest, IReadOnlyList<string> stepLines)) in _drafted)
            row.Redraft(Draft(quest, stepLines));
    }

    // The store outlives the window; the opener lets go once it has closed.
    public void Dispose() => _quests.Reloaded -= Redraft;
}
