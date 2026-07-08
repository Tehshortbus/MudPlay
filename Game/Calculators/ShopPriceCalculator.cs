using System;
using System.Globalization;

namespace FujinTerm.Game.Calculators;

// Pure MajorMUD shop price formulas: item base value → copper, per-shop BUY
// (shop markup + charm) and vendor SELL (charm only; markup-independent, so
// the same at every shop for a given charm). Stock and ParaMUD branches select
// on RealmType, matching how the reference client derives shop costs. All
// methods are pure; callers resolve the active realm from GameDataCache.
//
// The reference applies a legacy 32-bit overflow wrap on charm-scaled totals
// above 4,294,967,295 copper; we deliberately skip it. That wrap reflects a VB
// storage limit the game itself doesn't impose and only corrupts the value of
// absurdly expensive items.
public static class ShopPriceCalculator
{
    // Copper multiplier per MDB Currency code (0 Copper … 4 Runic).
    public static double ToCopper(long price, int currency) => currency switch
    {
        1 => price * 10.0,       // Silver
        2 => price * 100.0,      // Gold
        3 => price * 10000.0,    // Platinum
        4 => price * 1000000.0,  // Runic
        _ => price,              // Copper (0) / unknown
    };

    // VB6 Fix(): truncate toward zero.
    private static double Fix(double v) => Math.Truncate(v);

    // Charm multiplier applied to the buy price (both realms). charm 50 → 1.0
    // (retail); below 50 discounts, above 50 marks up.
    public static double CharmBuyMod(int charm) => 1.0 - ((Fix(charm / 5.0) - 10.0) / 100.0);

    // Per-shop purchase cost in copper: base + shop markup, then charm-scaled.
    public static double BuyCopper(double baseCopper, int markupPercent, int charm)
    {
        double copper = baseCopper;
        if (markupPercent > 0)
            copper += Fix(copper * (markupPercent / 100.0));
        if (charm > 0)
            copper = CharmBuyMod(charm) * copper;
        if (copper <= 0) copper = 0;
        return Math.Round(copper);
    }

    // Vendor sell-back value in copper — ignores shop markup. Charm scaling
    // differs by realm: ParaMUD halves then adjusts by (charm-50)/5 percent;
    // Stock scales by (charm/2 + 25) percent of base.
    public static double SellCopper(double baseCopper, int charm, RealmType realm)
    {
        double copper = baseCopper;
        if (charm > 0)
        {
            if (realm == RealmType.ParaMud)
            {
                copper /= 2.0;
                double mod = Fix((charm - 50) / 5.0);
                copper += (mod * copper) / 100.0;
            }
            else
            {
                double mod = Fix(charm / 2.0) + 25.0;
                copper = mod * copper;
                copper = Fix(copper / 100.0);
            }
        }
        if (copper <= 0) copper = 0;
        return Math.Round(copper);
    }

    // Reduce a copper total to its friendliest denomination, e.g. 1800 → "18 Gold".
    public static string FormatCopper(double copper)
    {
        string coin;
        double reduced;
        if (copper >= 100)
        {
            if (copper >= 10000000) { reduced = copper / 1000000.0; coin = "Runic"; }
            else if (copper >= 100000) { reduced = copper / 10000.0; coin = "Platinum"; }
            else if (copper >= 1000) { reduced = copper / 100.0; coin = "Gold"; }
            else { reduced = copper / 10.0; coin = "Silver"; }
            reduced = Math.Round(reduced, 2);
        }
        else
        {
            coin = "Copper";
            reduced = Math.Round(copper);
        }

        string s = reduced.ToString("#,##0.00", CultureInfo.InvariantCulture);
        if (s.EndsWith(".00", StringComparison.Ordinal)) s = s[..^3];
        return $"{s} {coin}";
    }
}
