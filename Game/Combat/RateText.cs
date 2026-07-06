using System.Globalization;

namespace FujinTerm.Game.Combat;

// Compact per-hour rate formatting shared by the Session Stats window and the
// main-window looping chip: large figures abbreviate to k / M so they fit a
// narrow chip without a comma-grouped run of digits (5749 -> "5.7k"). Invariant
// culture so the decimal point stays a dot on every locale.
public static class RateText
{
    // e.g. 5749 -> "5.7k", 1_200_000 -> "1.2M", 42 -> "42", <=0 -> "0".
    public static string Compact(double value)
    {
        if (value <= 0) return "0";
        return value switch
        {
            >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000_000d:0.#}M"),
            >= 1_000     => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000d:0.#}k"),
            _            => value.ToString("F0", CultureInfo.InvariantCulture),
        };
    }
}
