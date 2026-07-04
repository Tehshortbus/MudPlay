using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game;
using FujinTerm.Game.Calculators;
using FujinTerm.Game.Inventory;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.CharacterWorkshop;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// CP ALLOCATION section — the editable per-level character-point plan. The
// baseline is the live raw-base stats (current stats minus equipment bonuses);
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
    // Transient inline error (e.g. trying to remove a middle row); cleared on the next valid action.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemoveError))]
    private string? _removeError;

    // True when a RemoveError is showing — drives its visibility.
    public bool HasRemoveError => !string.IsNullOrEmpty(RemoveError);

    // ----- auto-train ----------------------------------------------------
    // True when the saved plan has an affordable raise to apply at the current level.
    [ObservableProperty] private bool _canTrainNow;
    // True while a train run is in flight — disables the Train Now button.
    [ObservableProperty] private bool _autoTrainBusy;

    // Captured on RefreshBaseline; inputs to the recalc.
    private CpPlanEntry _baseline = new();
    private CpPlanEntry _raceMin = new();
    private CpPlanEntry _raceMax = new();
    private RealmType _realm;

    public CpAllocationSectionViewModel(PlayerStats stats, GameDataCache gameData,
                                        InventoryManager inventory, ProfileService profile,
                                        CpPlanState planState, TrainerWalkManager trainerWalk)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(planState);
        ArgumentNullException.ThrowIfNull(trainerWalk);
        _stats = stats;
        _gameData = gameData;
        _inventory = inventory;
        _profile = profile;
        _planState = planState;
        _trainerWalk = trainerWalk;

        LoadPlanFromProfile();
        RefreshBaseline();
        SyncAutoTrain();

        _stats.PropertyChanged += OnStatsChanged;
        _inventory.Changed += OnInventoryChanged;
        _profile.ProfileLoaded += OnProfileLoaded;
        _trainerWalk.StateChanged += OnAutoTrainStateChanged;
        _trainerWalk.PlanApplied += OnPlanApplied;
    }

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
        int level = Rows.Count > 0 ? Rows[^1].Level + 1 : Math.Max(2, _stats.Level + 1);
        CpPlanEntry seed = Rows.Count > 0 ? Rows[^1].ToEntry() : _baseline;
        _lastEditedStat = null;
        RemoveError = null;
        Rows.Add(NewRow(level, seed));
        RecalcGrid();
        Persist();
    }

    // Remove the selected row — but only the top or bottom one, so the plan stays
    // a contiguous level run (removing a middle level would orphan the rows
    // above/below it). A middle selection sets RemoveError.
    [RelayCommand(CanExecute = nameof(HasRows))]
    private void RemoveRow()
    {
        if (Rows.Count == 0) return;
        if (SelectedRow is null)
        {
            RemoveError = "Select the top or bottom row to remove.";
            return;
        }
        int idx = Rows.IndexOf(SelectedRow);
        if (idx != 0 && idx != Rows.Count - 1)
        {
            RemoveError = "You can only remove the top or bottom row of the plan.";
            return;
        }
        RemoveError = null;
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
        RemoveError = null;
        Rows.Clear();
        RecalcGrid();
        Persist();
    }

    // Clearing / changing the selection dismisses a stale remove error.
    partial void OnSelectedRowChanged(CpPlanRowViewModel? value) => RemoveError = null;

    // Persist the current (clamped) plan to the loaded profile. Called after every
    // structural edit (add / remove / reset) and cell edit, so the plan saves itself
    // without a dedicated button; an empty grid clears the stored plan. No-op when no
    // character profile is loaded.
    private void Persist()
    {
        if (_profile.Current is not { } p) return;
        p.CharacterPlan = Rows.Count == 0 ? null : Rows.Select(r => r.ToEntry()).ToList();
        _profile.Save();
        SyncAutoTrain();   // the saved plan drives auto-train — refresh "can train now"
    }

    private bool HasRows() => Rows.Count > 0;

    // Walk to the nearest allowed trainer and train + apply the plan.
    [RelayCommand(CanExecute = nameof(CanRunTrain))]
    private void TrainNow() => _trainerWalk.TrainNow();

    private bool CanRunTrain() => CanTrainNow && !AutoTrainBusy;

    private void OnAutoTrainStateChanged() => SyncAutoTrain();

    // Mirror the coordinator's live state into the bound properties + command.
    private void SyncAutoTrain()
    {
        CanTrainNow = _trainerWalk.CanTrainNow;
        AutoTrainBusy = _trainerWalk.IsBusy;
        TrainNowCommand.NotifyCanExecuteChanged();
    }

    // ----- baseline + recalc ---------------------------------------------

    private void RefreshBaseline()
    {
        CharacterPlanContext ctx = CharacterPlanContext.Resolve(_stats, _gameData, _inventory);
        HasCharacter = ctx.HasCharacter;
        if (!ctx.HasCharacter) return;

        _baseline = ctx.Baseline;
        _raceMin = ctx.RaceMin;
        _raceMax = ctx.RaceMax;
        _realm = ctx.Realm;

        UnspentCp = _stats.Cp;
        _lastEditedStat = null;   // baseline-driven recompute, not a cell edit
        RecalcGrid();
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
                row.Strength = res.Strength;
                row.Intellect = res.Intellect;
                row.Willpower = res.Willpower;
                row.Agility = res.Agility;
                row.Health = res.Health;
                row.Charm = res.Charm;
                row.CpEarnedTotal = res.CpEarnedTotal;
                row.CpLeft = res.CpLeft;
            }
        }
        finally { _suppress = false; }

        RemoveRowCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();

        // Publish the (clamped) plan so the Level Projection tab can apply the
        // planned stat increases at each level.
        _planState.Update(_baseline, _stats.Level, results
            .Select(r => new CpPlanEntry(
                r.Level, r.Strength, r.Intellect, r.Willpower, r.Agility, r.Health, r.Charm))
            .ToList());
    }

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

    private void OnStatsChanged(object? sender, PropertyChangedEventArgs e) => RefreshBaseline();
    private void OnInventoryChanged() => RefreshBaseline();
    private void OnProfileLoaded(CharacterProfile _)
    {
        LoadPlanFromProfile();
        RefreshBaseline();
        SyncAutoTrain();
    }

    public override void Dispose()
    {
        _stats.PropertyChanged -= OnStatsChanged;
        _inventory.Changed -= OnInventoryChanged;
        _profile.ProfileLoaded -= OnProfileLoaded;
        _trainerWalk.StateChanged -= OnAutoTrainStateChanged;
        _trainerWalk.PlanApplied -= OnPlanApplied;
    }
}
