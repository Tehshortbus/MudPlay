namespace FujinTerm.Game.Map;

// Stable identity of a room — the (Map, Room) pair MajorMUD uses as a primary
// key everywhere it references a room (exit targets in Rooms.json, lair group
// indices, the in-game #room number visible to a connected player).
//
// Wire format matches the MDB exit-cell encoding: "{map}/{room}" — e.g. "1/3"
// for Map 1 / Room 3. TryParseWire accepts that exact form. The "0" sentinel
// used in the MDB to mean "no exit" is the caller's concern; this type only
// represents real keys.
public readonly record struct RoomKey(int Map, int Room)
{
    // Wire encoding ("{Map}/{Room}"). Round-trips via TryParseWire.
    public override string ToString() => $"{Map}/{Room}";

    // Parse the "{map}/{room}" encoding used by exit cells in Rooms.json. Strips
    // a trailing parenthetical hint (e.g. "1/3 (Door)") before parsing, but
    // doesn't surface the hint — callers reading exit cells must hand the raw
    // value to RoomExit.TryParseWire if they want the hint.
    public static bool TryParseWire(string? wire, out RoomKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(wire)) return false;

        // Trim trailing "(Door)" / "(Trap)" / etc. before the split.
        ReadOnlySpan<char> span = wire.AsSpan().Trim();
        int paren = span.IndexOf('(');
        if (paren >= 0) span = span[..paren].TrimEnd();

        int slash = span.IndexOf('/');
        if (slash <= 0 || slash == span.Length - 1) return false;

        if (!int.TryParse(span[..slash], out int map)) return false;
        if (!int.TryParse(span[(slash + 1)..], out int room)) return false;
        if (map <= 0 || room <= 0) return false;

        key = new RoomKey(map, room);
        return true;
    }
}
