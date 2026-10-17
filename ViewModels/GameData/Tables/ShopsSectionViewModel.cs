using System.Collections.Generic;
using System.Text.Json;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels.GameData.Tables;

// Game Data Browser → Shops tab. Static MDB shop definitions — the CashManager reads
// ShopType == "Bank" rows as the auto-deposit destinations; the Navigation window references
// shop ids when surfacing per-room actions.
//
// Column names mirror the MajorMUD MDB schema verbatim. Markup% is the buy/sell multiplier,
// ClassRest is a class-bitmask restriction, MinLVL / MaxLVL are the customer-level gates.
// ShopType renders via LookupEnums ("Weapons" / "Armour" / "Bank" / "Tavern" / etc.).
//
// Double-click opens the interactive room-detail popup for the room this shop sits in (read
// from the row's Assigned To field, formatted as "Room {map}/{room}") — the same popup the
// Navigation ROOM INFO panel's shop link opens, showing the shop's stock, exits, and
// neighbours. A shop that spans several rooms opens on the first and lists the rest as
// clickable siblings in the popup's title.
// There's no per-shop edit dialog — the MDB is the source of truth for shops.
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
        OpenEditAsyncCommand = new RelayCommand<GameDataRow?>(OpenRoomPopup);
    }

    // Resolve the shop's host room from Assigned To = "Room {map}/{room}" and open the interactive
    // room-detail popup on it (the popup itself surfaces the shop's other rooms as clickable
    // siblings, resolved from each room's Shop field). No-op when the shop has no Assigned To
    // (a stockless bank, an unbound merchant, etc.) or the map/room pair can't be parsed.
    private void OpenRoomPopup(GameDataRow? row)
    {
        if (row is null) return;
        string? shopNumberStr = row.Get("Number");
        if (string.IsNullOrEmpty(shopNumberStr)) return;
        if (!int.TryParse(shopNumberStr, out int shopNumber)) return;

        (int mapNo, int roomNo) = ReadAssignedRoom(shopNumber);
        if (mapNo == 0 && roomNo == 0) return;

        RoomDetailPopup.Show(AppServices.Current.Dialogs, new RoomKey(mapNo, roomNo));
    }

    // Look up the shop in the active set's Shops.json and parse the FIRST "Room {map}/{room}"
    // token in its Assigned To field. Returns (0,0) on any miss — including when the field is
    // \x00 (unassigned, e.g. an Inn) or doesn't start with "Room ".
    //
    // Some shops (Bank of Godfrey, multi-branch chains) carry a comma-separated list of rooms —
    // "Room 1/297, Room 6/1334". We take the first token; the popup lists the shop's other
    // rooms as clickable siblings.
    private (int Map, int Room) ReadAssignedRoom(int shopNumber)
    {
        GameTable? doc = _cache.GetTable("Shops");
        if (doc is null) return (0, 0);

        foreach (GameRow el in doc.Rows)
        {
            if (!el.TryGetProperty("Number", out GameValue n)) continue;
            if (n.ValueKind != JsonValueKind.Number) continue;
            if (n.GetInt32() != shopNumber) continue;
            if (!el.TryGetProperty("Assigned To", out GameValue assignedEl)) return (0, 0);
            if (assignedEl.ValueKind != JsonValueKind.String) return (0, 0);

            // Take just the FIRST "Room M/R" token from a comma-separated list — the popup
            // opens on it and surfaces the shop's other rooms as clickable siblings itself.
            // Multi-branch shops (Bank of Godfrey is "Room 1/297, Room 6/1334") would
            // otherwise fail the parse. Shared with the auto-train catalogue.
            return Game.GameData.ShopRoomParser.TryParseFirstRoom(assignedEl.GetString(), out int mapNo, out int roomNo)
                ? (mapNo, roomNo)
                : (0, 0);
        }
        return (0, 0);
    }
}
