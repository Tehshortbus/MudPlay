using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game;
using FujinTerm.Game.Map;
using FujinTerm.Game.Quests;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.CharacterWorkshop;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// QUEST STATUS section — the per-character quest checklist. Crawls every quest in
// the active set (QuestCrawler), resolves each to the character's class for
// class-branched bonuses, and lists the visible ones (QuestStore owns names +
// show/hide). Each card carries the quest's level gate, class-resolved bonus + award
// labels, a manual Complete checkbox, and a followable step checklist parsed from the
// quest's step markdown — the user's edited override (QuestStore) when present, else
// the crawler's auto-draft (QuestStepGraph, band-filtered per the crawl). Completion
// is one-way: ticking the checkbox, or ticking every step, marks the quest done; both
// states persist per character in CharacterProfile.QuestLog. The union of every
// completed quest's bonus is published to the shared QuestBonusState, which the
// Character Info tab folds into its derived combat + a Quest Bonuses readout.
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

    // Visible, class-resolved quest cards: incomplete first, completed at the bottom;
    // within each group ordered by required level (low→high), then quest name.
    public ObservableCollection<QuestCardViewModel> Quests { get; } = new();

    // False when no visible quest resolves (no set / no character) — drives the empty-state hint.
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
            int? raceId = ResolveRaceId();
            foreach (CrawledQuest q in QuestCrawler.Crawl(_gameData, classId))
            {
                QuestDefinition def = _quests.Resolve(q.Flag, q.Step);
                // Blocked = a false positive the user flagged out of the journal; hidden =
                // a per-taste hide. Either keeps the card out of the list (both un-doable
                // in the editor, which lists every quest regardless).
                if (def.Blocked || !def.Visible) continue;

                QuestProgress prog = GetOrCreateProgress(q.Flag, q.Step);
                _bonusesByCard[(q.Flag, q.Step)] = q.Bonuses;

                // The user's reward override (set in the editor) wins over the crawler's
                // inferred award — it's how awards the give-chain crawl can't see (e.g.
                // 5th-tier alignment weapons from per-class random textblocks) get shown.
                string awardText = !string.IsNullOrWhiteSpace(def.Rewards)
                    ? def.Rewards!
                    : QuestTextFormatter.Awards(_gameData, q);

                // Same override-wins rule for the level gate: a user-set requirement
                // (the editor's spinner) covers gates the crawl misses, else the crawl.
                int requiredLevel = def.RequiredLevel ?? q.RequiredLevel;

                var steps = new ObservableCollection<QuestStepRowViewModel>();
                var card = new QuestCardViewModel(
                    q.Flag, q.Step,
                    ResolveTitle(def, q),
                    requiredLevel,
                    QuestTextFormatter.Level(requiredLevel),
                    QuestTextFormatter.Bonuses(q.Bonuses),
                    awardText,
                    // Crawl-derived exp reward, shown straight (no user override): a reward
                    // override edits the item/ability award line, not the raw give-chain exp.
                    QuestTextFormatter.Experience(q),
                    QuestTextFormatter.Requirements(_gameData, q),
                    IsIneligible(q, classId, raceId),
                    prog.Complete,
                    steps,
                    OnCardCompletionChanged);

                // Every quest carries a followable checklist (multi-part bands too,
                // filtered to the band's give-step range by the crawl + StepLines).
                PopulateSteps(card, q, def, prog);

                Quests.Add(card);
            }

            // User-added quests the crawl never produces — self-contained from the overlay.
            foreach (QuestDefinition def in _quests.ManualQuests())
                if (BuildManualCard(def) is { } card)
                    Quests.Add(card);

            SortQuests();
            HasQuests = Quests.Count > 0;
        }
        finally { _suppress = false; }

        PublishBonuses();
    }

    // Order the Quest Status list: incomplete quests first, completed sink to the
    // bottom; within each group, required level low→high, ties broken alphabetically
    // by the user-facing quest name (case-insensitive). Re-runs live on a completion
    // toggle so a card slides to its group as it's checked, not only on rebuild.
    private void SortQuests()
    {
        List<QuestCardViewModel> sorted = Quests
            .OrderBy(c => c.IsComplete)
            .ThenBy(c => c.RequiredLevel)
            .ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int current = Quests.IndexOf(sorted[i]);
            if (current != i) Quests.Move(current, i);
        }
    }

    // Build the followable checklist from the quest's step markdown: the user-edited
    // override when present, else the crawler's auto-draft (band-filtered). Each
    // `[]`-marked line is a tickable step keyed by its 0-based checkbox index (the
    // CheckedSteps key — stable across edits to the surrounding prose); a plain line
    // is a context label. A step is pre-ticked when its index is in saved progress.
    private void PopulateSteps(QuestCardViewModel card, CrawledQuest q, QuestDefinition def, QuestProgress prog)
    {
        string text = !string.IsNullOrWhiteSpace(def.Steps)
            ? def.Steps!
            : string.Join("\n", QuestTextFormatter.StepLines(_gameData, q));
        AddStepRows(card, text, prog);
    }

    // Materialize a quest's step markdown into tickable/label rows on the card. Shared by
    // crawled quests (auto-draft or edited override) and manual quests (override only).
    private void AddStepRows(QuestCardViewModel card, string text, QuestProgress prog)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        int checkIndex = 0;
        foreach ((bool checkable, string display) in QuestTextFormatter.ParseStepLines(text))
        {
            int order = checkable ? checkIndex++ : -1;
            bool isChecked = checkable && prog.CheckedSteps?.Contains(order) == true;
            card.Steps.Add(new QuestStepRowViewModel(order, BuildSegments(display), isChecked, checkable, row => OnStepToggled(card, row)));
        }
    }

    // Build a card for a user-added quest (null when it's blocked / hidden). It has no
    // crawl backing, so every field comes from the definition: no class-resolved bonus,
    // no crawled requirements, the user's reward label as the award, and the user's step
    // markdown as the checklist.
    private QuestCardViewModel? BuildManualCard(QuestDefinition def)
    {
        if (def.Blocked || !def.Visible) return null;

        QuestProgress prog = GetOrCreateProgress(def.Flag, def.Step);
        _bonusesByCard[(def.Flag, def.Step)] = Array.Empty<QuestBonus>();

        var steps = new ObservableCollection<QuestStepRowViewModel>();
        var card = new QuestCardViewModel(
            def.Flag, def.Step,
            string.IsNullOrWhiteSpace(def.Name) ? "(unnamed quest)" : def.Name,
            def.RequiredLevel ?? 0,
            QuestTextFormatter.Level(def.RequiredLevel ?? 0),
            string.Empty,
            def.Rewards ?? string.Empty,
            string.Empty,   // manual quests carry no crawled exp reward
            string.Empty,
            ineligible: false,
            prog.Complete,
            steps,
            OnCardCompletionChanged);

        AddStepRows(card, def.Steps ?? string.Empty, prog);
        return card;
    }

    // Split a step label into render runs, wrapping each `(map/room)` coordinate in
    // a command that walks there. A plain run carries no command; only the
    // coordinate run is clickable.
    private static IReadOnlyList<QuestStepSegmentViewModel> BuildSegments(string display)
    {
        var segments = new List<QuestStepSegmentViewModel>();
        foreach ((string segText, RoomKey? room) in QuestTextFormatter.SplitRoomLinks(display))
            segments.Add(room is { } key
                ? new QuestStepSegmentViewModel(segText, new RelayCommand(() => WalkTo(key)))
                : new QuestStepSegmentViewModel(segText));
        return segments;
    }

    // Walk to a quest step's map/room. Stop whatever movement engine is running
    // first (mirrors the Navigation window's walk-to hand-off), then dispatch to
    // the walker; a destination outside the active graph surfaces via the walker's
    // own WalkEvent.Failed path, so no pre-check is needed here.
    private static void WalkTo(RoomKey room)
    {
        AppServices.Current.MovementControl.Stop();
        AppServices.Current.Walker.WalkTo(room);
    }

    // ----- editor ---------------------------------------------------------

    // Open the modeless Quest editor (name / show-hide / step markdown). The async
    // command auto-disables while open, so a second click can't stack a window. On
    // Save the overlay changed under us — re-crawl so renames / visibility apply.
    [RelayCommand]
    private async Task EditQuests()
    {
        var editor = new QuestEditorViewModel(_gameData, _quests, ResolveClassId());
        bool? saved = await AppServices.Current.Dialogs
            .OpenWindowAsync<QuestEditorViewModel, bool>(editor);
        if (saved == true) Rebuild();
    }

    // ----- toggle handlers ------------------------------------------------

    // The manual Complete checkbox flipped: mirror it into the progress record and
    // persist. (Programmatic forces during step-toggle are suppressed.)
    private void OnCardCompletionChanged(QuestCardViewModel card)
    {
        if (_suppress) return;
        QuestProgress prog = GetOrCreateProgress(card.Flag, card.Step);
        prog.Complete = card.IsComplete;
        SortQuests();
        Persist();
        PublishBonuses();
    }

    // A step ticked / unticked: capture the new checked-index set (checkable rows
    // only — plain label rows never count), and when every checkable step is ticked
    // force the card complete (one-way — unticking never auto-clears).
    private void OnStepToggled(QuestCardViewModel card, QuestStepRowViewModel _)
    {
        if (_suppress) return;
        QuestProgress prog = GetOrCreateProgress(card.Flag, card.Step);

        List<QuestStepRowViewModel> checkable = card.Steps.Where(s => s.IsCheckable).ToList();
        List<int> checkedOrders = checkable.Where(s => s.IsChecked).Select(s => s.Order).ToList();
        prog.CheckedSteps = checkedOrders.Count == 0 ? null : checkedOrders;

        if (checkable.Count > 0 && checkable.All(s => s.IsChecked) && !card.IsComplete)
        {
            _suppress = true;
            try { card.IsComplete = true; }
            finally { _suppress = false; }
            prog.Complete = true;
            SortQuests();
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

    private int? ResolveRaceId()
    {
        if (string.IsNullOrEmpty(_stats.Race)) return null;
        int num = GetInt(_gameData.FindRowByName("Races", _stats.Race), "Number");
        return num > 0 ? num : null;
    }

    // True when the character's known class or race is excluded from this quest's crawled
    // restriction set — a provable "can't take it" (a Warrior viewing Meditate, a Human
    // viewing the Gaunt-One SeeHidden quest). An unknown class/race never excludes: the
    // restriction sets are conservative, so we only flag what the crawl proves shut.
    private static bool IsIneligible(CrawledQuest q, int? classId, int? raceId)
    {
        if (q.ClassIds is { Count: > 0 } cls && classId is int c && !cls.Contains(c)) return true;
        if (q.RaceIds is { Count: > 0 } rcs && raceId is int r && !rcs.Contains(r)) return true;
        return false;
    }

    private static string ResolveTitle(QuestDefinition def, CrawledQuest q) =>
        !string.IsNullOrWhiteSpace(def.Name) ? def.Name : QuestTextFormatter.FallbackTitle(q);

    private static int GetInt(JsonElement? row, string property)
    {
        if (row is not JsonElement el || el.ValueKind != JsonValueKind.Object) return 0;
        if (!el.TryGetProperty(property, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }

    // ----- events ---------------------------------------------------------

    // Class drives the bonus / award resolution baked into each card, and class + race
    // drive the "Cannot complete" eligibility badge; all change only on a `stat` re-parse
    // (or character swap) — rebuild on those, not on every HP/level tick.
    private void OnStatsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerStats.Class) or nameof(PlayerStats.Race)) Rebuild();
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
