namespace FujinTerm.Game.Inventory;

/// <summary>
/// Numeric carry-weight reading parsed from the game's
/// <c>Encumbrance:  &lt;cur&gt;/&lt;max&gt;  -  &lt;Category&gt;  [&lt;pct&gt;%]</c>
/// line. The bracket <see cref="EncumbranceParser"/> already exposes as
/// <see cref="PlayerState.Encumbrance"/> is the display level; this reading
/// carries the raw numbers the cash engine needs to gate pickups against
/// the next encumbrance boundary.
/// </summary>
/// <param name="CurrentWeight">Current carry weight.</param>
/// <param name="MaxWeight">Maximum carry capacity.</param>
/// <param name="Percentage">Carry weight as a 0–100 percentage of max.</param>
/// <param name="Category">
/// Bracket the game reported (full parse) or the engine derived from
/// <see cref="Percentage"/> between full parses.
/// </param>
public readonly record struct EncumbranceReading(
    int CurrentWeight,
    int MaxWeight,
    int Percentage,
    EncumbranceLevel Category)
{
    /// <summary>Never-observed reading (all zero, Unknown bracket).</summary>
    public static EncumbranceReading Empty => new(0, 0, 0, EncumbranceLevel.Unknown);
}
