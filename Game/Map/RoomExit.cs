namespace FujinTerm.Game.Map;

/// <summary>
/// One outgoing exit from a room: the target <see cref="RoomKey"/>
/// plus a parsed <see cref="RoomExitHint"/> and the raw parenthetical
/// text from the MDB cell (preserved so an unknown hint can still be
/// surfaced for diagnostics or rendered on the map legend).
/// </summary>
public readonly record struct RoomExit(RoomKey Target, RoomExitHint Hint, string? RawHint)
{
    /// <summary>
    /// Parse a single MDB exit cell. Returns <c>false</c> for the
    /// <c>"0"</c> sentinel ("no exit"), for null/whitespace, and for
    /// malformed cells.
    /// </summary>
    /// <remarks>
    /// Hint vocabulary is conservative on purpose — see
    /// <see cref="RoomExitHint"/>. An unrecognised parenthetical
    /// (e.g. a future <c>(Climb)</c>) round-trips through
    /// <see cref="RawHint"/> as a non-null string while
    /// <see cref="Hint"/> stays <see cref="RoomExitHint.None"/>.
    /// </remarks>
    public static bool TryParseWire(string? wire, out RoomExit exit)
    {
        exit = default;
        if (string.IsNullOrWhiteSpace(wire)) return false;

        string trimmed = wire.Trim();
        if (trimmed == "0") return false;

        string? rawHint = null;
        string keyPart = trimmed;

        int paren = trimmed.IndexOf('(');
        if (paren >= 0)
        {
            int close = trimmed.IndexOf(')', paren + 1);
            if (close > paren)
            {
                rawHint = trimmed.Substring(paren + 1, close - paren - 1).Trim();
                keyPart = trimmed[..paren].TrimEnd();
            }
        }

        if (!RoomKey.TryParseWire(keyPart, out RoomKey key)) return false;

        RoomExitHint hint = ClassifyHint(rawHint);
        exit = new RoomExit(key, hint, rawHint);
        return true;
    }

    private static RoomExitHint ClassifyHint(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return RoomExitHint.None;
        if (raw.Equals("Door", StringComparison.OrdinalIgnoreCase)) return RoomExitHint.Door;
        if (raw.Equals("Trap", StringComparison.OrdinalIgnoreCase)) return RoomExitHint.Trap;
        return RoomExitHint.None;
    }
}
