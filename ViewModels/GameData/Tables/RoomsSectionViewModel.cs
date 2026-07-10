using System.Collections.Generic;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.ViewModels.GameData.Edit;

namespace FujinTerm.ViewModels.GameData.Tables;

// Game Data Browser → Rooms tab. Renders the imported MajorMUD Rooms table — fuel for the
// RoomGraphManager (seeded from Rooms + the embedded N / S / E / W / NE / NW / SE / SW / U / D
// exit fields at import time) and the Workshop's room-name lookups.
//
// MajorMUD MDBs store room exits inline on the Rooms row — there's no separate Paths table —
// so each direction is a column carrying the destination room number (or 0 / blank for a
// wall). The listing surfaces the most diagnostic fields plus the four cardinal exits.
//
// Double-click a row → open the read-only room-detail popup (the same
// everything-attached-to-this-room text the Navigation map hover shows: exits +
// requirements, illumination, shop, room spell, placed/spawned monsters, room commands).
public sealed class RoomsSectionViewModel : JsonTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly DialogService? _dialogs;

    public override string Id => "rooms";
    public override string Title => "Rooms";

    protected override string TableName => "Rooms";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Map Number",
        "Room Number",
        "Name",
        "Light",
        "Shop",
        "NPC",
        "CMD",
        "Lair",
        "N", "S", "E", "W",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "room", "map", "area", "shop", "lair", "exit",
    };

    public IRelayCommand<GameDataRow?> OpenDetailCommand { get; }
    ICommand IEditableTableSectionViewModel.OpenEditCommand => OpenDetailCommand;

    public RoomsSectionViewModel(GameDataCache cache, SettingsResolver? resolver = null, DialogService? dialogs = null)
        : base(cache, resolver)
    {
        _dialogs = dialogs;
        OpenDetailCommand = new RelayCommand<GameDataRow?>(ShowRoomDetail);
    }

    // Resolve the row's (Map Number, Room Number) and pop the room-detail popup.
    // No-op when the DialogService is absent (design-time) or the pair can't be parsed.
    private void ShowRoomDetail(GameDataRow? row)
    {
        if (row is null || _dialogs is null) return;
        if (!int.TryParse(row.Get("Map Number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int map))
            return;
        if (!int.TryParse(row.Get("Room Number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int room))
            return;
        RoomDetailPopup.Show(_dialogs, new RoomKey(map, room));
    }
}
