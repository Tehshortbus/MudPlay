using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Quests;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Views.CharacterWorkshop;

namespace MudPlay.ViewModels.CharacterWorkshop;

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
    // The ability a quest awards (its flag) and the level it can be done at, for the
    // quests whose reward is the ability itself — captured like _bonusesByCard.
    private readonly Dictionary<(int Flag, int Step), QuestAbilityAward> _abilityAwardByCard = new();

    public override string Id => "queststatus";
    public override string Title => "Quest Status";
    public override Control View => _view ??= new QuestSectionView { DataContext = this };

    // Visible, class-resolved quest cards after the name filter: incomplete first,
    // completed at the bottom; within each group ordered by required level (low→high),
    // then quest name. Rebuilt from _allCards whenever QuestSearchText changes.
    public ObservableCollection<QuestCardViewModel> Quests { get; } = new();

    // Full sorted card set behind the QuestSearchText filter. Quests is this list
    // narrowed to the search; kept so filtering doesn't re-crawl.
    private readonly List<QuestCardViewModel> _allCards = new();

    // Live name filter for the quest list (the search box left of "Edit Quests…").
    [ObservableProperty] private string _questSearchText = string.Empty;

    partial void OnQuestSearchTextChanged(string value) => ApplyQuestFilter();

    // Top-of-tab toggle: announce "[<quest> Quest is Now Available]" to the terminal as
    // training crosses a quest's min level, plus a full dump of what's available at login.
    // Persisted per character (CharacterProfile.AnnounceAvailableQuests).
    [ObservableProperty] private bool _announceAvailableQuests = true;

    partial void OnAnnounceAvailableQuestsChanged(bool value)
    {
        if (_suppress) return;
        if (_profile.Current is { } p) { p.AnnounceAvailableQuests = value; _profile.Save(); }
    }

    // Top-of-tab alignment-quest checkboxes: which alignment chain(s) this character is
    // committed to. An alignment-gated quest is only shown as doable when its bucket is
    // checked here. Persisted per character; toggling one re-runs eligibility so those
    // quests appear / disappear. All default off — never auto-checked (a player decision).
    [ObservableProperty] private bool _questAlignGood;
    [ObservableProperty] private bool _questAlignNeutral;
    [ObservableProperty] private bool _questAlignEvil;

    partial void OnQuestAlignGoodChanged(bool value) => SaveAlignmentPref(p => p.QuestAlignGood = value);
    partial void OnQuestAlignNeutralChanged(bool value) => SaveAlignmentPref(p => p.QuestAlignNeutral = value);
    partial void OnQuestAlignEvilChanged(bool value) => SaveAlignmentPref(p => p.QuestAlignEvil = value);

    private void SaveAlignmentPref(Action<CharacterProfile> apply)
    {
        if (_suppress) return;
        if (_profile.Current is { } p) { apply(p); _profile.Save(); }
        Rebuild();   // eligibility changed — re-mark "Cannot complete" + re-filter the list
    }

    // False when no visible quest resolves (no set / no character) — drives the empty-state hint.
    [ObservableProperty] private bool _hasQuests;

    // True when quests exist but the current name filter matches none — drives a
    // "no matches" hint distinct from the "load a character" empty state.
    public bool HasNoQuestMatches => HasQuests && Quests.Count == 0;

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
        // The overlay is BBS-tier now, so re-resolve the displayed text when it
        // reloads (BBS change) — a set switch alone no longer implies an overlay swap.
        _quests.Reloaded += Rebuild;
        // The login flag-sync writes completions straight to the profile's QuestLog; reload
        // so an open tab reflects them (and doesn't overwrite them on its next Persist).
        if (AppServices.CurrentOrNull?.QuestFlagSync is { } sync) sync.Synced += OnQuestFlagsSynced;
    }

    private void OnQuestFlagsSynced() => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);

    // ----- build ----------------------------------------------------------

    private void Rebuild()
    {
        _suppress = true;
        try
        {
            AnnounceAvailableQuests = _profile.Current?.AnnounceAvailableQuests ?? true;
            QuestAlignGood = _profile.Current?.QuestAlignGood ?? false;
            QuestAlignNeutral = _profile.Current?.QuestAlignNeutral ?? false;
            QuestAlignEvil = _profile.Current?.QuestAlignEvil ?? false;
            // Item-acquisition indexes, so the draft can add a line fetching an item a
            // step turns in or needs (an NPC or room that gives it, a monster that drops
            // it, a chest) when no earlier step of the quest hands it over.
            ItemSourceIndex? itemSources = AppServices.CurrentOrNull?.ItemSources;
            MonsterDropIndex? itemDrops = AppServices.CurrentOrNull?.MonsterDrops;

            // Ticks saved against an older shape of the drafts are put back on their
            // steps before anything reads them.
            if (_profile.Current is { } loaded
                && QuestTickMigration.Apply(loaded, _gameData, _quests.Resolve, out int moved, itemSources, itemDrops))
            {
                _profile.Save();
                if (moved > 0)
                    AppServices.CurrentOrNull?.Log.Info("Quests",
                        $"Quest step ticks on {moved} drafted checklist(s) moved onto their steps, after the drafts gained lines.");
            }
            _allCards.Clear();
            _bonusesByCard.Clear();
            _abilityAwardByCard.Clear();
            LoadProgressFromProfile();

            // Where each quest-kill / quest-NPC monster stands, resolved once (this loop
            // drafts a step checklist per quest, each kill / ask step consulting the map)
            // so a step can link to the room the quest places its target / NPC in.
            IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms =
                AppServices.Current?.RoomSearch?.QuestKillRooms();

            int? classId = ResolveClassId();
            int? raceId = ResolveRaceId();
            bool alGood = _profile.Current?.QuestAlignGood ?? false;
            bool alNeutral = _profile.Current?.QuestAlignNeutral ?? false;
            bool alEvil = _profile.Current?.QuestAlignEvil ?? false;
            foreach (CrawledQuest q in QuestCrawler.Crawl(_gameData, classId))
            {
                QuestDefinition def = _quests.Resolve(q.Flag, q.Step);
                bool ineligible = QuestEligibilityResolver.IsIneligible(
                    q, classId, raceId, def.ClassRestrict, alGood, alNeutral, alEvil);
                // Blocked = a false positive the user flagged out of the journal. A quest
                // this character can't complete is hidden unless they opted back in per
                // character (QuestProgress.ShowIfIneligible); an eligible one honours the
                // per-taste Visible hide. Either way it stays in the editor, which lists
                // every quest regardless.
                if (def.Blocked) continue;

                // Capture the quest's bonuses for every non-blocked quest — even ones we're
                // about to hide from the journal — so PublishBonuses can still apply a
                // completed quest's permanent bonus when it's out of the list (a quest's
                // stat gain follows its completion, not its journal visibility).
                _bonusesByCard[(q.Flag, q.Step)] = q.Bonuses;
                if (q.AwardsAbility)
                    _abilityAwardByCard[(q.Flag, q.Step)] =
                        new QuestAbilityAward(q.Flag, def.RequiredLevel ?? q.RequiredLevel);

                if (ineligible) { if (!ShowsWhenIneligible(q.Flag, q.Step)) continue; }
                else if (!def.Visible) continue;

                QuestProgress prog = GetOrCreateProgress(q.Flag, q.Step);

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
                    QuestTextFormatter.Title(_quests, q),
                    requiredLevel,
                    QuestTextFormatter.Level(requiredLevel),
                    QuestTextFormatter.Bonuses(q.Bonuses),
                    awardText,
                    // Crawl-derived exp reward, shown straight (no user override): a reward
                    // override edits the item/ability award line, not the raw give-chain exp.
                    QuestTextFormatter.Experience(q),
                    QuestTextFormatter.Requirements(_gameData, q),
                    ineligible,
                    prog.Complete,
                    steps,
                    OnCardCompletionChanged);

                // Every quest carries a followable checklist (multi-part bands too,
                // filtered to the band's give-step range by the crawl + StepLines).
                PopulateSteps(card, q, def, prog, monsterRooms, itemSources, itemDrops);

                _allCards.Add(card);
            }

            // User-added quests the crawl never produces — self-contained from the overlay.
            foreach (QuestDefinition def in _quests.ManualQuests())
                if (BuildManualCard(def) is { } card)
                    _allCards.Add(card);

            HasQuests = _allCards.Count > 0;
            SortQuests();   // sorts _allCards, then rebuilds the filtered Quests view
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
        _allCards.Sort(static (a, b) =>
        {
            int c = a.IsComplete.CompareTo(b.IsComplete);   // incomplete (false) first
            if (c != 0) return c;
            c = a.RequiredLevel.CompareTo(b.RequiredLevel);
            if (c != 0) return c;
            return string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
        });
        ApplyQuestFilter();
    }

    // Rebuild the visible Quests collection from the sorted _allCards, keeping only
    // cards whose name contains the QuestSearchText (case-insensitive; empty = all).
    private void ApplyQuestFilter()
    {
        string filter = (QuestSearchText ?? string.Empty).Trim();
        Quests.Clear();
        foreach (QuestCardViewModel card in _allCards)
            if (filter.Length == 0
                || card.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                Quests.Add(card);
        OnPropertyChanged(nameof(HasNoQuestMatches));
    }

    // Build the followable checklist from the quest's step markdown: the user-edited
    // override when present, else the crawler's auto-draft (band-filtered). Each
    // `[]`-marked line is a tickable step keyed by its 0-based checkbox index (the
    // CheckedSteps key — stable across edits to the surrounding prose); a plain line
    // is a context label. A step is pre-ticked when its index is in saved progress.
    //
    // On the auto-draft, a live flag read (QuestProgress.FlagValue) also ticks every step
    // whose give-step order it has reached — the game says those are done. A user-edited
    // checklist has no step orders to match, so it keeps only its own ticks.
    private void PopulateSteps(QuestCardViewModel card, CrawledQuest q, QuestDefinition def, QuestProgress prog,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms, ItemSourceIndex? itemSources, MonsterDropIndex? itemDrops)
    {
        if (!string.IsNullOrWhiteSpace(def.Steps))
        {
            AddStepRows(card, def.Steps!, prog);
            return;
        }
        IReadOnlyList<(int Order, string Line)> entries =
            QuestTextFormatter.StepEntries(_gameData, q, monsterRooms, itemSources, itemDrops);
        // Each auto-draft line is one checkbox, so its index is the CheckedSteps key.
        HashSet<int>? provenDone = prog.FlagValue is int value
            ? entries.Select((e, i) => (e.Order, i)).Where(x => x.Order <= value).Select(x => x.i).ToHashSet()
            : null;
        // A later tier opens with a plain "-Complete <tier> first-" line, which is no
        // checkbox, so the checkbox indices above still line up.
        string? prior = QuestTextFormatter.PriorTierLine(_gameData, q, c => QuestTextFormatter.Title(_quests, c));
        AddStepRows(card, string.Join("\n", new[] { prior }.OfType<string>().Concat(entries.Select(e => e.Line))),
            prog, provenDone);
    }

    // Materialize a quest's step markdown into tickable/label rows on the card. Shared by
    // crawled quests (auto-draft or edited override) and manual quests (override only).
    // provenDone: checkbox indices a live flag read shows done, ticked on top of saved progress.
    private void AddStepRows(QuestCardViewModel card, string text, QuestProgress prog,
        IReadOnlySet<int>? provenDone = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        int checkIndex = 0;
        foreach ((bool checkable, string display) in QuestTextFormatter.ParseStepLines(text))
        {
            int order = checkable ? checkIndex++ : -1;
            bool isChecked = checkable
                && (prog.CheckedSteps?.Contains(order) == true || provenDone?.Contains(order) == true);
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
    // a walk-there command and each quoted command (QuestTextFormatter.SplitStepLinks) in a send-to-game
    // command. Plain runs carry no command; only the tokens are clickable.
    private static IReadOnlyList<QuestStepSegmentViewModel> BuildSegments(string display)
    {
        var segments = new List<QuestStepSegmentViewModel>();
        foreach ((string segText, RoomKey? room, string? command) in QuestTextFormatter.SplitStepLinks(display))
        {
            if (room is { } key)
                segments.Add(new QuestStepSegmentViewModel(
                    segText, new AsyncRelayCommand(() => WalkToAsync(key)), QuestStepLinkKind.Walk));
            else if (command is { } cmd)
                segments.Add(new QuestStepSegmentViewModel(
                    segText, new RelayCommand(() => SendStepCommand(cmd)), QuestStepLinkKind.Send));
            else
                segments.Add(new QuestStepSegmentViewModel(segText));
        }
        return segments;
    }

    // Walk to a quest step's map/room. Stop whatever movement engine is running
    // first, then hand off to the shared route picker — the same entry point every
    // other user-started walk uses. A straight Walker.WalkTo skipped route planning,
    // so a room past a hazard just failed "blocked" instead of offering the obtain-
    // the-counter / cross-unprotected routes.
    private static async Task WalkToAsync(RoomKey room)
    {
        // Before the Stop: a run frozen by the master switch is not given up for
        // a walk that won't start.
        if (AppServices.Current.RefuseStartForMasterSwitch("Walk")) return;
        AppServices.Current.MovementControl.Stop();
        await Navigation.RouteChoicePrompt.WalkAsync(AppServices.Current, room);
    }

    // Send a quest-step's quoted command as the user's own typed line (SendTypedInput):
    // macro separators split it, aliases expand, and the outbound observers all run.
    private static void SendStepCommand(string command)
        => AppServices.Current.SendTypedInput(command);

    // ----- editor ---------------------------------------------------------

    // Open the modeless Quest editor (name / show-hide / step markdown). The async
    // command auto-disables while open, so a second click can't stack a window. On
    // Save the overlay changed under us — re-crawl so renames / visibility apply.
    [RelayCommand]
    private async Task EditQuests()
    {
        // Seed the editor with this character's current show-anyway opt-ins (per-quest),
        // so a "Cannot complete" quest's "Show in quest journal" checkbox reflects the
        // character's saved choice rather than the board's.
        Dictionary<(int Flag, int Step), bool> showAnyway = _progress.Values
            .Where(p => p.ShowIfIneligible)
            .ToDictionary(p => (p.Flag, p.Step), _ => true);

        var editor = new QuestEditorViewModel(
            _gameData, _quests, ResolveClassId(), ResolveRaceId(),
            _profile.Current?.QuestAlignGood ?? false,
            _profile.Current?.QuestAlignNeutral ?? false,
            _profile.Current?.QuestAlignEvil ?? false,
            showAnyway);
        bool? saved;
        try { saved = await AppServices.Current.Dialogs.OpenWindowAsync<QuestEditorViewModel, bool>(editor); }
        finally { editor.Dispose(); }
        if (saved != true) return;

        // The show-anyway opt-in is per character, so it lands on QuestProgress here (the
        // sole QuestLog writer) rather than through QuestStore's per-set overlay. Only
        // ineligible rows carry a meaningful value; write it (incl. clearing to false).
        foreach (QuestEditRowViewModel row in editor.Quests)
            if (row.IsIneligible)
                GetOrCreateProgress(row.Flag, row.Step).ShowIfIneligible = row.ShowIfIneligibleOverride;

        Persist();
        Rebuild();
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

    // Flatten every completed quest's class-resolved bonuses (quests stack, so no dedup)
    // and hand them to the shared state the Character Info tab reads. Keyed off the
    // per-character completion records, not the visible cards, so a completed quest's
    // permanent bonus still applies when the quest is hidden from the journal (search
    // filter, per-taste hide, or the cannot-complete auto-hide) — its stat gain is a
    // function of completion, not visibility. _bonusesByCard holds every non-blocked
    // crawled quest this set produced (captured before any hide).
    private void PublishBonuses()
    {
        var bonuses = new List<QuestBonus>();
        var awards = new List<QuestAbilityAward>();
        foreach (QuestProgress p in _progress.Values)
        {
            if (!p.Complete) continue;
            if (_bonusesByCard.TryGetValue((p.Flag, p.Step), out IReadOnlyList<QuestBonus>? b))
                bonuses.AddRange(b);
            if (_abilityAwardByCard.TryGetValue((p.Flag, p.Step), out QuestAbilityAward award))
                awards.Add(award);
        }
        _bonusState.Update(bonuses, awards);
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

    // A record is worth persisting only when it carries completion, step progress, or the
    // show-a-cannot-complete-quest opt-in; empty drafts are dropped so the log stays a
    // delta, not a full crawl mirror.
    private static bool IsMeaningful(QuestProgress p) =>
        p.Complete || p.CheckedSteps is { Count: > 0 } || p.ShowIfIneligible;

    // Whether the character opted to keep this cannot-complete quest in the journal —
    // a read against the hydrated per-character progress (no entry created for a quest
    // that's staying hidden).
    private bool ShowsWhenIneligible(int flag, int step) =>
        _progress.TryGetValue((flag, step), out QuestProgress? p) && p.ShowIfIneligible;

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

    private static int GetInt(GameRow? row, string property)
    {
        if (row is not GameRow el) return 0;
        if (!el.TryGetProperty(property, out GameValue v)) return 0;
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
        _quests.Reloaded -= Rebuild;
        if (AppServices.CurrentOrNull?.QuestFlagSync is { } sync) sync.Synced -= OnQuestFlagsSynced;
    }
}
