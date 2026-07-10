using System.Collections.Generic;
using System.Globalization;

namespace FujinTerm.Game.Inventory;

// Parses the body of the shop `list` readout into live stock rows. The captured
// in-game format is a fixed-width three-column table:
//
//   The following items are for sale here:
//
//   Item                    Quantity        Price
//   -----------------------------------------------
//   torch                   250             Free
//   rope and grapple        56              10 gold crowns
//   crowbar                 35              6 gold crowns (You can't use)
//
// Item names carry spaces ("rope and grapple"), so the columns can't be split on
// whitespace — the parser locates the Quantity / Price column offsets from the
// header row and slices each data row at those fixed positions (the monospace
// grid guarantees alignment). The trailing "(You can't use)" usability hint is
// left in the Price text and ignored by callers — it does not gate auto-buy.
public static class ShopListParser
{
    public readonly record struct StockRow(string Name, int Quantity, string Price);

    public static IReadOnlyList<StockRow> Parse(IReadOnlyList<string> bodyLines)
    {
        List<StockRow> rows = new();
        int qtyCol = -1;
        int priceCol = -1;

        foreach (string raw in bodyLines)
        {
            string line = raw.TrimEnd();
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            if (qtyCol < 0)
            {
                // The column-header row establishes the fixed slice offsets; skip
                // everything (blank lead-in included) until it's found.
                int q = line.IndexOf("Quantity", System.StringComparison.Ordinal);
                int p = line.IndexOf("Price", System.StringComparison.Ordinal);
                if (q >= 0 && p > q) { qtyCol = q; priceCol = p; }
                continue;
            }

            if (IsSeparator(trimmed)) continue;
            if (TryParseRow(line, qtyCol, priceCol, out StockRow row)) rows.Add(row);
        }

        return rows;
    }

    private static bool IsSeparator(string trimmed)
    {
        foreach (char c in trimmed)
            if (c != '-') return false;
        return true;
    }

    private static bool TryParseRow(string line, int qtyCol, int priceCol, out StockRow row)
    {
        row = default;
        if (line.Length <= qtyCol) return false;

        string name = line[..qtyCol].Trim();
        if (name.Length == 0) return false;

        int qtyEnd = System.Math.Min(priceCol, line.Length);
        string qtyStr = line[qtyCol..qtyEnd].Trim();
        if (!int.TryParse(qtyStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int qty)) return false;

        string price = priceCol < line.Length ? line[priceCol..].Trim() : string.Empty;
        row = new StockRow(name, qty, price);
        return true;
    }
}
