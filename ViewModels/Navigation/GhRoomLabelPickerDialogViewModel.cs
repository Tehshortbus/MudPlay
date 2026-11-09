using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.ViewModels.Navigation;

// A combo-box entry: an MDB category code paired with its display name. A
// plain type rather than a ValueTuple — Avalonia's compiled bindings can't
// resolve tuple element names (they're compile-time-only metadata).
public sealed record GhCategoryOption(int Code, string Name);

// Rule-kind picker entry — whether a row matches by top-level item category
// or by bare equip slot (see GhCategoryRule's two mutually exclusive shapes).
public sealed record GhRuleKindOption(bool IsWornSlot, string Name);

// One editable rule row inside the picker dialog. A room label can hold
// several of these (OR'd) so one room can sort for multiple categories at
// once — e.g. "Chain Scale" = Chainmail OR Scalemail; "Necks Bracers" =
// worn-at-Neck OR worn-at-Arms.
public sealed partial class GhRuleEditRowViewModel : ObservableObject
{
    public static readonly GhCategoryOption[] ItemTypes =
    {
        new(0, "Armour"), new(1, "Weapon"), new(2, "Projectile"), new(3, "Sign"),
        new(4, "Food"),   new(5, "Drink"),  new(6, "Light"),      new(7, "Key"),
        new(8, "Container"), new(9, "Scroll"), new(10, "Special"),
    };

    public static readonly GhCategoryOption[] WeaponTypes =
    {
        new(0, "1H Blunt"), new(1, "2H Blunt"), new(2, "1H Sharp"), new(3, "2H Sharp"),
    };

    // One representative MDB code per distinct displayed ArmourType — codes
    // 3/4/5/6 all render as "Leather" (LookupEnums.FormatArmourType), so
    // offering all four would just be four identical-looking duplicate rows.
    public static readonly GhCategoryOption[] ArmourTypes =
    {
        new(0, "Natural"), new(1, "Silk"), new(2, "Ninja"), new(3, "Leather"),
        new(7, "Chainmail"), new(8, "Scalemail"), new(9, "Platemail"),
    };

    // Equip-slot options for Worn-slot rules. Codes 0/1 (Nowhere / Everywhere)
    // are left off: they are what most items carry, so a room sorted by them
    // would take almost anything. 16 (Worn) stays: it marks a small set of its
    // own (severed heads, emblems) a house keeps together. One representative
    // code per displayed name (4 not 13 for "Finger", 14 not 17 for "Wrist" —
    // LookupEnums.FormatWornSlot collapses the pairs).
    public static readonly GhCategoryOption[] WornSlots =
    {
        new(2, "Head"), new(3, "Hands"),  new(4, "Finger"), new(5, "Feet"),
        new(6, "Arms"), new(7, "Back"),   new(8, "Neck"),   new(9, "Legs"),
        new(10, "Waist"), new(11, "Torso"), new(12, "Off-Hand"), new(14, "Wrist"),
        new(15, "Ears"), new(18, "Eyes"), new(19, "Face"), new(16, "Worn"),
    };

    private static readonly GhRuleKindOption[] KindOptions =
    {
        new(IsWornSlot: false, "Item category"), new(IsWornSlot: true, "Worn slot"),
    };

    public IReadOnlyList<GhRuleKindOption> RuleKindOptions => KindOptions;
    public IReadOnlyList<GhCategoryOption> ItemTypeOptions => ItemTypes;
    public IReadOnlyList<GhCategoryOption> WeaponTypeOptions => WeaponTypes;
    public IReadOnlyList<GhCategoryOption> ArmourTypeOptions => ArmourTypes;
    public IReadOnlyList<GhCategoryOption> WornSlotOptions => WornSlots;

    [ObservableProperty] private GhRuleKindOption _selectedKind;
    [ObservableProperty] private GhCategoryOption _selectedItemType;
    [ObservableProperty] private GhCategoryOption? _selectedWeaponType;
    [ObservableProperty] private GhCategoryOption? _selectedArmourType;
    [ObservableProperty] private GhCategoryOption _selectedWornSlot;

    public bool IsWornSlotKind => SelectedKind.IsWornSlot;
    public bool ShowWeaponType => !IsWornSlotKind && SelectedItemType.Code == 1;
    public bool ShowArmourType => !IsWornSlotKind && SelectedItemType.Code == 0;

    private readonly Action<GhRuleEditRowViewModel> _onRemove;

    public GhRuleEditRowViewModel(Action<GhRuleEditRowViewModel> onRemove, GhCategoryRule? existing = null)
    {
        _onRemove = onRemove;

        if (existing?.Worn is int worn)
        {
            _selectedKind = KindOptions[1];
            _selectedItemType = ItemTypes[1];
            _selectedWornSlot = Array.Find(WornSlots, o => o.Code == worn) ?? WornSlots[0];
        }
        else
        {
            _selectedKind = KindOptions[0];
            _selectedItemType = existing?.ItemType is int it
                ? Array.Find(ItemTypes, o => o.Code == it) ?? ItemTypes[1]
                : ItemTypes[1];
            if (existing?.WeaponType is int wt)
                _selectedWeaponType = Array.Find(WeaponTypes, o => o.Code == wt);
            if (existing?.ArmourType is int at)
                _selectedArmourType = Array.Find(ArmourTypes, o => o.Code == at);
            _selectedWornSlot = WornSlots[0];
        }
    }

    partial void OnSelectedKindChanged(GhRuleKindOption value)
    {
        OnPropertyChanged(nameof(IsWornSlotKind));
        OnPropertyChanged(nameof(ShowWeaponType));
        OnPropertyChanged(nameof(ShowArmourType));
    }

    partial void OnSelectedItemTypeChanged(GhCategoryOption value)
    {
        OnPropertyChanged(nameof(ShowWeaponType));
        OnPropertyChanged(nameof(ShowArmourType));
    }

    public GhCategoryRule ToRule() => IsWornSlotKind
        ? GhCategoryRule.ForWornSlot(SelectedWornSlot.Code)
        : GhCategoryRule.ForItemType(SelectedItemType.Code,
            ShowWeaponType ? SelectedWeaponType?.Code : null,
            ShowArmourType ? SelectedArmourType?.Code : null);

    [RelayCommand]
    private void Remove() => _onRemove(this);
}

// Rule picker for a Roomba room, opened from the map's right-click "Toggle:
// Roomba Room" and the Workshop's Roomba tab. Holds a list of rule rows (OR'd
// together for this room) plus a catch-all toggle. Save writes through
// GhRoomLabelStore.SetLabel; Cancel discards.
public sealed partial class GhRoomLabelPickerDialogViewModel : ObservableObject, IDialogViewModel<GhRoomLabel?>
{
    public event Action<GhRoomLabel?>? CloseRequested;

    private readonly int _map;
    private readonly int _room;

    public string RoomLabel { get; }

    // Window + header title, e.g. "Set 1/384 Back Room, Gypsy Trainer as Roomba Room".
    public string DialogTitle { get; }

    public ObservableCollection<GhRuleEditRowViewModel> Rules { get; } = new();

    [ObservableProperty] private bool _isCatchAll;

    public GhRoomLabelPickerDialogViewModel(string roomLabel, int map, int room, GhRoomLabel? existing)
    {
        RoomLabel = roomLabel;
        _map = map;
        _room = room;
        DialogTitle = string.IsNullOrWhiteSpace(roomLabel)
            ? $"Set {map}/{room} as Roomba Room"
            : $"Set {map}/{room} {roomLabel} as Roomba Room";
        _isCatchAll = existing?.IsCatchAll ?? false;

        if (existing is { Rules.Count: > 0 })
            foreach (GhCategoryRule rule in existing.Rules)
                Rules.Add(new GhRuleEditRowViewModel(RemoveRule, rule));
        else
            Rules.Add(new GhRuleEditRowViewModel(RemoveRule));
    }

    // No minimum row count — a catch-all room (e.g. "Misc Items") is commonly
    // labeled with zero explicit rules, relying purely on the catch-all flag.
    private void RemoveRule(GhRuleEditRowViewModel row) => Rules.Remove(row);

    [RelayCommand]
    private void AddRule() => Rules.Add(new GhRuleEditRowViewModel(RemoveRule));

    [RelayCommand]
    private void Save()
    {
        GhRoomLabel label = new(_map, _room)
        {
            Rules = Rules.Select(r => r.ToRule()).ToList(),
            IsCatchAll = IsCatchAll,
        };
        CloseRequested?.Invoke(label);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
