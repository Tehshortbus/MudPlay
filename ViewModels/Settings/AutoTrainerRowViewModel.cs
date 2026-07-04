using System;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game.GameData;

namespace FujinTerm.ViewModels.Settings;

// One discovered trainer room in the Settings → Auto-Trainer table: a room+shop
// label, host map/room, served level range, and the per-row Use? toggle that
// decides whether auto-train may route to it. Carries the raw TrainerShop so the
// section's level filter can evaluate it. Flipping Allowed marks the parent
// section dirty via the supplied callback.
public sealed partial class AutoTrainerRowViewModel : ObservableObject
{
    private readonly TrainerShop _shop;
    private readonly Action _onAllowedChanged;

    // Stable per-row key (shop/map/room) — the persistence key for the disabled set.
    public string RowKey => _shop.RowKey;

    // Display label: "<room name> - <shop name>". Always pairs the host room with
    // the trainer shop so a multi-room shop's rows read consistently (e.g. the
    // Druid trainer's "Druid Training Room - Druid Training Room" and "Druid's
    // Training Room - Druid Training Room" both show the shop), even when the room
    // and shop happen to share a name. Falls back to the shop name alone only when
    // the room name didn't resolve.
    public string TrainerLabel
    {
        get
        {
            string room = _shop.RoomName.Trim();
            string shop = _shop.Name.Trim();
            return room.Length == 0 ? shop : $"{room} - {shop}";
        }
    }

    // Host room as "map/room", or "—" when the shop has no resolvable room.
    public string MapRoom { get; }
    // Served level range as "min–max".
    public string LevelRange { get; }

    [ObservableProperty] private bool _allowed;

    public AutoTrainerRowViewModel(TrainerShop shop, string mapRoom, string levelRange,
                                   bool allowed, Action onAllowedChanged)
    {
        ArgumentNullException.ThrowIfNull(onAllowedChanged);
        _shop = shop;
        MapRoom = mapRoom;
        LevelRange = levelRange;
        _allowed = allowed;
        _onAllowedChanged = onAllowedChanged;
    }

    // True when this trainer serves a character at the given level.
    public bool ServesLevel(int level) => _shop.ServesLevel(level);

    // True when this trainer serves the given class number — either the universal
    // Training Room (ClassRest == 0) or the matching class guild. Used to hide
    // other classes' guild trainers from the list.
    public bool ServesClass(int classNumber) => _shop.ServesClass(classNumber);

    partial void OnAllowedChanged(bool value) => _onAllowedChanged();
}
