using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

// Game Data Browser → Rooms tab. Renders the imported MajorMUD Rooms table — fuel for the
// RoomGraphManager (seeded from Rooms + the embedded N / S / E / W / NE / NW / SE / SW / U / D
// exit fields at import time) and the Workshop's room-name lookups.
//
// MajorMUD MDBs store room exits inline on the Rooms row — there's no separate Paths table —
// so each direction is a column carrying the destination room number (or 0 / blank for a
// wall). The listing surfaces the most diagnostic fields plus the four cardinal exits; the
// per-room editor will show the full schema including all eight horizontal exits plus
// up / down.
public sealed class RoomsSectionViewModel : JsonTableSectionViewModel
{
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

    public RoomsSectionViewModel(GameDataCache cache, SettingsResolver? resolver = null) : base(cache, resolver) { }
}
