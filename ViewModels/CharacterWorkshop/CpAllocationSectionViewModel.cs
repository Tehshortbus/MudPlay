using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Views.CharacterWorkshop;

namespace MudPlay.ViewModels.CharacterWorkshop;

// CP ALLOCATION section — the editable per-level character-point plan. The
// baseline is the live raw-base stats (the `stat` screen's values with whatever
// it marked as modified worked back out, and a note when that can't be done);
// each grid row is a planned future level whose target STR/INT/WIL/AGL/HEA/CHM
// the user edits, with Total CP earned / CP Left recomputed live via
// CpPlanCalculator (race-min cost curve, race-max clamp, cumulative carryover).
// A target that would overspend is auto-trimmed at the just-edited cell so CP
// Left never goes negative. The clamped plan is published to the shared
// CpPlanState (so the Level Projection tab reflects the planned stat increases
// per level) and persists to the profile's CharacterProfile.CharacterPlan,
// driving auto-train and the @train remote command.
public sealed partial class CpAllocationSectionViewModel : WorkshopSectionViewModel
{
    private readonly PlayerStats _stats;
    private readonly GameDataCache _gameData;
    private readonly InventoryManager _inventory;
    private readonly ProfileService _profile;
    private readonly CpPlanState _planState;
    private readonly TrainerWalkManager _trainerWalk;
    private readonly AutoTrainManager _autoTrain;
    private readonly Game.Spells.ListedEffectCatalog _listedEffects;
    private Control? _view;
    private bool _suppress;
    // The cell most recently edited by the user, so an overspend trims that cell
    // (not an unrelated stat). Null for structural / baseline-driven recalcs.
    private CpStat? _lastEditedStat;

    public override string Id => "cpallocation";
    public override string Title => "CP Allocation";
    public override Control View => _view ??= new CpAllocationSectionView { DataContext = this };

    public ObservableCollection<CpPlanRowViewModel> Rows { get; } = new();

    // Current unspent CP, seeded as the plan's starting balance (not displayed).
    [ObservableProperty] private int _unspentCp;
    // False when no race/class resolves (no character / game data) — gates the grid.
    [ObservableProperty] private bool _hasCharacter;

    // The grid row the user has selected — drives RemoveRowCommand.
    [ObservableProperty] private CpPlanRowViewModel? _selectedRow;

    // Per-stat column mouseover text for the grid headers: realm-aware "~N → +1 X"
    // ratios plus the next value of that stat (from the live character's current
    // base value) that ticks each derived stat up — so the user can spend CP to a
    // real breakpoint instead of guessing every 5th / 10th point. Built from
    // StatEffects (the same math the combat engine + Level Projection use).
    [ObservableProperty] private string _strStatTip = string.Empty;
    [ObservableProperty] private string _intStatTip = string.Empty;
    [ObservableProperty] private string _wilStatTip = string.Empty;
    [ObservableProperty] private string _agiStatTip = string.Empty;
    [ObservableProperty] private string _heaStatTip = string.Empty;
    [ObservableProperty] private string _chmStatTip = string.Empty;

    // ----- consolidated status line --------------------------------------------
    // One notice area (right of the action buttons) for every transient/standing
    // message this tab emits, so "can train now", "training…", apply results and
    // row-edit errors never light up in three separate spots at once. Precedence,
    // most-urgent first: an in-flight train, then the last action result, then the
    // passive can-train hint — StatusText surfaces exactly one.
    // True when the saved plan has an affordable raise to apply at the current level.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasStatusText))]
    private bool _canTrainNow;
    // True while a train run is in flight (either engine) — disables the buttons.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasStatusText))]
    private bool _autoTrainBusy;
    // Last user-action message: apply result, CP-fit refusal, or a row-edit error.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasStatusText))]
    private string? _actionMessage;

    // True while a Buy spells run is in flight: the busy notice names it.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _buyingSpells;

    public string? StatusText =>
        AutoTrainBusy ? (BuyingSpells ? "Buying spells…" : "Training…")
        : !string.IsNullOrEmpty(ActionMessage) ? ActionMessage
        : CanTrainNow ? "● Can train now"
        : null;

    public bool HasStatusText => !string.IsNullOrEmpty(StatusText);

    // What the last `stat` screen marked as modified and how the baseline dealt
    // with it. Null when it marked nothing.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBaselineNote))]
    private string? _baselineNote;

    public bool HasBaselineNote => !string.IsNullOrEmpty(BaselineNote);

    // Captured on RefreshBaseline; inputs to the recalc.
    private CpPlanEntry _baseline = new();
    private CpPlanEntry _raceMin = new();
    private CpPlanEntry _raceMax = new();
    private RealmType _realm;
    // The baseline may be acted on: rows rewritten against it and pruned by it.
    private bool _baselineTrusted;
    // The last `stat` marked a stat the baseline couldn't work back. Narrower than
    // "not trusted": a screen whose marks couldn't be read at all says nothing
    // either way, and a realm that never shows them must still be able to plan.
    private bool _baselineKnownOff;

    public CpAllocationSectionViewModel(PlayerStats stats, GameDataCache gameData,
                                        InventoryManager inventory, ProfileService profile,
                                        CpPlanState planState, TrainerWalkManager trainerWalk,
                                        AutoTrainManager autoTrain,
                                        Game.Spells.ListedEffectCatalog listedEffects)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(planState);
        ArgumentNullException.ThrowIfNull(trainerWalk);
        ArgumentNullException.ThrowIfNull(autoTrain);
        ArgumentNullException.ThrowIfNull(listedEffects);
        _listedEffects = listedEffects;
        _stats = stats;
        _gameData = gameData;
        _inventory = inventory;
        _profile = profile;
        _planState = planState;
        _trainerWalk = trainerWalk;
        _autoTrain = autoTrain;

        LoadPlanFromProfile();
        RefreshBaseline();
        SyncAutoTrain();
        SeedAutoTrainToggles();

        _stats.PropertyChanged += OnStatsChanged;
        _inventory.Changed += OnInventoryChanged;
        _profile.ProfileLoaded += OnProfileLoaded;
        _profile.ProfileSaving += OnProfileSavingReseed;
        _trainerWalk.StateChanged += OnAutoTrainStateChanged;
        _trainerWalk.SpellRunFinished += OnSpellRunFinished;
        _trainerWalk.PlanApplied += OnPlanApplied;
        _autoTrain.StateChanged += OnAutoTrainStateChanged;
        _autoTrain.ApplyTargetsCompleted += OnApplyLevelCompleted;
        // A game-data set swap flips the realm (Stock ↔ Paradigm) and race bounds,
        // so re-resolve the baseline + realm-aware column tooltips + CP cost curve.
        _gameData.ActiveSetChanged += OnActiveSetChanged;
    }

    private void OnActiveSetChanged(string? _) => RefreshBaseline();

    // Auto-train applied (and removed) a level's CP row — reload the grid so the
    // consumed row disappears from the displayed plan.
    private void OnPlanApplied()
    {
        LoadPlanFromProfile();
        RefreshBaseline();
        SyncAutoTrain();
    }

    // ----- commands -------------------------------------------------------

    // Append the next level, seeded from the previous row (or baseline).
    [RelayCommand]
    private void AddLevel()
    {
        // The first row starts from the trained stats, so they have to be known:
        // seeded from a buffed reading, the buff would be saved as the plan.
        if (Rows.Count == 0 && _baselineKnownOff)
        {
            ActionMessage = "Read `stat` with no buff or curse on your stats first — the first row starts from your trained stats.";
            return;
        }
        int level = Rows.Count > 0 ? Rows[^1].Level + 1 : Math.Max(2, _stats.Level + 1);
        CpPlanEntry seed = Rows.Count > 0 ? Rows[^1].ToEntry() : _baseline;
        _lastEditedStat = null;
        ActionMessage = null;
        Rows.Add(NewRow(level, seed));
        RecalcGrid();
        Persist();
    }

    // Remove the selected row — but only the top or bottom one, so the plan stays
    // a contiguous level run (removing a middle level would orphan the rows
    // above/below it). A middle selection explains why in ActionMessage instead.
    [RelayCommand(CanExecute = nameof(HasRows))]
    private void RemoveRow()
    {
        if (Rows.Count == 0) return;
        if (SelectedRow is null)
        {
            ActionMessage = "Select the top or bottom row to remove.";
            return;
        }
        int idx = Rows.IndexOf(SelectedRow);
        if (idx != 0 && idx != Rows.Count - 1)
        {
            ActionMessage = "You can only remove the top or bottom row of the plan.";
            return;
        }
        ActionMessage = null;
        _lastEditedStat = null;
        Rows.RemoveAt(idx);
        RecalcGrid();
        Persist();
    }

    // Clear the plan and persist the now-empty plan to the profile.
    [RelayCommand(CanExecute = nameof(HasRows))]
    private void Reset()
    {
        _lastEditedStat = null;
        ActionMessage = null;
        Rows.Clear();
        RecalcGrid();
        Persist();
    }

    // Clearing / changing the selection dismisses stale status + re-evaluates the
    // Apply-this-level button against the newly selected row.
    partial void OnSelectedRowChanged(CpPlanRowViewModel? value)
    {
        ActionMessage = null;
        ApplyLevelCommand.NotifyCanExecuteChanged();
    }

    // Persist the current (clamped) plan to the loaded profile. Called after every
    // structural edit (add / remove / reset) and cell edit, so the plan saves itself
    // without a dedicated button; an empty grid clears the stored plan. No-op when no
    // character profile is loaded. The plan lands in the in-memory profile at once;
    // the disk write waits for a pause in editing, because a whole-profile save per
    // cell or added row made the tab lag and churn the disk. A profile swap saves the
    // outgoing profile itself, and Dispose flushes a pending write.
    private void Persist()
    {
        if (_profile.Current is not { } p) return;
        p.CharacterPlan = Rows.Count == 0 ? null : Rows.Select(r => r.ToEntry()).ToList();
        SyncAutoTrain();   // the saved plan drives auto-train — refresh "can train now"
        _saveTimer ??= CreateSaveTimer();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);
    private Avalonia.Threading.DispatcherTimer? _saveTimer;

    private Avalonia.Threading.DispatcherTimer CreateSaveTimer()
    {
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = SaveDelay };
        timer.Tick += (_, _) => FlushSave();
        return timer;
    }

    private void FlushSave()
    {
        if (_saveTimer is not { IsEnabled: true }) return;
        _saveTimer.Stop();
        _profile.Save();
    }

    private bool HasRows() => Rows.Count > 0;

    // Walk to the nearest allowed trainer and train + apply the plan.
    [RelayCommand(CanExecute = nameof(CanRunTrain))]
    private void TrainNow()
    {
        // A trip started by hand is a walk started by hand.
        if (Services.AppServices.Current.RefuseStartForMasterSwitch("Training trip")) return;
        _trainerWalk.TrainNow();
    }

    private bool CanRunTrain() => CanTrainNow && !AutoTrainBusy;

    // The shop leg of a train trip on its own: buy and read the scrolls for spells
    // this level can learn, whatever Auto-obtain spells from shops is set to.
    [RelayCommand(CanExecute = nameof(CanBuySpells))]
    private void BuySpellsNow()
    {
        if (Services.AppServices.Current.RefuseStartForMasterSwitch("Spell-buying trip")) return;
        ActionMessage = _trainerWalk.BuySpellsNow();
    }

    private bool CanBuySpells() => _trainerWalk.CanBuySpellsNow && !AutoTrainBusy;

    private void OnSpellRunFinished(string summary) => ActionMessage = summary;

    private void OnAutoTrainStateChanged() => SyncAutoTrain();

    // Mirror the coordinator's live state into the bound properties + commands.
    // Busy is either engine — the walk-train (Train now) or the form driver
    // (Apply this level) — so both buttons disable while any train is in flight.
    private void SyncAutoTrain()
    {
        CanTrainNow = _trainerWalk.CanTrainNow;
        AutoTrainBusy = _trainerWalk.IsBusy || _autoTrain.IsBusy;
        BuyingSpells = _trainerWalk.IsBuyingSpells;
        TrainNowCommand.NotifyCanExecuteChanged();
        BuySpellsNowCommand.NotifyCanExecuteChanged();
        ApplyLevelCommand.NotifyCanExecuteChanged();
    }

    // ----- auto-train parity toggles (mirror Settings → Auto-Trainer) ----------
    // Auto-train / Auto-train-stats live in profile.Settings["AutoTrainer"]. These
    // toggles drive that DTO from the CP-Alloc tab; a flip elsewhere (Settings tab,
    // reconnect) reseeds these via ProfileSaving so every surface stays agreed.
    [ObservableProperty] private bool _autoTrainOn;
    [ObservableProperty] private bool _autoTrainStatsOn;
    [ObservableProperty] private bool _autoTrainPartyOn;
    private bool _suppressToggleWriteback;

    private void SeedAutoTrainToggles()
    {
        AutoTrainerSettings dto = ReadAutoTrainer();
        _suppressToggleWriteback = true;
        try { AutoTrainOn = dto.AutoTrain; AutoTrainStatsOn = dto.AutoTrainStats; AutoTrainPartyOn = dto.AutoTrainParty; }
        finally { _suppressToggleWriteback = false; }
    }

    private AutoTrainerSettings ReadAutoTrainer()
    {
        if (_profile.Current?.Settings is { } s
            && s.TryGetValue("AutoTrainer", out System.Text.Json.JsonElement json))
        {
            try { return System.Text.Json.JsonSerializer.Deserialize<AutoTrainerSettings>(json) ?? new(); }
            catch { /* malformed → defaults */ }
        }
        return new AutoTrainerSettings();
    }

    partial void OnAutoTrainOnChanged(bool value)
    {
        if (_suppressToggleWriteback) return;
        _profile.UpdateSection<AutoTrainerSettings>("AutoTrainer", dto => dto.AutoTrain = value);
    }

    partial void OnAutoTrainPartyOnChanged(bool value)
    {
        if (_suppressToggleWriteback) return;
        _profile.UpdateSection<AutoTrainerSettings>("AutoTrainer", dto => dto.AutoTrainParty = value);
    }

    partial void OnAutoTrainStatsOnChanged(bool value)
    {
        if (_suppressToggleWriteback) return;
        // Auto-train stats needs a saved CP plan to apply — refuse + revert without one.
        if (value && Rows.Count == 0)
        {
            ActionMessage = "Save a CP allocation plan first — Auto-train stats has nothing to apply.";
            _suppressToggleWriteback = true;
            try { AutoTrainStatsOn = false; }
            finally { _suppressToggleWriteback = false; }
            return;
        }
        _profile.UpdateSection<AutoTrainerSettings>("AutoTrainer", dto => dto.AutoTrainStats = value);
    }

    // A save from any surface reseeds our toggles so they never drift from the
    // persisted AutoTrainerSettings (the Settings tab, the reconnect re-enable, …).
    private void OnProfileSavingReseed(CharacterProfile _) => SeedAutoTrainToggles();

    // ----- Apply this level (semi-manual) --------------------------------------
    // Apply the SELECTED plan row's stats at the trainer we're standing at — only
    // when its CP genuinely lines up (fully affordable from live CP, nothing
    // trimmed). On a `stat`-confirmed success the row is cleared; else a status shows.
    [RelayCommand(CanExecute = nameof(CanApplyLevel))]
    private void ApplyLevel()
    {
        if (!TryResolveSelectedRow(out int[] row, out string reason))
        {
            ActionMessage = reason;
            return;
        }
        ActionMessage = "Applying at the trainer…";
        _autoTrain.ApplyTargets(row);   // fires StateChanged → SyncAutoTrain disables the buttons
        SyncAutoTrain();
    }

    // Enabled the moment a plan row is selected and no train run is in flight; the
    // CP-lines-up / has-a-raise validation runs on click so the refusal reasons in
    // TryResolveSelectedRow actually reach the user instead of a silently-dead
    // button.
    private bool CanApplyLevel() =>
        !AutoTrainBusy && !_autoTrain.IsBusy && HasCharacter && SelectedRow is not null;

    // The explicit apply reported back: clear the row only when it was checked as applied.
    private void OnApplyLevelCompleted(bool ok)
    {
        if (ok && SelectedRow is { } row)
        {
            int level = row.Level;
            // Remove first: dropping the selected row nulls SelectedRow, which clears
            // ActionMessage via OnSelectedRowChanged — so set the success text AFTER.
            Rows.Remove(row);
            RecalcGrid();
            Persist();
            ActionMessage = $"Level {level} applied — plan row cleared.";
        }
        else if (!ok)
        {
            ActionMessage = _autoTrain.LastApplyNote is { } why
                ? $"Nothing cleared — {why.TrimEnd('.')}."
                : "Couldn't confirm the training — nothing cleared (are you at a trainer?).";
        }
        SyncAutoTrain();
    }

    // Resolve the selected row to the plan row to apply, with a user-facing
    // `reason` when it can't be: no row / no character, the row's full spend doesn't
    // fit live CP (the budget clamp trimmed it), or there's nothing left to raise
    // (already trained). Order matters — a trim outranks "already trained" so a CP
    // shortfall never masquerades as a fully-trained level.
    //
    // The row handed over is the saved plan's, never the grid's copy of it. And
    // when the baseline can't be trusted neither check means anything, so the row
    // goes as it is: the trainer screen shows the real values and settles both.
    private bool TryResolveSelectedRow(out int[] row, out string reason)
    {
        row = Array.Empty<int>();
        reason = string.Empty;
        if (!HasCharacter || SelectedRow is null)
        {
            reason = "Select a plan row to apply.";
            return false;
        }
        int level = SelectedRow.Level;
        if (_profile.Current?.CharacterPlan?.FirstOrDefault(e => e.Level == level) is not { } saved)
        {
            reason = "This row isn't in the saved plan yet.";
            return false;
        }
        row = ToArr(saved);
        if (!_baselineTrusted) return true;

        int[] prev = ToArr(_baseline);
        int[] clamped = CpPlanCalculator.ClampRowToBudget(
            prev, row, ToArr(_raceMin), ToArr(_raceMax), _stats.Cp, _realm, null, out _);
        if (!clamped.SequenceEqual(row))
        {
            reason = "This level's CP doesn't line up with the CP you have available.";
            return false;
        }
        if (!AutoTrainSequenceBuilder.HasRaise(prev, clamped))
        {
            reason = "This level's stats are already trained — nothing to raise.";
            return false;
        }
        return true;
    }

    private static int[] ToArr(CpPlanEntry e) =>
        new[] { e.Strength, e.Intellect, e.Willpower, e.Agility, e.Health, e.Charm };

    // ----- baseline + recalc ---------------------------------------------

    private void RefreshBaseline()
    {
        CharacterPlanContext ctx = CharacterPlanContext.Resolve(_stats, _gameData, _inventory, _listedEffects);
        HasCharacter = ctx.HasCharacter;
        _baselineTrusted = ctx.BaselineTrusted;
        _baselineKnownOff = ctx.HasCharacter && !ctx.BaselineTrusted
            && ctx.Reading.State != StatReadingState.Unverified;
        BaselineNote = DescribeReading(ctx);
        if (!ctx.HasCharacter) return;

        _baseline = ctx.Baseline;
        _raceMin = ctx.RaceMin;
        _raceMax = ctx.RaceMax;
        _realm = ctx.Realm;

        UnspentCp = _stats.Cp;
        _lastEditedStat = null;   // baseline-driven recompute, not a cell edit
        RefreshStatTips();
        RecalcGrid();
    }

    // The note under the toggles: what the screen marked, and what that means for
    // the grid and for training here. Stock won't open the trainer while a stat is
    // altered; Paradigm will, and its form shows the real values whatever the
    // baseline says. Nothing is said for a mark that is only worn gear.
    internal static string? DescribeReading(CharacterPlanContext ctx)
    {
        if (!ctx.HasCharacter) return null;
        if (ctx.Reading.State == StatReadingState.Unverified)
            return "No `stat` screen is on record that says whether these stats were read with a buff up. "
                 + "Rows aren't checked against your stats until one is read.";
        if (ctx.Reading.Describe() is not { } said) return null;
        if (ctx.Realm == RealmType.Stock)
            return said + " The game won't open `train stats` while a stat is altered: Train now and Apply this "
                        + "level read `stat` again first, and wait for it to show the stats back to normal. "
                        + "Rows aren't checked against your stats meanwhile.";
        return ctx.BaselineTrusted
            ? said
            : said + " Rows aren't checked against your stats until a `stat` is read without it; Train now and "
                   + "Apply this level take the real values from the trainer screen.";
    }

    // Rebuild the six column tooltips from the live raw-base stats + realm. Anchored
    // on the character's current base value so the "next breakpoint" is the real
    // next point that ticks a derived stat, gear aside. The class/race context feeds
    // the HP and (class-specific) mana-regen effects.
    private void RefreshStatTips()
    {
        var block = new StatBlock(
            _stats.Level, _baseline.Strength, _baseline.Intellect, _baseline.Willpower,
            _baseline.Agility, _baseline.Health, _baseline.Charm);
        StatContext ctx = StatContext.Resolve(_gameData, _stats.Class, _stats.Race, _realm);
        StrStatTip = StatEffects.Tooltip(BaseStat.Strength, block, ctx);
        IntStatTip = StatEffects.Tooltip(BaseStat.Intellect, block, ctx);
        WilStatTip = StatEffects.Tooltip(BaseStat.Willpower, block, ctx);
        AgiStatTip = StatEffects.Tooltip(BaseStat.Agility, block, ctx);
        HeaStatTip = StatEffects.Tooltip(BaseStat.Health, block, ctx);
        ChmStatTip = StatEffects.Tooltip(BaseStat.Charm, block, ctx);
    }

    private void RecalcGrid()
    {
        if (_suppress || !HasCharacter) return;

        List<CpPlanEntry> entries = Rows.Select(r => r.ToEntry()).ToList();
        IReadOnlyList<CpRowResult> results = CpPlanCalculator.Compute(
            _baseline, entries, _raceMin, _raceMax, UnspentCp, _realm, _lastEditedStat);

        _suppress = true;
        try
        {
            for (int i = 0; i < results.Count && i < Rows.Count; i++)
            {
                CpRowResult res = results[i];
                CpPlanRowViewModel row = Rows[i];
                // Write back the clamped target stats so the grid reflects
                // race-max / can't-untrain limits the user may have typed past.
                CpPlanEntry shown = RowToShow(row.ToEntry(), res, _baselineTrusted);
                row.Strength = shown.Strength;
                row.Intellect = shown.Intellect;
                row.Willpower = shown.Willpower;
                row.Agility = shown.Agility;
                row.Health = shown.Health;
                row.Charm = shown.Charm;
                row.CpEarnedTotal = res.CpEarnedTotal;
                row.CpLeft = res.CpLeft;
            }
        }
        finally { _suppress = false; }

        RemoveRowCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        ApplyLevelCommand.NotifyCanExecuteChanged();

        // Publish the (clamped) plan so the Level Projection tab can apply the
        // planned stat increases at each level.
        _planState.Update(_baseline, _stats.Level, results
            .Select(r => new CpPlanEntry(
                r.Level, r.Strength, r.Intellect, r.Willpower, r.Agility, r.Health, r.Charm))
            .ToList());
    }

    // The stats a grid row should hold after a recalc: the clamped ones when the
    // baseline can be trusted, else the row as it stands. Clamping raises a target
    // to the baseline ("can't untrain") and trims it to the CP the baseline leaves,
    // so against a baseline read with a buff up it would rewrite the plan to the
    // buffed numbers, which are then saved and typed at the trainer.
    internal static CpPlanEntry RowToShow(CpPlanEntry row, CpRowResult clamped, bool baselineTrusted) =>
        baselineTrusted
            ? new CpPlanEntry(row.Level, clamped.Strength, clamped.Intellect, clamped.Willpower,
                              clamped.Agility, clamped.Health, clamped.Charm)
            : row;

    // A user cell edit: remember which stat so an overspend trims that cell, then
    // re-total. Suppressed during seeding / write-back (those aren't user edits).
    private void OnRowEdited(CpStat stat)
    {
        if (_suppress) return;
        _lastEditedStat = stat;
        RecalcGrid();
        Persist();
    }

    private CpPlanRowViewModel NewRow(int level, CpPlanEntry seed)
    {
        var row = new CpPlanRowViewModel(level, OnRowEdited);
        _suppress = true;   // seeding the 6 stats shouldn't trigger a recalc per-set
        try
        {
            row.Strength = seed.Strength;
            row.Intellect = seed.Intellect;
            row.Willpower = seed.Willpower;
            row.Agility = seed.Agility;
            row.Health = seed.Health;
            row.Charm = seed.Charm;
        }
        finally { _suppress = false; }
        return row;
    }

    private void LoadPlanFromProfile()
    {
        Rows.Clear();
        if (_profile.Current?.CharacterPlan is not { Count: > 0 } plan) return;
        foreach (CpPlanEntry e in plan)
            Rows.Add(NewRow(e.Level, e));
    }

    private void OnStatsChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshBaseline();
        ReconcileTrainedRows();
    }

    // Drop plan rows the character has already trained past: a level at or below the
    // current level whose target stats the live raw-base baseline now meets (its CP has
    // been allocated). The local Apply / Train-now buttons prune the plan via PlanApplied,
    // but @train, a manually-typed `train stats`, or a server-side CP spend raise the stats
    // WITHOUT the client's own CP-apply committing — so the just-trained level was left
    // stuck on the grid. Reconciling off the character's real level + stats catches every
    // path. Conservative: a row is removed only once the baseline meets ALL of its targets,
    // so a level reached but not yet allocated (its planned raises still pending) stays.
    private void ReconcileTrainedRows()
    {
        if (_suppress || !HasCharacter || Rows.Count == 0) return;
        // Rows are deleted here on the baseline's word alone, so it has to be one
        // that can be trusted: a buffed Strength can meet a row's target while the
        // row's CP is still unspent.
        if (!_baselineTrusted) return;
        int level = _stats.Level;

        List<CpPlanRowViewModel> fulfilled =
            Rows.Where(r => IsRowTrained(r.ToEntry(), _baseline, level)).ToList();
        if (fulfilled.Count == 0) return;

        foreach (CpPlanRowViewModel r in fulfilled) Rows.Remove(r);
        RecalcGrid();
        Persist();
    }

    // A plan row is "trained past" — safe to drop from the plan — when its level is at
    // or below the character's current level AND the raw-base baseline meets every one of
    // its target stats (the CP for that level has actually been allocated). Both halves
    // matter: the level guard keeps a future level whose stats happen to be met already,
    // and the stats guard keeps a level that's been reached but not yet allocated. Pure, since
    // this prune decision deletes persisted plan rows.
    internal static bool IsRowTrained(CpPlanEntry row, CpPlanEntry baseline, int currentLevel)
        => row.Level <= currentLevel
           && baseline.Strength  >= row.Strength
           && baseline.Intellect >= row.Intellect
           && baseline.Willpower >= row.Willpower
           && baseline.Agility   >= row.Agility
           && baseline.Health    >= row.Health
           && baseline.Charm     >= row.Charm;
    private void OnInventoryChanged() => RefreshBaseline();
    private void OnProfileLoaded(CharacterProfile _)
    {
        _saveTimer?.Stop();   // the swap already saved the outgoing profile
        LoadPlanFromProfile();
        RefreshBaseline();
        SyncAutoTrain();
        SeedAutoTrainToggles();
    }

    public override void Dispose()
    {
        FlushSave();
        _stats.PropertyChanged -= OnStatsChanged;
        _inventory.Changed -= OnInventoryChanged;
        _profile.ProfileLoaded -= OnProfileLoaded;
        _profile.ProfileSaving -= OnProfileSavingReseed;
        _trainerWalk.StateChanged -= OnAutoTrainStateChanged;
        _trainerWalk.SpellRunFinished -= OnSpellRunFinished;
        _trainerWalk.PlanApplied -= OnPlanApplied;
        _autoTrain.StateChanged -= OnAutoTrainStateChanged;
        _autoTrain.ApplyTargetsCompleted -= OnApplyLevelCompleted;
        _gameData.ActiveSetChanged -= OnActiveSetChanged;
    }
}
