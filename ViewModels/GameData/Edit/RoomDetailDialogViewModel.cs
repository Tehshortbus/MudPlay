using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Edit;

// Interactive "everything attached to this room" popup — opened from the Rooms
// tab (row double-click) and the Monsters tab's spawn/placed/summoned room
// chips. Reuses RoomTooltipBuilder for the descriptive tail (shop / room spell /
// light / room commands / regen) so the popup never drifts from the Navigation
// map hover, and layers clickable affordances on top:
//   - the room title centres the Navigation map on that room (opening the
//     window if it's closed),
//   - each exit destination re-roots the popup itself on the neighbour and, if
//     the map is already open, lets it follow along (never force-opens it),
//   - each monster name jumps to its Game Data record,
//   - Add / Remove buttons toggle the room on the per-BBS blacklist.
public sealed partial class RoomDetailDialogViewModel
    : ObservableObject, IDialogViewModel<bool>
{
    public event Action<bool>? CloseRequested;

    private readonly AppServices _services;
    private RoomKey _key;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _keyLabel = string.Empty;

    [ObservableProperty]
    private string _monsterHeader = string.Empty;

    public ObservableCollection<RoomDetailLink> Monsters { get; } = new();
    public bool HasMonsters => Monsters.Count > 0;

    public ObservableCollection<RoomDetailLink> Exits { get; } = new();
    public bool HasExits => Exits.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExtras))]
    private string _extrasText = string.Empty;
    public bool HasExtras => ExtrasText.Length > 0;

    // Blacklist toggle state — drives the two mutually-exclusive buttons.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddToBlacklist))]
    [NotifyPropertyChangedFor(nameof(CanRemoveFromBlacklist))]
    private bool _isBlacklisted;

    public bool CanAddToBlacklist => !IsBlacklisted;
    public bool CanRemoveFromBlacklist => IsBlacklisted;

    public RoomDetailDialogViewModel(AppServices services, RoomKey key)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        LoadRoom(key);
    }

    // Populate every section for a room. Called once at construction and again
    // whenever an exit is clicked, so the same window walks room-to-room.
    private void LoadRoom(RoomKey key)
    {
        _key = key;
        Monsters.Clear();
        Exits.Clear();

        Room? room = _services.RoomGraph.GetRoom(key);
        if (room is null)
        {
            Title = $"Room {key}";
            KeyLabel = key.ToString();
            MonsterHeader = string.Empty;
            ExtrasText = $"No room record for {key} in the active game-data set.";
            IsBlacklisted = _services.RoomBlacklist.IsBlacklisted(key);
            RaiseSectionVisibility();
            return;
        }

        Title = room.DisplayName;
        KeyLabel = $"Map {key.Map} · Room {key.Room}";
        IsBlacklisted = _services.RoomBlacklist.IsBlacklisted(key);

        // Monsters — lair + summoned (shared resolver), then the placed NPC the
        // tooltip's "Also Here" deliberately omits (a boss / shopkeeper lives on
        // the room's Npc field, not its lair tag).
        IReadOnlyList<RoomTooltipBuilder.RoomMonsterRef> alsoHere =
            RoomTooltipBuilder.ResolveAlsoHere(room, _services.GameData, _services.MonsterSpawns, out int? max);
        MonsterHeader = max is { } m ? $"Also here (max {m}):" : "Also here:";

        var monsterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (RoomTooltipBuilder.RoomMonsterRef mref in alsoHere)
        {
            monsterNames.Add(mref.Name);
            Monsters.Add(MakeMonsterLink(mref.Id, mref.Name, note: null));
        }
        if (room.Npc > 0)
        {
            string placed = _services.GameData.FindNameByNumber("Monsters", room.Npc)
                ?? $"#{room.Npc}";
            if (monsterNames.Add(placed))
                Monsters.Add(MakeMonsterLink(room.Npc, placed, note: "placed"));
        }

        // Exits — one clickable row per obvious exit, using the same ordering +
        // hint rendering as the map tooltip.
        foreach ((Direction dir, RoomExit exit) in RoomTooltipBuilder.OrderedExits(room))
        {
            Room? dest = _services.RoomGraph.GetRoom(exit.Target);
            string destName = dest is not null ? dest.DisplayName : exit.Target.ToString();
            string label = $"{RoomTooltipBuilder.DirectionLabel(dir)} → {destName} ({exit.Target})";
            string hint = RoomTooltipBuilder.FormatExitHint(exit, _services.GameData);
            RoomKey target = exit.Target;
            Exits.Add(new RoomDetailLink(
                label,
                hint.Length > 0 ? hint : null,
                new RelayCommand(() => OnExitClicked(target))));
        }

        ExtrasText = RoomTooltipBuilder.BuildDetailExtras(
            room, _services.RoomGraph, _services.GameData, _services.TBInfo,
            _services.SpellCatalog, _services.PlayerIllumination.Current);

        RaiseSectionVisibility();
    }

    // HasMonsters / HasExits track collection counts, not observable fields, so
    // a re-load has to poke their bindings by hand.
    private void RaiseSectionVisibility()
    {
        OnPropertyChanged(nameof(HasMonsters));
        OnPropertyChanged(nameof(HasExits));
    }

    // Exit click — walk the popup to the neighbour and let an already-open map
    // follow, without dragging the map onto the screen when it's closed.
    private void OnExitClicked(RoomKey target)
    {
        LoadRoom(target);
        _services.CenterNavigationIfOpen(target);
    }

    private RoomDetailLink MakeMonsterLink(int id, string name, string? note)
        => new(name, note, new RelayCommand(() => _services.OpenMonsterGameData(id)));

    // Title / key click — open (or focus) the Navigation map and centre it on
    // whichever room the popup is currently showing.
    [RelayCommand]
    private void OpenInNavigation() => _services.NavigateToRoom(_key);

    [RelayCommand]
    private void AddToBlacklist()
    {
        if (IsBlacklisted) return;
        _services.RoomBlacklist.Add(_key, Title);          // fires Changed → map redraws
        IsBlacklisted = true;
    }

    [RelayCommand]
    private void RemoveFromBlacklist()
    {
        if (!IsBlacklisted) return;
        _services.RoomBlacklist.Remove(_key);              // fires Changed → map redraws
        IsBlacklisted = false;
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(true);
}
