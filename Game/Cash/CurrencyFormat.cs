using System.Globalization;

namespace FujinTerm.Game.Cash;

// Formats a copper-farthing amount as MajorMUD coin denominations. The Session
// Stats window uses it so the currency total and per-hour rate read as coins
// ("10 gold/hr") instead of a raw comma-grouped copper count ("1,000/hr").
//
// The ladder mirrors CurrencyHoldings.ToCopper — copper 1, silver 10, gold 100,
// platinum 10 000, runic 1 000 000 — the single source of truth for the ratio.
// Names are the stock denomination words; callers on a realm that renames the
// runic word (Settings → BBS) pass it through the optional runicName argument so
// the top rung reads the board's label instead of "runic". Denominate renders the
// abbreviated column (plat / silv / copp) to fit the narrow Session Stats cells;
// Full keeps the whole words so its tooltip still mirrors the game's wealth line.
public static class CurrencyFormat
{
    // Copper worth of the top (runic) rung — the only denomination whose label
    // is board-renameable.
    private const long RunicWorth = 1_000_000;
    // Largest denomination first, so the flip-up scan takes the first rung the
    // amount clears. Short is Denominate's compact label; Name is Full's whole word.
    private static readonly (long Worth, string Name, string Short)[] Ladder =
    {
        (1_000_000, "runic",    "runic"),
        (10_000,    "platinum", "plat"),
        (100,       "gold",     "gold"),
        (10,        "silver",   "silv"),
        (1,         "copper",   "copp"),
    };

    // Compact single-denomination text: the coin "flips up" as the amount grows,
    // showing the largest denomination that has at least one whole unit
    // (1000 copper -> "10 gold", 1050 -> "10.5 gold"). Fits the narrow table
    // columns where a full itemised wealth line wouldn't. Negatives clamp to 0.
    public static string Denominate(double copper, string? runicName = null)
    {
        if (copper <= 0) return "0 copp";
        foreach ((long worth, _, string shrt) in Ladder)
        {
            if (copper >= worth)
                return string.Create(CultureInfo.InvariantCulture, $"{Trim(copper / worth)} {LabelFor(worth, shrt, runicName)}");
        }
        return "0 copp"; // unreachable: any copper >= 1 clears the copper rung
    }

    // Fully itemised wealth line, largest denomination first, zero rungs skipped
    // (1_930_506 -> "1 runic 93 platinum 5 gold 6 copper"). Mirrors the game's
    // wealth breakdown; used as the exact-value tooltip behind the compact text.
    public static string Full(long copper, string? runicName = null)
    {
        if (copper <= 0) return "0 copper";
        List<string> parts = new();
        long remaining = copper;
        foreach ((long worth, string name, _) in Ladder)
        {
            long count = remaining / worth;
            if (count > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{count} {LabelFor(worth, name, runicName)}"));
                remaining -= count * worth;
            }
        }
        return string.Join(" ", parts);
    }

    // Board-renamed label for the top rung; every other rung keeps its stock
    // word. Blank/whitespace runicName falls back to the stock "runic".
    private static string LabelFor(long worth, string stockName, string? runicName) =>
        worth == RunicWorth && !string.IsNullOrWhiteSpace(runicName) ? runicName : stockName;

    // Integer when whole, else one decimal — keeps "10 gold" clean while still
    // distinguishing "10.5 gold" / "1.2 runic".
    private static string Trim(double value)
    {
        double rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        return rounded == Math.Floor(rounded)
            ? ((long)rounded).ToString("0", CultureInfo.InvariantCulture)
            : rounded.ToString("0.0", CultureInfo.InvariantCulture);
    }
}
