using System.Collections.Generic;
using System.Text.Json;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

// Game Data Browser → Shops tab. Static MDB shop definitions — the CashManager reads
// ShopType == "Bank" rows as the auto-deposit destinations; the Navigation window references
// shop ids when surfacing per-room actions.
//
// Column names mirror the MajorMUD MDB schema verbatim. Markup% is the buy/sell multiplier,
// ClassRest is a class-bitmask restriction, MinLVL / MaxLVL are the customer-level gates.
// ShopType renders via LookupEnums ("Weapons" / "Armour" / "Bank" / "Tavern" / etc.).
//
// Double-click hops to the Rooms tab and selects the room this shop is attached to (read from
// the row's Assigned To field, formatted as "Room {map}/{room}"). There's no per-shop edit
// dialog — the MDB is the source of truth for shops; if you want to look at the room a shop
// sits in, the Rooms tab is where the rest of the geography lives.
public sealed class ShopsSectionViewModel : JsonTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly GameDataCache _cache;

    public override string Id => "shops";
    public override string Title => "Shops";

    protected override string TableName => "Shops";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Number",
        "Name",
        "ShopType",
        "MinLVL",
        "MaxLVL",
        "Markup%",
        "ClassRest",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "shop", "bank", "merchant", "buy", "sell", "markup", "room",
    };

    protected override IReadOnlyDictionary<string, Func<string?, string?>> ColumnFormatters { get; } =
        new Dictionary<string, Func<string?, string?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ShopType"] = LookupEnums.FormatShopType,
        };

    public IRelayCommand<GameDataRow?> OpenEditAsyncCommand { get; }
    ICommand IEditableTableSectionViewModel.OpenEditCommand => OpenEditAsyncCommand;

    public ShopsSectionViewModel(GameDataCache cache, SettingsResolver? resolver = null)
        : base(cache, resolver)
    {
        _cache = cache;
        OpenEditAsyncCommand = new RelayCommand<GameDataRow?>(JumpToRoom);
    }

    // Resolve the shop's host room from Assigned To = "Room {map}/{room}" and ask the browser to
    // switch to the Rooms tab + highlight the matching entry. No-op when the shop has no Assigned
    // To (a stockless bank, an unbound merchant, etc.) or the map/room pair can't be parsed.
    private void JumpToRoom(GameDataRow? row)
    {
        if (row is null) return;
        string? shopNumberStr = row.Get("Number");
        if (string.IsNullOrEmpty(shopNumberStr)) return;
        if (!int.TryParse(shopNumberStr, out int shopNumber)) return;

        (int mapNo, int roomNo) = ReadAssignedRoom(shopNumber);
        if (mapNo == 0 && roomNo == 0) return;

        // Predicate runs against each Rooms row — Map Number + Room
        // Number live as numeric strings on the row (JSON numbers
        // stringified at load).
        string mapStr  = mapNo.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string roomStr = roomNo.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RequestNavigation("rooms",
            r => string.Equals(r.Get("Map Number"),  mapStr,  StringComparison.Ordinal)
              && string.Equals(r.Get("Room Number"), roomStr, StringComparison.Ordinal));
    }

    // Look up the shop in the active set's Shops.json and parse the FIRST "Room {map}/{room}"
    // token in its Assigned To field. Returns (0,0) on any miss — including when the field is
    // \x00 (unassigned, e.g. an Inn) or doesn't start with "Room ".
    //
    // Some shops (Bank of Godfrey, multi-branch chains) carry a comma-separated list of rooms —
    // "Room 1/297, Room 6/1334". We take the first token; the dialog only jumps to one room
    // anyway, and the rest are accessible via the Rooms tab's search.
    private (int Map, int Room) ReadAssignedRoom(int shopNumber)
    {
        JsonDocument? doc = _cache.GetRawTable("Shops");
        if (doc is null) return (0, 0);

        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("Number", out JsonElement n)) continue;
            if (n.ValueKind != JsonValueKind.Number) continue;
            if (n.GetInt32() != shopNumber) continue;
            if (!el.TryGetProperty("Assigned To", out JsonElement assignedEl)) return (0, 0);
            if (assignedEl.ValueKind != JsonValueKind.String) return (0, 0);

            // Take just the FIRST "Room M/R" token from a comma-separated list.
            // Multi-branch shops (Bank of Godfrey is "Room 1/297, Room 6/1334")
            // would otherwise fail the parse. Shared with the auto-train catalogue.
            return Game.GameData.ShopRoomParser.TryParseFirstRoom(assignedEl.GetString(), out int mapNo, out int roomNo)
                ? (mapNo, roomNo)
                : (0, 0);
        }
        return (0, 0);
    }
}
