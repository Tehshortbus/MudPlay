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
using FujinTerm.Game.Calculators;
using FujinTerm.Game.Inventory;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.CharacterWorkshop;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// CP ALLOCATION section — the editable per-level character-point plan. The
/// baseline is the live raw-base stats (current stats minus equipment bonuses);
/// each grid row is a planned future level whose target STR/INT/WIL/AGL/HEA/CHM
/// the user edits, with Total CP earned / CP Left recomputed live via
/// <see cref="CpPlanCalculator"/> (race-min cost curve, race-max clamp,
/// cumulative carryover). A target that would overspend is auto-trimmed at the
/// just-edited cell so CP Left never goes negative. The clamped plan is published
/// to the shared <see cref="CpPlanState"/> (so the Level Projection tab reflects
/// the planned stat increases per level) and persists to the profile's
/// <see cref="CharacterProfile.CharacterPlan"/>, driving auto-train + the
/// <c>@train</c> remote command in later PRs.
/// </summary>
public sealed partial class CpAllocationSectionViewModel : WorkshopSectionViewModel
{
    private readonly PlayerStats _stats;
    private readonly GameDataCache _gameData;
    private readonly InventoryManager _inventory;
    private readonly ProfileService _profile;
    private readonly CpPlanState _planState;
    private Control? _view;
    private bool _suppress;
    // The cell most recently edited by the user, so an overspend trims that cell
    // (not an unrelated stat). Null for structural / baseline-driven recalcs.
    private CpStat? _lastEditedStat;

    public override string Id => "cpallocation";
    public override string Title => "CP Allocation";
    public override Control View => _view ??= new CpAllocationSectionView { DataContext = this };

    public ObservableCollection<CpPlanRowViewModel> Rows { get; } = new();

    /// <summary>Current unspent CP, seeded as the plan's starting balance (not displayed).</summary>
    [ObservableProperty] private int _unspentCp;
    /// <summary>False when no race/class resolves (no character / game data) — gates the grid.</summary>
    [ObservableProperty] private bool _hasCharacter;

    // Captured on RefreshBaseline; inputs to the recalc.
    private CpPlanEntry _baseline = new();
    private CpPlanEntry _raceMin = new();
    private CpPlanEntry _raceMax = new();
    private RealmType _realm;

    public CpAllocationSectionViewModel(PlayerStats stats, GameDataCache gameData,
                                        InventoryManager inventory, ProfileService profile,
                                        CpPlanState planState)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(planState);
        _stats = stats;
        _gameData = gameData;
        _inventory = inventory;
        _profile = profile;
        _planState = planState;

        LoadPlanFromProfile();
        RefreshBaseline();

        _stats.PropertyChanged += OnStatsChanged;
        _inventory.Changed += OnInventoryChanged;
        _profile.ProfileLoaded += OnProfileLoaded;
    }

    // ----- commands -------------------------------------------------------

    /// <summary>Append the next level, seeded from the previous row (or baseline).</summary>
    [RelayCommand]
    private void AddLevel()
    {
        int level = Rows.Count > 0 ? Rows[^1].Level + 1 : Math.Max(2, _stats.Level + 1);
        CpPlanEntry seed = Rows.Count > 0 ? Rows[^1].ToEntry() : _baseline;
        _lastEditedStat = null;
        Rows.Add(NewRow(level, seed));
        RecalcGrid();
    }

    /// <summary>Drop the last planned level.</summary>
    [RelayCommand(CanExecute = nameof(HasRows))]
    private void RemoveLast()
    {
        if (Rows.Count == 0) return;
        _lastEditedStat = null;
        Rows.RemoveAt(Rows.Count - 1);
        RecalcGrid();
    }

    /// <summary>Clear the plan back to the baseline (does not persist until Save).</summary>
    [RelayCommand(CanExecute = nameof(HasRows))]
    private void Reset()
    {
        _lastEditedStat = null;
        Rows.Clear();
        RecalcGrid();
    }

    /// <summary>Persist the current plan to the loaded profile.</summary>
    [RelayCommand]
    private void Save()
    {
        if (_profile.Current is not { } p) return;
        p.CharacterPlan = Rows.Count == 0 ? null : Rows.Select(r => r.ToEntry()).ToList();
        _profile.Save();
    }

    private bool HasRows() => Rows.Count > 0;

    // ----- baseline + recalc ---------------------------------------------

    private void RefreshBaseline()
    {
        JsonElement? raceOpt = _gameData.FindRowByName("Races", _stats.Race);
        if (raceOpt is not JsonElement race || string.IsNullOrEmpty(_stats.Race))
        {
            HasCharacter = false;
            return;
        }
        HasCharacter = true;
        _realm = _gameData.ActiveRealm;

        _raceMin = new CpPlanEntry(0,
            GetInt(race, "mSTR"), GetInt(race, "mINT"), GetInt(race, "mWIL"),
            GetInt(race, "mAGL"), GetInt(race, "mHEA"), GetInt(race, "mCHM"));
        _raceMax = new CpPlanEntry(0,
            GetInt(race, "xSTR"), GetInt(race, "xINT"), GetInt(race, "xWIL"),
            GetInt(race, "xAGL"), GetInt(race, "xHEA"), GetInt(race, "xCHM"));

        // Raw base = live stats minus equipment bonuses (the `stat` screen shows
        // gear-boosted values), floored at the race minimum.
        EquipmentStatSummary eq = CharacterCalculator
            .AggregateEquipmentStats(_inventory.Snapshot.EquippedItems, _gameData).Totals;
        _baseline = new CpPlanEntry(_stats.Level,
            RawBase(_stats.Strength, eq.PlusStrength, _raceMin.Strength),
            RawBase(_stats.Intellect, eq.PlusIntellect, _raceMin.Intellect),
            RawBase(_stats.Willpower, eq.PlusWillpower, _raceMin.Willpower),
            RawBase(_stats.Agility, eq.PlusAgility, _raceMin.Agility),
            RawBase(_stats.Health, eq.PlusHealth, _raceMin.Health),
            RawBase(_stats.Charm, eq.PlusCharm, _raceMin.Charm));

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

        RemoveLastCommand.NotifyCanExecuteChanged();
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

    private static int RawBase(int live, int equipBonus, int raceMin) =>
        Math.Max(raceMin, live - equipBonus);

    private static int GetInt(JsonElement row, string property)
    {
        if (row.ValueKind != JsonValueKind.Object) return 0;
        if (!row.TryGetProperty(property, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }

    private void OnStatsChanged(object? sender, PropertyChangedEventArgs e) => RefreshBaseline();
    private void OnInventoryChanged() => RefreshBaseline();
    private void OnProfileLoaded(CharacterProfile _)
    {
        LoadPlanFromProfile();
        RefreshBaseline();
    }

    public override void Dispose()
    {
        _stats.PropertyChanged -= OnStatsChanged;
        _inventory.Changed -= OnInventoryChanged;
        _profile.ProfileLoaded -= OnProfileLoaded;
    }
}
