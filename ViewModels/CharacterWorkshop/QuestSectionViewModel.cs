using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game;
using FujinTerm.Game.GameData;
using FujinTerm.Game.Quests;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.CharacterWorkshop;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// QUEST STATUS section — the per-character quest checklist. Crawls every quest in
/// the active set (<see cref="QuestCrawler"/>), resolves each to the character's
/// class for class-branched bonuses, and lists the visible ones
/// (<see cref="QuestStore"/> owns names + show/hide). Each card carries the quest's
/// level gate, class-resolved bonus + award labels, a manual Complete checkbox, and
/// — for a single-part quest — its followable step checklist
/// (<see cref="QuestStepGraph"/>). Completion is one-way: ticking the checkbox, or
/// ticking every step, marks the quest done; both states persist per character in
/// <see cref="CharacterProfile.QuestLog"/>. The union of every completed quest's
/// bonus is published to the shared <see cref="QuestBonusState"/>, which the
/// Character Info tab folds into its derived combat + a Quest Bonuses readout.
/// </summary>
public sealed partial class QuestSectionViewModel : WorkshopSectionViewModel
{
    private readonly PlayerStats _stats;
    private readonly GameDataCache _gameData;
    private readonly ProfileService _profile;
    private readonly QuestStore _quests;
    private readonly QuestBonusState _bonusState;
    private Control? _view;

    // Re-entrancy guard: suppresses card/step callbacks while we hydrate or
    // programmatically force a card complete (those aren't user edits).
    private bool _suppress;

    // Live completion keyed by (flag, step); hydrated from the profile, written back
    // on every toggle. Survives a card rebuild so unsaved progress isn't lost.
    private readonly Dictionary<(int Flag, int Step), QuestProgress> _progress = new();

    // Class-resolved crawl bonuses per card, captured at build time so PublishBonuses
    // can fold a completed quest's reward without re-crawling.
    private readonly Dictionary<(int Flag, int Step), IReadOnlyList<QuestBonus>> _bonusesByCard = new();

    public override string Id => "queststatus";
    public override string Title => "Quest Status";
    public override Control View => _view ??= new QuestSectionView { DataContext = this };

    /// <summary>Visible, class-resolved quest cards in crawl order (flag, then band level).</summary>
    public ObservableCollection<QuestCardViewModel> Quests { get; } = new();

    /// <summary>False when no visible quest resolves (no set / no character) — drives the empty-state hint.</summary>
    [ObservableProperty] private bool _hasQuests;

    public QuestSectionViewModel(PlayerStats stats, GameDataCache gameData,
                                 ProfileService profile, QuestStore quests, QuestBonusState bonusState)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(quests);
        ArgumentNullException.ThrowIfNull(bonusState);
        _stats = stats;
        _gameData = gameData;
        _profile = profile;
        _quests = quests;
        _bonusState = bonusState;

        Rebuild();

        _stats.PropertyChanged += OnStatsChanged;
        _profile.ProfileLoaded += OnProfileLoaded;
        _gameData.ActiveSetChanged += OnActiveSetChanged;
    }

    // ----- build ----------------------------------------------------------

    private void Rebuild()
    {
        _suppress = true;
        try
        {
            Quests.Clear();
            _bonusesByCard.Clear();
            LoadProgressFromProfile();

            int? classId = ResolveClassId();
            foreach (CrawledQuest q in QuestCrawler.Crawl(_gameData, classId))
            {
                QuestDefinition def = _quests.Resolve(q.Flag, q.Step);
                if (!def.Visible) continue;

                QuestProgress prog = GetOrCreateProgress(q.Flag, q.Step);
                _bonusesByCard[(q.Flag, q.Step)] = q.Bonuses;

                var steps = new ObservableCollection<QuestStepRowViewModel>();
                var card = new QuestCardViewModel(
                    q.Flag, q.Step,
                    ResolveTitle(def, q),
                    FormatLevel(q.RequiredLevel),
                    FormatBonuses(q.Bonuses),
                    FormatAwards(q.AwardItems),
                    prog.Complete,
                    steps,
                    OnCardCompletionChanged);

                // Single-part quests carry a followable step checklist; multi-part
                // bands are manual-complete only (the crawler owns band membership).
                if (q.Step == 0)
                    PopulateSteps(card, q.Flag, prog);

                Quests.Add(card);
            }

            HasQuests = Quests.Count > 0;
        }
        finally { _suppress = false; }

        PublishBonuses();
    }

    // One row per distinct give-step order (CheckedSteps is keyed by order), in
    // crawl order. A row is pre-ticked when its order is in the saved progress.
    private void PopulateSteps(QuestCardViewModel card, int flag, QuestProgress prog)
    {
        var seenOrders = new HashSet<int>();
        foreach (QuestStep s in QuestStepGraph.Build(_gameData, flag))
        {
            if (!seenOrders.Add(s.Order)) continue;
            bool isChecked = prog.CheckedSteps?.Contains(s.Order) == true;
            card.Steps.Add(new QuestStepRowViewModel(
                s.Order, FormatStep(s), isChecked, row => OnStepToggled(card, row)));
        }
    }

    // ----- toggle handlers ------------------------------------------------

    // The manual Complete checkbox flipped: mirror it into the progress record and
    // persist. (Programmatic forces during step-toggle are suppressed.)
    private void OnCardCompletionChanged(QuestCardViewModel card)
    {
        if (_suppress) return;
        QuestProgress prog = GetOrCreateProgress(card.Flag, card.Step);
        prog.Complete = card.IsComplete;
        Persist();
        PublishBonuses();
    }

    // A step ticked / unticked: capture the new checked-order set, and when every
    // step is ticked force the card complete (one-way — unticking never auto-clears).
    private void OnStepToggled(QuestCardViewModel card, QuestStepRowViewModel _)
    {
        if (_suppress) return;
        QuestProgress prog = GetOrCreateProgress(card.Flag, card.Step);

        List<int> checkedOrders = card.Steps.Where(s => s.IsChecked).Select(s => s.Order).ToList();
        prog.CheckedSteps = checkedOrders.Count == 0 ? null : checkedOrders;

        if (card.Steps.Count > 0 && card.Steps.All(s => s.IsChecked) && !card.IsComplete)
        {
            _suppress = true;
            try { card.IsComplete = true; }
            finally { _suppress = false; }
            prog.Complete = true;
        }

        Persist();
        PublishBonuses();
    }

    // ----- publish + persist ----------------------------------------------

    // Flatten every completed card's class-resolved bonuses (quests stack, so no
    // dedup) and hand them to the shared state the Character Info tab reads.
    private void PublishBonuses()
    {
        var bonuses = new List<QuestBonus>();
        foreach (QuestCardViewModel card in Quests)
        {
            if (!card.IsComplete) continue;
            if (_bonusesByCard.TryGetValue((card.Flag, card.Step), out IReadOnlyList<QuestBonus>? b))
                bonuses.AddRange(b);
        }
        _bonusState.Update(bonuses);
    }

    private void Persist()
    {
        if (_profile.Current is not { } p) return;
        List<QuestProgress> log = _progress.Values
            .Where(IsMeaningful)
            .OrderBy(e => e.Flag).ThenBy(e => e.Step)
            .ToList();
        p.QuestLog = log.Count == 0 ? null : log;
        _profile.Save();
    }

    // A record is worth persisting only when it carries completion or step progress;
    // empty drafts are dropped so the log stays a delta, not a full crawl mirror.
    private static bool IsMeaningful(QuestProgress p) => p.Complete || p.CheckedSteps is { Count: > 0 };

    // ----- helpers --------------------------------------------------------

    private QuestProgress GetOrCreateProgress(int flag, int step)
    {
        (int Flag, int Step) key = (flag, step);
        if (!_progress.TryGetValue(key, out QuestProgress? p))
        {
            p = new QuestProgress(flag, step);
            _progress[key] = p;
        }
        return p;
    }

    private void LoadProgressFromProfile()
    {
        _progress.Clear();
        if (_profile.Current?.QuestLog is not { } log) return;
        foreach (QuestProgress p in log)
            _progress[(p.Flag, p.Step)] = p;
    }

    private int? ResolveClassId()
    {
        if (string.IsNullOrEmpty(_stats.Class)) return null;
        int num = GetInt(_gameData.FindRowByName("Classes", _stats.Class), "Number");
        return num > 0 ? num : null;
    }

    private static string ResolveTitle(QuestDefinition def, CrawledQuest q)
    {
        if (!string.IsNullOrWhiteSpace(def.Name)) return def.Name;
        string flagName = AbilityNames.FormatId(q.Flag);
        return q.Step > 0 ? $"{flagName} (Lv {q.Step})" : flagName;
    }

    private static string FormatLevel(int level) =>
        level > 0 ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Level {level}") : string.Empty;

    private static string FormatBonuses(IReadOnlyList<QuestBonus> bonuses) =>
        bonuses.Count == 0 ? string.Empty
            : AbilityNames.SummarizeAbilities(bonuses.Select(b => (b.AbilityId, b.Value)));

    private string FormatAwards(IReadOnlyList<int> awardItems) =>
        awardItems.Count == 0 ? string.Empty : string.Join(", ", awardItems.Select(ItemName));

    private string FormatStep(QuestStep s)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.Command)) parts.Add(s.Command!);
        if (!string.IsNullOrWhiteSpace(s.Location)) parts.Add($"@ {s.Location}");
        if (s.TurnInItems.Count > 0) parts.Add("turn in " + string.Join(", ", s.TurnInItems.Select(ItemName)));
        if (s.RequiredItems.Count > 0) parts.Add("need " + string.Join(", ", s.RequiredItems.Select(ItemName)));
        if (s.GrantedItems.Count > 0) parts.Add("receive " + string.Join(", ", s.GrantedItems.Select(ItemName)));
        return parts.Count > 0
            ? string.Join("  ·  ", parts)
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Step {s.Order}");
    }

    private string ItemName(int id) =>
        _gameData.FindNameByNumber("Items", id)
        ?? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"#{id}");

    private static int GetInt(JsonElement? row, string property)
    {
        if (row is not JsonElement el || el.ValueKind != JsonValueKind.Object) return 0;
        if (!el.TryGetProperty(property, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }

    // ----- events ---------------------------------------------------------

    // Class drives bonus resolution and only changes on a `stat` re-parse (or
    // character swap) — rebuild on that, not on every HP/level tick.
    private void OnStatsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerStats.Class)) Rebuild();
    }

    private void OnProfileLoaded(CharacterProfile _) => Rebuild();
    private void OnActiveSetChanged(string? _) => Rebuild();

    public override void Dispose()
    {
        _stats.PropertyChanged -= OnStatsChanged;
        _profile.ProfileLoaded -= OnProfileLoaded;
        _gameData.ActiveSetChanged -= OnActiveSetChanged;
    }
}
