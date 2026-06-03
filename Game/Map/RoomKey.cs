namespace FujinTerm.Game.Map;

/// <summary>
/// Stable identity of a room — the <c>(Map, Room)</c> pair MajorMUD uses
/// as a primary key everywhere it references a room (exit targets in
/// <c>Rooms.json</c>, lair group indices, the in-game <c>#room</c>
/// number visible to a connected player).
/// </summary>
/// <remarks>
/// Wire format matches the MDB exit-cell encoding: <c>"{map}/{room}"</c>
/// — e.g. <c>"1/3"</c> for Map 1 / Room 3. <see cref="TryParseWire"/>
/// accepts that exact form. The <c>"0"</c> sentinel used in the MDB to
/// mean "no exit" is the caller's concern; this type only represents
/// real keys.
/// </remarks>
public readonly record struct RoomKey(int Map, int Room)
{
    /// <summary>Wire encoding (<c>"{Map}/{Room}"</c>). Round-trips via <see cref="TryParseWire"/>.</summary>
    public override string ToString() => $"{Map}/{Room}";

    /// <summary>
    /// Parse the <c>"{map}/{room}"</c> encoding used by exit cells in
    /// <c>Rooms.json</c>. Strips a trailing parenthetical hint
    /// (e.g. <c>"1/3 (Door)"</c>) before parsing, but doesn't surface
    /// the hint — callers reading exit cells must hand the raw value
    /// to <see cref="RoomExit.TryParseWire"/> if they want the hint.
    /// </summary>
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
