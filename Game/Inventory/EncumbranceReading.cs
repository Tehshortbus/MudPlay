namespace FujinTerm.Game.Inventory;

// Numeric carry-weight reading parsed from the game's
// "Encumbrance:  <cur>/<max>  -  <Category>  [<pct>%]" line. The bracket
// EncumbranceParser already exposes as PlayerState.Encumbrance is the display
// level; this reading carries the raw numbers the cash engine needs to gate
// pickups against the next encumbrance boundary. Category is the bracket the
// game reported (full parse) or the engine derived from Percentage between full
// parses.
public readonly record struct EncumbranceReading(
    int CurrentWeight,
    int MaxWeight,
    int Percentage,
    EncumbranceLevel Category)
{
    // Never-observed reading (all zero, Unknown bracket).
    public static EncumbranceReading Empty => new(0, 0, 0, EncumbranceLevel.Unknown);
}
