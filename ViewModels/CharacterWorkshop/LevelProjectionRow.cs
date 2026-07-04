using System.Globalization;
using FujinTerm.Game.Calculators;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// One display row in the Level Projection grid — formats a LevelProjection into
// the grid's string columns and carries the IsCurrentLevel flag the view uses to
// highlight the live character's current level.
public sealed class LevelProjectionRow
{
    public int Level { get; }
    // Exp remaining to reach this level from the character's current exp
    // (max(0, TotalXp − currentExp)). 0 once you already hold enough — equals
    // TotalXp when current exp is 0.
    public string ExpToLevel { get; }
    // Cumulative exp threshold to reach this level (absolute).
    public string TotalXp { get; }
    public string HpRange { get; }
    public string HpRegen { get; }
    // Max mana (or Kai for Mystics); "—" for non-casters.
    public string Mana { get; }
    // Mana/Kai regen per tick; "—" for non-casters.
    public string MpRegen { get; }
    // True when this row is the live character's current level.
    public bool IsCurrentLevel { get; }

    public LevelProjectionRow(LevelProjection p, long currentExp, bool isCurrentLevel, bool isCaster)
    {
        Level = p.Level;
        TotalXp = FormatExp(p.TotalXp);
        long remaining = p.TotalXp == long.MaxValue
            ? long.MaxValue
            : System.Math.Max(0, p.TotalXp - currentExp);
        ExpToLevel = FormatExp(remaining);
        HpRange = p.HpMin == p.HpMax
            ? p.HpMin.ToString("N0", CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{p.HpMin:N0}–{p.HpMax:N0}");
        HpRegen = p.HpRegen.ToString(CultureInfo.InvariantCulture);
        Mana = isCaster ? p.Mana.ToString("N0", CultureInfo.InvariantCulture) : "—";
        MpRegen = isCaster ? p.MpRegen.ToString(CultureInfo.InvariantCulture) : "—";
        IsCurrentLevel = isCurrentLevel;
    }

    // The exp formulas saturate at long.MaxValue instead of overflowing; show
    // that ceiling as ∞ rather than a meaningless 9.2-quintillion figure.
    private static string FormatExp(long value) =>
        value == long.MaxValue ? "∞" : value.ToString("N0", CultureInfo.InvariantCulture);
}
