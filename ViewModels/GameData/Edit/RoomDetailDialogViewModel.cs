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
//   - the room title + each exit destination centre the Navigation map on that
//     room (opening the window if it's closed),
//   - each monster name jumps to its Game Data record,
//   - Add / Remove buttons toggle the room on the per-BBS blacklist.
public sealed partial class RoomDetailDialogViewModel
    : ObservableObject, IDialogViewModel<bool>
{
    public event Action<bool>? CloseRequested;

    private readonly AppServices _services;
    private readonly RoomKey _key;

    public string Title { get; }
    public string KeyLabel { get; }

    public string MonsterHeader { get; }
    public ObservableCollection<RoomDetailLink> Monsters { get; } = new();
    public bool HasMonsters => Monsters.Count > 0;

    public ObservableCollection<RoomDetailLink> Exits { get; } = new();
    public bool HasExits => Exits.Count > 0;

    public string ExtrasText { get; }
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
        _key = key;

        Room? room = services.RoomGraph.GetRoom(key);
        if (room is null)
        {
            Title = $"Room {key}";
            KeyLabel = key.ToString();
            MonsterHeader = string.Empty;
            ExtrasText = $"No room record for {key} in the active game-data set.";
            IsBlacklisted = services.RoomBlacklist.IsBlacklisted(key);
            return;
        }

        Title = room.DisplayName;
        KeyLabel = $"Map {key.Map} · Room {key.Room}";
        IsBlacklisted = services.RoomBlacklist.IsBlacklisted(key);

        // Monsters — lair + summoned (shared resolver), then the placed NPC the
        // tooltip's "Also Here" deliberately omits (a boss / shopkeeper lives on
        // the room's Npc field, not its lair tag).
        IReadOnlyList<RoomTooltipBuilder.RoomMonsterRef> alsoHere =
            RoomTooltipBuilder.ResolveAlsoHere(room, services.GameData, services.MonsterSpawns, out int? max);
        MonsterHeader = max is { } m ? $"Also here (max {m}):" : "Also here:";

        var monsterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (RoomTooltipBuilder.RoomMonsterRef mref in alsoHere)
        {
            monsterNames.Add(mref.Name);
            Monsters.Add(MakeMonsterLink(mref.Id, mref.Name, note: null));
        }
        if (room.Npc > 0)
        {
            string placed = services.GameData.FindNameByNumber("Monsters", room.Npc)
                ?? $"#{room.Npc}";
            if (monsterNames.Add(placed))
                Monsters.Add(MakeMonsterLink(room.Npc, placed, note: "placed"));
        }

        // Exits — one clickable row per obvious exit, using the same ordering +
        // hint rendering as the map tooltip.
        foreach ((Direction dir, RoomExit exit) in RoomTooltipBuilder.OrderedExits(room))
        {
            Room? dest = services.RoomGraph.GetRoom(exit.Target);
            string destName = dest is not null ? dest.DisplayName : exit.Target.ToString();
            string label = $"{RoomTooltipBuilder.DirectionLabel(dir)} → {destName} ({exit.Target})";
            string hint = RoomTooltipBuilder.FormatExitHint(exit, services.GameData);
            RoomKey target = exit.Target;
            Exits.Add(new RoomDetailLink(
                label,
                hint.Length > 0 ? hint : null,
                new RelayCommand(() => _services.NavigateToRoom(target))));
        }

        ExtrasText = RoomTooltipBuilder.BuildDetailExtras(
            room, services.RoomGraph, services.GameData, services.TBInfo,
            services.SpellCatalog, services.PlayerIllumination.Current);
    }

    private RoomDetailLink MakeMonsterLink(int id, string name, string? note)
        => new(name, note, new RelayCommand(() => _services.OpenMonsterGameData(id)));

    // Title / key click — open (or focus) the Navigation map and centre it here.
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
