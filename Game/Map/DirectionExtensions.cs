namespace FujinTerm.Game.Map;

// Display helpers for Direction — the single home for the enum →
// human-readable name mapping that UI labels and @-command replies share,
// so the wording can't drift between surfaces.
public static class DirectionExtensions
{
    // Long-form lowercase name as MajorMUD prints it in the
    // "Obvious exits:" line — "north", "southeast", "up". Matches
    // RoomDisplayParser's inbound token vocabulary so a reply reads in the
    // same words the game itself uses.
    public static string ToLongName(this Direction d) => d switch
    {
        Direction.N  => "north",
        Direction.S  => "south",
        Direction.E  => "east",
        Direction.W  => "west",
        Direction.NE => "northeast",
        Direction.NW => "northwest",
        Direction.SE => "southeast",
        Direction.SW => "southwest",
        Direction.U  => "up",
        Direction.D  => "down",
        _            => d.ToString(),
    };
}
