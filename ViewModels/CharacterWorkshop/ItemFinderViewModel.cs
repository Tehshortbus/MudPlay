using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Calculators;
using FujinTerm.Game.Inventory;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// Modeless catalog browser opened from the Equipment Manager's "Item Finder"
// button. Lists every equippable item in the active game-data set — one combined
// list sorted by slot then name (weapons and armour folded into one) — and
// narrows it with selectively-applied filters grouped for ease of use: a
// Character group (class / usable-at level / alignment, which defer to
// ItemEquipFilter.CanEquip), a Slot & type group (slot, weapon type, armour type,
// backstab-capable), and a Filter by stats group — bonus thresholds (HP / mana /
// regens / damage / accuracy / crits / backstab / AC / DR) kept at-or-above the
// ticker, a hit-magic-level min/max range, and the strength / level requirement
// gates kept at-or-below the ticker. Read-only — the finder informs slot choices;
// it doesn't write the set.
public sealed partial class ItemFinderViewModel : ObservableObject, IDialogViewModel<bool>
{
    private const string AnyClass = "(Any class)";
    private const string AnyAlign = "(Any)";
    private const string AnySlot = "(Any slot)";
    private const string AnyType = "(Any)";

    public event Action<bool>? CloseRequested;

    private readonly GameDataCache _gameData;
    private readonly IReadOnlyList<ItemFinderEntry> _all;
    private readonly Dictionary<string, EquipmentSlot> _slotByLabel = new(StringComparer.Ordinal);

    // Snapshot of the derived filter inputs, refreshed by ApplyFilter so the per-item
    // predicate stays a cheap field compare rather than re-resolving each call.
    private ClassEquipProfile _activeClass = ClassEquipProfile.Unknown;
    private AlignmentBucket? _activeAlignment;
    private bool _activeCharFilter;
    private EquipmentSlot? _activeSlot;
    private string? _activeWeaponType;
    private string? _activeArmourType;
    private bool _filterSuspended = true;

    // The sorted, filterable item catalog the grid binds to.
    public DataGridCollectionView RowsView { get; }

    // Class names from the active set, "(Any class)" first.
    public ObservableCollection<string> ClassOptions { get; } = new();

    // Alignment buckets, "(Any)" first.
    public ObservableCollection<string> AlignmentOptions { get; } = new() { AnyAlign, "Good", "Neutral", "Evil" };

    // Slot labels present in the catalog, "(Any slot)" first.
    public ObservableCollection<string> SlotOptions { get; } = new();

    // Weapon-type labels present in the catalog, "(Any)" first.
    public ObservableCollection<string> WeaponTypeOptions { get; } = new();

    // Armour-type labels present in the catalog, "(Any)" first.
    public ObservableCollection<string> ArmourTypeOptions { get; } = new();

    [ObservableProperty] private string _countText = string.Empty;

    // ----- Character group -----
    [ObservableProperty] private string? _selectedClass = AnyClass;
    [ObservableProperty] private int _usableLevel;
    [ObservableProperty] private string? _selectedAlignment = AnyAlign;

    // ----- Slot & type group -----
    [ObservableProperty] private string? _selectedSlot = AnySlot;
    [ObservableProperty] private string? _selectedWeaponType = AnyType;
    [ObservableProperty] private string? _selectedArmourType = AnyType;
    [ObservableProperty] private bool _backstabOnly;

    // ----- Filter by stats (0 = off on every row) -----
    // Bonus thresholds keep items whose stat is ≥ the value; the hit-magic ceiling and
    // the requirement gates keep items whose value is ≤ the ticker (so a higher gate is
    // less restrictive — "usable at this strength / level or below").
    [ObservableProperty] private int _minHp;
    [ObservableProperty] private int _minHpRegen;
    [ObservableProperty] private int _minMana;
    [ObservableProperty] private int _minManaRegen;
    [ObservableProperty] private int _minMinDmg;
    [ObservableProperty] private int _minMaxDmg;
    [ObservableProperty] private int _minAccuracy;
    [ObservableProperty] private int _minCrits;
    [ObservableProperty] private int _minHitMagic;   // hit-magic floor (≥)
    [ObservableProperty] private int _maxHitMagic;   // hit-magic ceiling (≤)
    [ObservableProperty] private int _minBsAccuracy;
    [ObservableProperty] private int _minBsMin;
    [ObservableProperty] private int _minBsMax;
    [ObservableProperty] private int _minAc;
    [ObservableProperty] private int _minDr;
    [ObservableProperty] private int _maxStrReq;     // required-strength gate (≤)
    [ObservableProperty] private int _maxLevelReq;   // required-level gate (≤)

    public ItemFinderViewModel(GameDataCache gameData)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        _gameData = gameData;
        _all = ItemFinderEntry.BuildCatalog(gameData);

        RowsView = new DataGridCollectionView(_all) { Filter = PassesFilter };

        BuildOptionLists();

        _filterSuspended = false;
        PropertyChanged += OnFilterPropertyChanged;
        ApplyFilter();
    }

    private void BuildOptionLists()
    {
        ClassOptions.Add(AnyClass);
        foreach (string name in ClassNames(_gameData)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase))
            ClassOptions.Add(name);

        SlotOptions.Add(AnySlot);
        foreach (ItemFinderEntry e in _all
                     .GroupBy(static e => e.Slot)
                     .OrderBy(static g => (int)g.Key)
                     .Select(static g => g.First()))
        {
            _slotByLabel[e.SlotLabel] = e.Slot;
            SlotOptions.Add(e.SlotLabel);
        }

        WeaponTypeOptions.Add(AnyType);
        foreach (string label in _all
                     .Where(static e => e.WeaponTypeLabel is not null)
                     .OrderBy(static e => e.WeaponType)
                     .Select(static e => e.WeaponTypeLabel!)
                     .Distinct(StringComparer.Ordinal))
            WeaponTypeOptions.Add(label);

        ArmourTypeOptions.Add(AnyType);
        foreach (string label in _all
                     .Where(static e => e.ArmourTypeLabel is not null)
                     .GroupBy(static e => e.ArmourTypeLabel!, StringComparer.Ordinal)
                     .OrderBy(static g => g.Min(e => e.ArmourType))
                     .Select(static g => g.Key))
            ArmourTypeOptions.Add(label);
    }

    private void OnFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_filterSuspended || e.PropertyName == nameof(CountText)) return;
        ApplyFilter();
    }

    // Re-resolve the derived filter inputs, re-run the predicate, refresh the count.
    private void ApplyFilter()
    {
        _activeClass = string.IsNullOrEmpty(SelectedClass) || SelectedClass == AnyClass
            ? ClassEquipProfile.Unknown
            : ItemEquipFilter.ResolveClassProfile(_gameData, SelectedClass);
        _activeAlignment = SelectedAlignment switch
        {
            "Good" => AlignmentBucket.Good,
            "Neutral" => AlignmentBucket.Neutral,
            "Evil" => AlignmentBucket.Evil,
            _ => null,
        };
        _activeCharFilter = _activeClass.ClassNumber > 0 || UsableLevel > 0 || _activeAlignment is not null;

        _activeSlot = SelectedSlot is { } sl && _slotByLabel.TryGetValue(sl, out EquipmentSlot s) ? s : null;
        _activeWeaponType = SelectedWeaponType is { } wt && wt != AnyType ? wt : null;
        _activeArmourType = SelectedArmourType is { } at && at != AnyType ? at : null;

        RowsView.Refresh();
        CountText = string.Create(CultureInfo.InvariantCulture,
            $"Showing {RowsView.Count:N0} of {_all.Count:N0} items");
    }

    private bool PassesFilter(object o)
    {
        if (o is not ItemFinderEntry e) return false;

        if (_activeSlot is { } slot && e.Slot != slot) return false;
        if (_activeWeaponType is { } wt && e.WeaponTypeLabel != wt) return false;
        if (_activeArmourType is { } at && e.ArmourTypeLabel != at) return false;
        if (BackstabOnly && !e.CanBackstab) return false;

        if (_activeCharFilter &&
            !ItemEquipFilter.CanEquip(e.Row, UsableLevel, _activeClass, _activeAlignment))
            return false;

        if (MinHp > 0 && e.Hp < MinHp) return false;
        if (MinHpRegen > 0 && e.HpRegen < MinHpRegen) return false;
        if (MinMana > 0 && e.Mana < MinMana) return false;
        if (MinManaRegen > 0 && e.ManaRegen < MinManaRegen) return false;
        if (MinMinDmg > 0 && e.MinDmg < MinMinDmg) return false;
        if (MinMaxDmg > 0 && e.MaxDmg < MinMaxDmg) return false;
        if (MinAccuracy > 0 && e.Accuracy < MinAccuracy) return false;
        if (MinCrits > 0 && e.Crits < MinCrits) return false;
        if (MinHitMagic > 0 && e.HitMagic < MinHitMagic) return false;
        if (MaxHitMagic > 0 && e.HitMagic > MaxHitMagic) return false;
        if (MinBsAccuracy > 0 && e.BsAccuracy < MinBsAccuracy) return false;
        if (MinBsMin > 0 && e.BsMin < MinBsMin) return false;
        if (MinBsMax > 0 && e.BsMax < MinBsMax) return false;
        if (MinAc > 0 && e.Ac < MinAc) return false;
        if (MinDr > 0 && e.Dr < MinDr) return false;

        if (MaxStrReq > 0 && e.StrReq > MaxStrReq) return false;
        if (MaxLevelReq > 0 && e.LevelReq > MaxLevelReq) return false;

        return true;
    }

    // Clear every filter back to "show everything".
    [RelayCommand]
    private void Reset()
    {
        _filterSuspended = true;
        SelectedClass = AnyClass;
        UsableLevel = 0;
        SelectedAlignment = AnyAlign;
        SelectedSlot = AnySlot;
        SelectedWeaponType = AnyType;
        SelectedArmourType = AnyType;
        BackstabOnly = false;
        MinHp = MinHpRegen = MinMana = MinManaRegen = 0;
        MinMinDmg = MinMaxDmg = MinAccuracy = MinCrits = 0;
        MinHitMagic = MaxHitMagic = 0;
        MinBsAccuracy = MinBsMin = MinBsMax = MinAc = MinDr = 0;
        MaxStrReq = MaxLevelReq = 0;
        _filterSuspended = false;
        ApplyFilter();
    }

    // Close the finder (read-only — no result to commit).
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(false);

    private static IEnumerable<string> ClassNames(GameDataCache cache)
    {
        JsonDocument? doc = cache.GetRawTable("Classes");
        if (doc is null) yield break;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!row.TryGetProperty("Name", out JsonElement nameEl)) continue;
            if (nameEl.ValueKind != JsonValueKind.String) continue;
            string? name = nameEl.GetString();
            if (!string.IsNullOrEmpty(name)) yield return name!;
        }
    }
}
