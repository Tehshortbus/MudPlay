using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Calculators;
using FujinTerm.Game.Inventory;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.CharacterWorkshop;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// EQUIPMENT MANAGER section — the gear-set editor. A set selector (with New /
/// Delete and inline Name / Keyword fields) drives the 21-row slot grid: each
/// row is <c>[☐ controls] [slot label] [item ⌄] [stat preview]</c>. The left
/// checkbox marks "this set governs this slot" — an unchecked slot is left
/// untouched on apply, so a partial set can't accidentally strip gear. "Snapshot
/// Current" seeds the grid from the live worn loadout
/// (<see cref="InventoryManager"/>); "Apply Set" hands off to
/// <see cref="EquipmentManager"/>. A right-side totals panel sums the controlled
/// physical slots' bonuses. Every edit auto-persists to
/// <see cref="CharacterProfile.Equipment"/> — there is no Save button.
/// </summary>
/// <remarks>
/// This PR (10.13) ships the slot grid + minimal set management. The fixed
/// six-condition auto-equip trigger table and the richer item finder land in
/// later Phase-10 PRs that build on this same section.
/// </remarks>
public sealed partial class EquipmentSectionViewModel : WorkshopSectionViewModel
{
    private readonly ProfileService _profile;
    private readonly InventoryManager _inventory;
    private readonly GameDataCache _gameData;
    private readonly EquipmentManager _equipment;
    private Control? _view;
    // Gates the edit callbacks while rows / fields are seeded programmatically,
    // so a profile load or set switch doesn't re-persist what it just read.
    private bool _suppress;

    public override string Id => "equipment";
    public override string Title => "Equipment Manager";
    public override Control View => _view ??= new EquipmentSectionView { DataContext = this };

    /// <summary>Saved gear sets for the loaded character, in user order.</summary>
    public ObservableCollection<EquipmentSet> Sets { get; } = new();

    /// <summary>The 21 slot rows, in <see cref="EquipmentSlotMap.DisplayOrder"/>.</summary>
    public ObservableCollection<EquipmentSlotRowViewModel> Rows { get; } = new();

    /// <summary>Aggregate bonuses of the selected set's controlled physical slots.</summary>
    public ObservableCollection<EquipBonusRow> TotalRows { get; } = new();

    /// <summary>The set being edited; null when none exists for the character.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSet))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSetCommand))]
    [NotifyCanExecuteChangedFor(nameof(SnapshotCurrentCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplySetCommand))]
    private EquipmentSet? _selectedSet;

    /// <summary>Editable name of the selected set (committed on focus-out).</summary>
    [ObservableProperty] private string _setName = string.Empty;

    /// <summary>Editable <c>@equip-</c> keyword of the selected set (committed on focus-out).</summary>
    [ObservableProperty] private string _setKeyword = string.Empty;

    /// <summary>Transient one-line result of the last Apply Set press.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasApplyStatus))]
    private string _applyStatus = string.Empty;

    /// <summary>False with no character loaded — gates New Set and the empty state.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewSetCommand))]
    private bool _hasProfile;

    /// <summary>True when the totals panel has at least one non-zero bonus row.</summary>
    [ObservableProperty] private bool _hasTotals;

    /// <summary>True when a set is selected — gates the grid and the per-set actions.</summary>
    public bool HasSet => SelectedSet is not null;

    /// <summary>True while an Apply Set status line is showing.</summary>
    public bool HasApplyStatus => !string.IsNullOrEmpty(ApplyStatus);

    public EquipmentSectionViewModel(
        ProfileService profile, InventoryManager inventory,
        GameDataCache gameData, EquipmentManager equipment)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(equipment);
        _profile = profile;
        _inventory = inventory;
        _gameData = gameData;
        _equipment = equipment;

        BuildRows();
        ReloadFromProfile();

        _profile.ProfileLoaded += OnProfileLoaded;
        _gameData.ActiveSetChanged += OnActiveSetChanged;
    }

    // ----- set management -------------------------------------------------

    /// <summary>Create a fresh, fully-uncontrolled set and select it for editing.</summary>
    [RelayCommand(CanExecute = nameof(HasProfile))]
    private void NewSet()
    {
        if (_profile.Current is not { } p) return;
        EquipmentSettings cfg = p.Equipment ??= new EquipmentSettings();
        var set = new EquipmentSet { Name = DefaultSetName(), Slots = FreshSlots() };
        cfg.Sets.Add(set);
        _profile.Save();

        _suppress = true;
        try { Sets.Add(set); SelectedSet = set; }
        finally { _suppress = false; }
        LoadSelectedSet();
    }

    /// <summary>Delete the selected set and fall back to the first remaining one.</summary>
    [RelayCommand(CanExecute = nameof(HasSet))]
    private void DeleteSet()
    {
        if (SelectedSet is not { } set || _profile.Current?.Equipment is not { } cfg) return;
        cfg.Sets.Remove(set);
        _profile.Save();

        _suppress = true;
        try { Sets.Remove(set); SelectedSet = Sets.FirstOrDefault(); }
        finally { _suppress = false; }
        LoadSelectedSet();
    }

    partial void OnSelectedSetChanged(EquipmentSet? value)
    {
        if (_suppress) return;
        LoadSelectedSet();
    }

    partial void OnSetNameChanged(string value)
    {
        if (_suppress || SelectedSet is null) return;
        SelectedSet.Name = value.Trim();
        _profile.Save();
        RefreshSetDisplay(SelectedSet);
    }

    partial void OnSetKeywordChanged(string value)
    {
        if (_suppress || SelectedSet is null) return;
        SelectedSet.Keyword = value.Trim();
        _profile.Save();
    }

    // EquipmentSet isn't observable, so renaming it won't redraw the selector's
    // item text. Replace the item in place (same reference) to force the redraw,
    // restoring the selection the replace clears.
    private void RefreshSetDisplay(EquipmentSet set)
    {
        int idx = Sets.IndexOf(set);
        if (idx < 0) return;
        _suppress = true;
        try
        {
            Sets[idx] = set;
            SelectedSet = set;
        }
        finally { _suppress = false; }
    }

    // ----- snapshot / apply ----------------------------------------------

    /// <summary>Seed the controlled physical slots from the live worn loadout.</summary>
    [RelayCommand(CanExecute = nameof(HasSet))]
    private void SnapshotCurrent()
    {
        if (SelectedSet is null) return;

        var assigned = new Dictionary<EquipmentSlot, string>();
        foreach (EquippedItem item in _inventory.Snapshot.EquippedItems)
        {
            if (EquipmentSlotMap.FromWornString(item.Slot) is not { } slot) continue;
            assigned[ResolvePairedSlot(slot, assigned)] = item.Name;
        }

        _suppress = true;
        try
        {
            foreach (EquipmentSlotRowViewModel row in Rows)
            {
                if (row.IsVirtual) continue;   // snapshot captures worn gear only
                if (assigned.TryGetValue(row.Slot, out string? name))
                    row.Load(true, name);
                else
                    row.Load(false, null);
            }
        }
        finally { _suppress = false; }

        PersistRowsToSet();
        RefreshPreviewsAndTotals();
        ApplyStatus = string.Empty;
    }

    /// <summary>Hand the selected set to the engine to walk the character into it.</summary>
    [RelayCommand(CanExecute = nameof(HasSet))]
    private void ApplySet()
    {
        if (SelectedSet is not { } set) return;
        string key = !string.IsNullOrWhiteSpace(set.Keyword) ? set.Keyword : set.Name;
        ApplyStatus = _equipment.ApplyByKeyword(key) switch
        {
            EquipResult.Applied => "Applying gear set…",
            EquipResult.NoChange => "Already wearing this set.",
            EquipResult.Busy => "An apply is already in progress.",
            _ => "Set could not be resolved.",
        };
    }

    // FromWornString resolves ambiguous "Finger" / "Wrist" to slot 1; fall through
    // to slot 2 when slot 1 is already taken by an earlier worn piece.
    private static EquipmentSlot ResolvePairedSlot(
        EquipmentSlot slot, IReadOnlyDictionary<EquipmentSlot, string> assigned) => slot switch
    {
        EquipmentSlot.Finger1 when assigned.ContainsKey(EquipmentSlot.Finger1) => EquipmentSlot.Finger2,
        EquipmentSlot.Wrist1 when assigned.ContainsKey(EquipmentSlot.Wrist1) => EquipmentSlot.Wrist2,
        _ => slot,
    };

    // ----- row editing ----------------------------------------------------

    private void OnRowEdited(EquipmentSlotRowViewModel row)
    {
        if (_suppress) return;
        PersistRowsToSet();
        UpdatePreview(row);
        RebuildTotals();
        ApplyStatus = string.Empty;
    }

    private void PersistRowsToSet()
    {
        if (SelectedSet is null) return;
        SelectedSet.Slots = Rows.Select(r => r.ToEntry()).ToList();
        _profile.Save();
    }

    // ----- load / rebuild -------------------------------------------------

    // Materialize the 21 row VMs once per game-data set — AvailableItems is fixed
    // at construction, so a set swap (different item table) rebuilds them.
    private void BuildRows()
    {
        Rows.Clear();
        foreach (EquipmentSlot slot in EquipmentSlotMap.DisplayOrder)
            Rows.Add(new EquipmentSlotRowViewModel(
                slot, EquipmentSlotMap.Label(slot), EquipmentSlotMap.IsVirtual(slot),
                EquipmentSlotMap.GetItemsForSlot(_gameData, slot), OnRowEdited));
    }

    private void ReloadFromProfile()
    {
        HasProfile = _profile.Current is not null;
        _suppress = true;
        try
        {
            Sets.Clear();
            if (_profile.Current?.Equipment?.Sets is { } sets)
                foreach (EquipmentSet s in sets) Sets.Add(s);
            SelectedSet = Sets.FirstOrDefault();
        }
        finally { _suppress = false; }
        LoadSelectedSet();
    }

    private void LoadSelectedSet()
    {
        _suppress = true;
        try
        {
            SetName = SelectedSet?.Name ?? string.Empty;
            SetKeyword = SelectedSet?.Keyword ?? string.Empty;
            ApplyStatus = string.Empty;
        }
        finally { _suppress = false; }
        LoadSelectedSetIntoRows();
    }

    private void LoadSelectedSetIntoRows()
    {
        var bySlot = new Dictionary<EquipmentSlot, EquipmentSlotEntry>();
        if (SelectedSet is not null)
            foreach (EquipmentSlotEntry e in SelectedSet.Slots)
                bySlot.TryAdd(e.Slot, e);

        _suppress = true;
        try
        {
            foreach (EquipmentSlotRowViewModel row in Rows)
            {
                if (bySlot.TryGetValue(row.Slot, out EquipmentSlotEntry? e))
                    row.Load(e.Controlled, e.ItemName);
                else
                    row.Load(false, null);
            }
        }
        finally { _suppress = false; }

        RefreshPreviewsAndTotals();
    }

    private void RefreshPreviewsAndTotals()
    {
        foreach (EquipmentSlotRowViewModel row in Rows) UpdatePreview(row);
        RebuildTotals();
    }

    // ----- stat preview + totals ------------------------------------------

    private void UpdatePreview(EquipmentSlotRowViewModel row)
    {
        string? name = string.IsNullOrWhiteSpace(row.ItemName) ? null : row.ItemName!.Trim();
        if (name is null)
        {
            row.StatPreview = string.Empty;
            return;
        }

        bool weapon = row.Slot is EquipmentSlot.Weapon or EquipmentSlot.AlternateWeapon;
        EquipmentStatSummary t = CharacterCalculator
            .AggregateEquipmentStats(new[] { new EquippedItem(name, SlotTag(row.Slot)) }, _gameData)
            .Totals;

        var parts = new List<string>();
        if (weapon && t.WeaponMax > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{t.WeaponMin}-{t.WeaponMax} dmg"));
        foreach ((string label, string value) in SummaryLines(t))
            parts.Add($"{label} {value}");

        row.StatPreview = parts.Count > 0 ? string.Join(", ", parts) : "—";
    }

    private void RebuildTotals()
    {
        var worn = new List<EquippedItem>();
        foreach (EquipmentSlotRowViewModel row in Rows)
        {
            // Virtual slots are swap-time alternates, never worn alongside the
            // primaries — excluding them keeps the totals a real loadout.
            if (!row.Controlled || row.IsVirtual) continue;
            string? name = string.IsNullOrWhiteSpace(row.ItemName) ? null : row.ItemName!.Trim();
            if (name is null) continue;
            worn.Add(new EquippedItem(name, SlotTag(row.Slot)));
        }

        TotalRows.Clear();
        if (worn.Count > 0)
        {
            EquipmentStatSummary t = CharacterCalculator.AggregateEquipmentStats(worn, _gameData).Totals;
            foreach ((string label, string value) in SummaryLines(t))
                TotalRows.Add(new EquipBonusRow(label, value, null));
        }
        HasTotals = TotalRows.Count > 0;
    }

    // The slot's worn-string tag drives which fields AggregateEquipmentStats fills
    // (weapon damage from "Weapon Hand", off-hand accy from "Off-Hand"); every
    // other field aggregates regardless, so the rest map to a neutral "Worn".
    private static string SlotTag(EquipmentSlot slot) => slot switch
    {
        EquipmentSlot.Weapon or EquipmentSlot.AlternateWeapon => "Weapon Hand",
        EquipmentSlot.OffHand or EquipmentSlot.AlternateOffHand => "Off-Hand",
        _ => "Worn",
    };

    // The curated stat lines shown in both the per-row preview and the totals
    // panel — only the non-zero ones, in a fixed reading order.
    private static IEnumerable<(string Label, string Value)> SummaryLines(EquipmentStatSummary t)
    {
        var lines = new List<(string, string)>();
        AddDouble(lines, "AC", t.PlusAC);
        AddDouble(lines, "DR", t.PlusDR);
        AddInt(lines, "STR", t.PlusStrength);
        AddInt(lines, "INT", t.PlusIntellect);
        AddInt(lines, "WIL", t.PlusWillpower);
        AddInt(lines, "AGL", t.PlusAgility);
        AddInt(lines, "HEA", t.PlusHealth);
        AddInt(lines, "CHM", t.PlusCharm);
        AddInt(lines, "Max HP", t.PlusMaxHp);
        AddInt(lines, "Max Mana", t.PlusMaxMana);
        AddInt(lines, "Accuracy", t.TotalWornAccy + t.PlusAccuracy);
        AddInt(lines, "Crits", t.PlusCrits);
        AddInt(lines, "Max Dmg", t.PlusMaxDamage);
        AddInt(lines, "Dodge", t.PlusDodge);
        AddInt(lines, "Magic Resist", t.PlusMagicResist);
        return lines;
    }

    private static void AddInt(List<(string, string)> lines, string label, int value)
    {
        if (value != 0)
            lines.Add((label, value.ToString("+0;-0", CultureInfo.InvariantCulture)));
    }

    private static void AddDouble(List<(string, string)> lines, string label, double value)
    {
        if (value != 0)
            lines.Add((label, value.ToString("+0.#;-0.#", CultureInfo.InvariantCulture)));
    }

    // ----- helpers --------------------------------------------------------

    private static List<EquipmentSlotEntry> FreshSlots() =>
        EquipmentSlotMap.DisplayOrder
            .Select(s => new EquipmentSlotEntry(s, false, null))
            .ToList();

    private string DefaultSetName()
    {
        const string baseName = "New Set";
        var taken = new HashSet<string>(Sets.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName)) return baseName;
        for (int i = 2; ; i++)
        {
            string candidate = string.Create(CultureInfo.InvariantCulture, $"{baseName} {i}");
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    private void OnProfileLoaded(CharacterProfile _) => ReloadFromProfile();

    private void OnActiveSetChanged(string? _)
    {
        BuildRows();                // new item table → refresh suggestions + previews
        LoadSelectedSetIntoRows();
    }

    public override void Dispose()
    {
        _profile.ProfileLoaded -= OnProfileLoaded;
        _gameData.ActiveSetChanged -= OnActiveSetChanged;
    }
}
