using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One labeled gang-house room in the GH Management tab's list. Read-only
// display + a Remove button — editing a label re-opens the same right-click
// picker the map uses (RenameFavorite mirrors this "map is the editor"
// pattern for favourites), so this row doesn't duplicate the picker UI.
public sealed partial class GhRoomLabelRowViewModel : ObservableObject
{
    public RoomKey Key { get; }
    public string RoomName { get; }
    public string RoomKeyText => Key.ToString();
    public string CategoryText { get; }

    // Live sweep status for this room: "Scanning" during recon, "Cleaning" while it
    // still has items to move out, "Complete" once cleared (or after a run). Blank
    // before the first sweep. Written by GhManagementSectionViewModel.
    [ObservableProperty] private string _status = string.Empty;

    // "Actively Manage" checkbox: whether Start Sweep / Start Inventory visits this
    // room for THIS character. Two-way bound; a user toggle writes back through
    // _onManageToggle to the per-character GhManagedRoomStore. Sourced from that
    // store (not the shared label), so alts on the same BBS manage independently;
    // rooms adopted via @roomba sync arrive unchecked.
    [ObservableProperty] private bool _activelyManaged;

    private readonly Action<GhRoomLabelRowViewModel> _onRemove;
    private readonly Action<RoomKey, bool> _onManageToggle;
    private readonly Action<RoomKey> _onGoto;
    // Guards the ctor's initial assignment from writing back to the store — rows are
    // rebuilt on every label/managed-set change, so a write there would loop.
    private readonly bool _loaded;

    public GhRoomLabelRowViewModel(GhRoomLabel label, string? roomName, bool activelyManaged,
        Action<GhRoomLabelRowViewModel> onRemove, Action<RoomKey, bool> onManageToggle,
        Action<RoomKey> onGoto)
    {
        ArgumentNullException.ThrowIfNull(onRemove);
        ArgumentNullException.ThrowIfNull(onManageToggle);
        ArgumentNullException.ThrowIfNull(onGoto);
        Key = new RoomKey(label.Map, label.Room);
        RoomName = string.IsNullOrWhiteSpace(roomName) ? "(unknown)" : roomName;
        _onRemove = onRemove;
        _onManageToggle = onManageToggle;
        _onGoto = onGoto;

        string rules = label.Rules.Count == 0
            ? "(no rules)"
            : string.Join("; ", label.Rules.Select(DescribeRule));
        CategoryText = label.IsCatchAll ? $"{rules} [catch-all]" : rules;

        _activelyManaged = activelyManaged;
        _loaded = true;
    }

    partial void OnActivelyManagedChanged(bool value)
    {
        if (!_loaded) return;
        _onManageToggle(Key, value);
    }

    private static string DescribeRule(GhCategoryRule rule)
    {
        if (rule.Worn is int worn)
            return "Slot: " + (LookupEnums.FormatWornSlot(worn) ?? "Unknown");

        string category = LookupEnums.FormatItemType(rule.ItemType?.ToString()) ?? "Unknown";
        if (rule.WeaponType is { } wt)
            category += " > " + (LookupEnums.FormatWeaponType(wt.ToString()) ?? "Unknown");
        else if (rule.ArmourType is { } at)
            category += " > " + (LookupEnums.FormatArmourType(at.ToString()) ?? "Unknown");
        return category;
    }

    [RelayCommand]
    private void Remove() => _onRemove(this);

    // Queue + start a walk-to this room (the full "Walk here" path). Handed up to the
    // section VM, which routes it through AppServices.GoWalkTo.
    [RelayCommand]
    private void Goto() => _onGoto(Key);
}
