using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Edit;

// One "bought / sold" entry in an item's read-only MDB info: the shop's host
// room (name + map/room locator, plus any SELL / NO GEN flag) and the charm-
// priced buy/sell line beneath it. The location is a clickable link that jumps
// the Game Data browser to that room's Rooms-tab record — the item pane doubles
// as a jump-off to where the item is traded (mirrors ItemLink / ShopStockRow).
public sealed class ShopSaleRow
{
    public string Location { get; }
    public string Price { get; }
    public bool HasPrice => Price.Length > 0;

    // False when the shop's host room couldn't be resolved (map <= 0) — the row
    // still shows the raw "Shop #N" fallback but has nothing to navigate to, so
    // the view greys the link.
    public bool CanOpen { get; }
    public ICommand Open { get; }

    public ShopSaleRow(string location, string price, int map, int room)
    {
        Location = location;
        Price = price;
        CanOpen = map > 0 && room > 0;
        Open = new RelayCommand(
            () => AppServices.Current.OpenRoomGameData(map, room),
            () => CanOpen);
    }
}
