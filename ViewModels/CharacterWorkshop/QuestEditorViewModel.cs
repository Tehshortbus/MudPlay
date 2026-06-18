using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Quests;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// Modeless editor for the active set's quest overlay (<c>{set}/quests.json</c>):
/// per quest the user sets a display name, show/hide visibility, and amplifying
/// step markdown over the crawler's auto-draft. Every crawled quest is listed —
/// hidden ones included, so they can be un-hidden. <b>Save</b> writes the overlay
/// as a delta (untouched seed/default rows aren't frozen in) via
/// <see cref="QuestStore.Save"/>; <b>Cancel</b> / title-bar X discard. Standard
/// edit-window contract (CLAUDE.md): Save commits, X / Cancel discards.
/// </summary>
public sealed partial class QuestEditorViewModel : ObservableObject, IDialogViewModel<bool>
{
    public event Action<bool>? CloseRequested;

    private readonly QuestStore _quests;

    /// <summary>Every crawled quest in crawl order (flag, then band level), editable.</summary>
    public ObservableCollection<QuestEditRowViewModel> Quests { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private QuestEditRowViewModel? _selectedQuest;

    /// <summary>False when the active set crawls no quests — drives the empty-state hint.</summary>
    public bool HasQuests => Quests.Count > 0;

    /// <summary>True when a quest is selected — gates the detail pane.</summary>
    public bool HasSelection => SelectedQuest is not null;

    /// <param name="gameData">Active set, source of the crawl + item names.</param>
    /// <param name="quests">Overlay store the edits persist to.</param>
    /// <param name="classId">Character class number for class-resolved bonus labels, or <c>null</c> for the no-class default.</param>
    public QuestEditorViewModel(GameDataCache gameData, QuestStore quests, int? classId)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(quests);
        _quests = quests;

        foreach (CrawledQuest q in QuestCrawler.Crawl(gameData, classId))
        {
            QuestDefinition def = quests.Resolve(q.Flag, q.Step);
            Quests.Add(new QuestEditRowViewModel(
                q.Flag, q.Step,
                QuestTextFormatter.FallbackTitle(q),
                BuildAutoSteps(gameData, q),
                QuestTextFormatter.Bonuses(q.Bonuses),
                QuestTextFormatter.Level(q.RequiredLevel),
                def.Name,
                def.Visible,
                def.Steps ?? string.Empty));
        }

        SelectedQuest = Quests.FirstOrDefault();
    }

    [RelayCommand]
    private void Save()
    {
        _quests.Save(Quests.Select(q => q.ToDefinition()));
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    // Join the crawler's ordered followable steps into a readable block the editor
    // shows beside the user's markdown — single-part quests only (multi-part bands
    // own no per-step graph). Empty string when the crawl drafts nothing.
    private static string BuildAutoSteps(GameDataCache gameData, CrawledQuest q)
    {
        if (q.Step != 0) return string.Empty;
        var lines = new List<string>();
        var seenOrders = new HashSet<int>();
        foreach (QuestStep s in QuestStepGraph.Build(gameData, q.Flag))
        {
            if (!seenOrders.Add(s.Order)) continue;
            lines.Add(QuestTextFormatter.Step(gameData, s));
        }
        return string.Join("\n", lines);
    }
}
