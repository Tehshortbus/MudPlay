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

        // Real MDB hints are prefix-tagged with optional trailing
        // detail — e.g. "Trap, 30 damage", "Trap, 45 damage",
        // "Spell Trap: 905", "Door", "Door 1234", etc. Match by
        // prefix so the detail variants all classify correctly.
        if (raw.StartsWith("Spell Trap", StringComparison.OrdinalIgnoreCase)
         || raw.StartsWith("Trap",       StringComparison.OrdinalIgnoreCase))
            return RoomExitHint.Trap;

        if (raw.StartsWith("Door", StringComparison.OrdinalIgnoreCase))
            return RoomExitHint.Door;

        // Other gated-exit categories (Key / Level / Class / Race /
        // Alignment / Hidden / Item / Cast / Ticket / Timed / Toll /
        // Ability / Max / Text) round-trip through RawHint for the
        // editor; the map doesn't surface them as a distinct stub
        // colour yet. Add Hint values when the user calls them out.
        return RoomExitHint.None;
    }
}
