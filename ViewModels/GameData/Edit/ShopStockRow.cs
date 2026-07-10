using System.Windows.Input;

namespace FujinTerm.ViewModels.GameData.Edit;

// One row of the room-detail popup's shop table — modelled on MMUD Explorer's
// Shops readout (Name | Max | Regen | Cost) with the single Cost split into Buy
// and Sell. Name is a clickable link that opens the item's Game Data record;
// Regen is the reference's restock phrase ("100% for 10 per 10m", or "no regen");
// Buy / Sell are pre-formatted currency strings priced at retail (50 charm).
public sealed class ShopStockRow
{
    public string Name { get; }
    public string Max { get; }
    public string Regen { get; }
    public string Buy { get; }
    public string Sell { get; }
    public ICommand Open { get; }

    public ShopStockRow(string name, string max, string regen, string buy, string sell, ICommand open)
    {
        Name = name;
        Max = max;
        Regen = regen;
        Buy = buy;
        Sell = sell;
        Open = open;
    }
}
