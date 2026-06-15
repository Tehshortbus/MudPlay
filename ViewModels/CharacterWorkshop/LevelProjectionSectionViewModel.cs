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
using FujinTerm.Services;
using FujinTerm.Views.CharacterWorkshop;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// LEVEL PROJECTION section — a per-level grid of XP-to-next / total XP, the
/// HP roll bracket + per-tick HP regen, and (for casters) max Mana/Kai + MP
/// regen, across a target level range. The Race / Class dropdowns follow the
/// live character — they adopt the character's race/class on profile load and
/// whenever an in-game <c>stat</c> changes them — while still allowing a
/// momentary what-if pick. The realm is detected from the active game-data set
/// (no picker), and the character's current level is highlighted in the grid.
/// </summary>
/// <remarks>
/// Built on the PR 10.2 calculators via <see cref="LevelProjectionCalculator"/>.
/// The what-if swaps the class/race table coefficients but holds the character's
/// current attributes (HEA/INT/WIL/CHM) flat — quest HP/MP bonuses and CP-plan
/// stat deltas fold in once those tabs ship (PR 10.10 / 10.7).
/// </remarks>
public sealed partial class LevelProjectionSectionViewModel : WorkshopSectionViewModel
{
    // Cap the projected span so a stray From/To range can't build a runaway grid.
    private const int MaxRowSpan = 500;

    private readonly PlayerStats _stats;
    private readonly GameDataCache _gameData;
    private Control? _view;
    private bool _suppress;
    private bool _seeded;
    // Last character race/class we synced to — lets a what-if pick survive
    // routine stat refreshes (HP/exp) and only re-sync when race/class change.
    private string? _lastCharRace;
    private string? _lastCharClass;

    public override string Id => "levelprojection";
    public override string Title => "Level Projection";
    public override Control View => _view ??= new LevelProjectionSectionView { DataContext = this };

    public ObservableCollection<LevelProjectionRow> Rows { get; } = new();
    public ObservableCollection<string> RaceOptions { get; } = new();
    public ObservableCollection<string> ClassOptions { get; } = new();

    [ObservableProperty] private int _fromLevel = 1;
    [ObservableProperty] private int _toLevel = 15;
    [ObservableProperty] private string? _selectedRace;
    [ObservableProperty] private string? _selectedClass;
    /// <summary>False when no class/race resolves — drives the empty-state hint.</summary>
    [ObservableProperty] private bool _hasProjection;

    public LevelProjectionSectionViewModel(PlayerStats stats, GameDataCache gameData)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(gameData);
        _stats = stats;
        _gameData = gameData;

        SeedFromCurrent();
        _stats.PropertyChanged += OnStatsChanged;
        _gameData.ActiveSetChanged += OnActiveSetChanged;
    }

    /// <summary>Re-seed Race / Class + the level range from the live character.</summary>
    [RelayCommand]
    public void ResetToCurrent() => SeedFromCurrent();

    private void SeedFromCurrent()
    {
        _suppress = true;
        try
        {
            EnsureOptionLists();
            SelectedRace = !string.IsNullOrEmpty(_stats.Race) ? _stats.Race : RaceOptions.FirstOrDefault();
            SelectedClass = !string.IsNullOrEmpty(_stats.Class) ? _stats.Class : ClassOptions.FirstOrDefault();
            // Characters start at level 1 with 0 exp, so the projection begins at
            // level 2 (the first level that costs anything to reach).
            int level = Math.Max(2, _stats.Level);
            FromLevel = level;
            ToLevel = level + 15;
        }
        finally { _suppress = false; }

        _lastCharRace = _stats.Race;
        _lastCharClass = _stats.Class;
        if (!string.IsNullOrEmpty(SelectedClass)) _seeded = true;
        Rebuild();
    }

    partial void OnFromLevelChanged(int value) => Rebuild();
    partial void OnToLevelChanged(int value) => Rebuild();
    partial void OnSelectedRaceChanged(string? value) => Rebuild();
    partial void OnSelectedClassChanged(string? value) => Rebuild();

    private void OnStatsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Follow the character's race/class — on the first real stats and
        // whenever they actually change (profile swap / in-game stat). Routine
        // refreshes (HP/exp/etc.) that don't touch race or class leave a what-if
        // pick alone; they just refresh the numbers + current-level highlight.
        bool charChanged = !string.Equals(_stats.Race, _lastCharRace, StringComparison.Ordinal)
                        || !string.Equals(_stats.Class, _lastCharClass, StringComparison.Ordinal);
        if (charChanged && !string.IsNullOrEmpty(_stats.Class))
        {
            SyncToCharacter();
            return;
        }
        Rebuild();
    }

    // New active set → new race/class tables + possibly a different realm.
    // Rebuild the option lists and re-adopt the character against them.
    private void OnActiveSetChanged(string? setName)
    {
        _suppress = true;
        try
        {
            RaceOptions.Clear();
            ClassOptions.Clear();
            EnsureOptionLists();
            if (!string.IsNullOrEmpty(_stats.Race)) SelectedRace = _stats.Race;
            if (!string.IsNullOrEmpty(_stats.Class)) SelectedClass = _stats.Class;
        }
        finally { _suppress = false; }

        _lastCharRace = _stats.Race;
        _lastCharClass = _stats.Class;
        Rebuild();
    }

    // Adopt the live character's race/class into the dropdowns (suppressed so
    // the two assignments rebuild once).
    private void SyncToCharacter()
    {
        _suppress = true;
        try
        {
            EnsureOptionLists();
            if (!string.IsNullOrEmpty(_stats.Race)) SelectedRace = _stats.Race;
            if (!string.IsNullOrEmpty(_stats.Class)) SelectedClass = _stats.Class;
        }
        finally { _suppress = false; }

        _lastCharRace = _stats.Race;
        _lastCharClass = _stats.Class;
        _seeded = !string.IsNullOrEmpty(_stats.Class);
        Rebuild();
    }

    private void EnsureOptionLists()
    {
        if (RaceOptions.Count == 0) PopulateNames("Races", RaceOptions);
        if (ClassOptions.Count == 0) PopulateNames("Classes", ClassOptions);
    }

    private void PopulateNames(string table, ObservableCollection<string> into)
    {
        JsonDocument? doc = _gameData.GetRawTable(table);
        if (doc is null) return;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.TryGetProperty("Name", out JsonElement n) && n.ValueKind == JsonValueKind.String)
            {
                string? name = n.GetString();
                if (!string.IsNullOrEmpty(name)) into.Add(name);
            }
        }
    }

    private void Rebuild()
    {
        if (_suppress) return;

        RealmType realm = _gameData.ActiveRealm;
        Rows.Clear();

        JsonElement? classOpt = _gameData.FindRowByName("Classes", SelectedClass ?? string.Empty);
        JsonElement? raceOpt = _gameData.FindRowByName("Races", SelectedRace ?? string.Empty);
        if (classOpt is not JsonElement classRow || raceOpt is not JsonElement raceRow)
        {
            HasProjection = false;
            return;
        }

        int chart = ExperienceTableCalculator.CalcExpChart(
            GetInt(classRow, "ExpTable"), GetInt(raceRow, "ExpTable"));
        int minHits = GetInt(classRow, "MinHits");
        int maxHits = GetInt(classRow, "MaxHits");
        int mageryType = GetInt(classRow, "MageryType");
        int mageryLevel = GetInt(classRow, "MageryLVL");
        int raceHpPerLevel = GetInt(raceRow, "HPPerLVL");
        bool isCaster = mageryType != 0;

        // Floor at level 2 — level 1 is the 0-exp starting point, not a row.
        int from = Math.Max(2, FromLevel);
        int to = Math.Max(from, ToLevel);
        if (to - from > MaxRowSpan) to = from + MaxRowSpan;

        IReadOnlyList<LevelProjection> projections = LevelProjectionCalculator.Project(
            from, to, chart, _stats.Health, _stats.Intellect, _stats.Willpower, _stats.Charm,
            minHits, maxHits, raceHpPerLevel, mageryType, mageryLevel, realm);

        int currentLevel = _stats.Level;
        long currentExp = _stats.Exp;   // 0 when no profile/live exp → remaining == total
        foreach (LevelProjection p in projections)
            Rows.Add(new LevelProjectionRow(p, currentExp, p.Level == currentLevel, isCaster));

        HasProjection = Rows.Count > 0;
    }

    public override void Dispose()
    {
        _stats.PropertyChanged -= OnStatsChanged;
        _gameData.ActiveSetChanged -= OnActiveSetChanged;
    }

    private static int GetInt(JsonElement row, string property)
    {
        if (row.ValueKind != JsonValueKind.Object) return 0;
        if (!row.TryGetProperty(property, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }
}
