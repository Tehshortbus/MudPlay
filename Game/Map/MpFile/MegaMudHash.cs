using System.Collections.Generic;

namespace MudPlay.Game.Map.MpFile;

// Reproduces the MegaMUD rooms.md hash + exits encoding used by every .mp
// path/loop file. Each step row in a .mp file starts with an 8-char hashExits
// token: a 3-char name hash followed by a 5-char exit signature. Encoded here
// without behaviour change so a known room name + exit set produces the exact
// same 8-char token MegaMUD wrote into its rooms.md entries.
//
// A recorded step's hashExits decodes into a (nameHash, exitSet) candidate
// filter, and our graph's room must produce the same hashExits when a candidate
// path is walked (per-leg integrity check).
public static class MegaMudHash
{
    // Direction ordering matches MegaMUD's: N, S, E, W, NE, NW, SE, SW, U, D.
    private static readonly Direction[] OrderedDirs =
    {
        Direction.N, Direction.S, Direction.E, Direction.W,
        Direction.NE, Direction.NW, Direction.SE, Direction.SW,
        Direction.U, Direction.D,
    };

    // Per-exit weight added when the exit is present. Doors count double, as
    // MegaMUD multiplies by 2 for door/gate/key exits (EncodeExitsInternal).
    private static readonly int[] ExitVal      = { 1, 4, 1, 4, 1, 4, 1, 4, 1, 4 };

    // 1-indexed hex digit (counting from the right of the 5-char code)
    // that each exit contributes to.
    private static readonly int[] ExitPosition = { 5, 5, 4, 4, 3, 3, 2, 2, 1, 1 };

    // Compute the 3-char MegaMUD name hash for roomName: sum of (1-based position
    // × charCode), low 12 bits, hex-encoded, padded to 3 chars. Empty name yields
    // "FFF" (MegaMUD's sentinel for null/empty input).
    public static string ComputeNameHash(string? roomName)
    {
        if (string.IsNullOrEmpty(roomName)) return "FFF";

        int nValue = 0;
        for (int i = 0; i < roomName.Length; i++)
        {
            int charCode = roomName[i];
            int pos = i + 1;
            nValue = unchecked(nValue + pos * charCode);
        }

        uint unsignedVal = unchecked((uint)nValue);
        string hex = unsignedVal.ToString("X");
        string hash = hex.Length >= 3 ? hex[^3..] : hex.PadLeft(3, '0');
        return hash.ToUpperInvariant();
    }

    // Encode room's exits into the 5-char MegaMUD exits code, replicating
    // MegaMUD's rules: door / key-locked / toll-gate exits weight ×2; hidden (incl.
    // SearchableHidden and MultiActionHidden) exits are excluded entirely;
    // everything else weights ×1. Required for the importer's per-step hash compare
    // to match rooms.md exactly when the source room has any door / hidden exit.
    public static string EncodeExits(Room room)
    {
        ArgumentNullException.ThrowIfNull(room);
        HashSet<Direction> visible = new();
        HashSet<Direction> doors = new();
        foreach (KeyValuePair<Direction, RoomExit> kv in room.Exits)
        {
            if (IsHidden(kv.Value.Hint)) continue;
            visible.Add(kv.Key);
            if (IsDoorLike(kv.Value.Hint)) doors.Add(kv.Key);
        }
        return EncodeExitsInternal(visible, doors);
    }

    // Compute the 8-char hashExits token for room using its name and
    // door-/hidden-aware exit encoding.
    public static string ComputeHashExits(Room room)
    {
        ArgumentNullException.ThrowIfNull(room);
        return ComputeNameHash(room.Name) + EncodeExits(room);
    }

    private static string EncodeExitsInternal(IReadOnlySet<Direction> exits, IReadOnlySet<Direction> doors)
    {
        Span<int> buckets = stackalloc int[6];
        for (int i = 0; i < OrderedDirs.Length; i++)
        {
            Direction dir = OrderedDirs[i];
            if (!exits.Contains(dir)) continue;
            int pos = ExitPosition[i];
            int weight = ExitVal[i];
            // MegaMUD doubles the weight for door/gate/key-locked exits.
            if (doors.Contains(dir)) weight *= 2;
            buckets[pos] += weight;
        }
        Span<char> code = stackalloc char[5];
        for (int x = 1; x <= 5; x++) code[x - 1] = HexDigit(buckets[x]);
        return new string(code);
    }

    // Returns true when hint classifies as a "door" for MegaMUD exit-code purposes
    // ("door", "gate", or "key"). Toll counts because toll-gate text typically
    // reads "(Gate, N gold)".
    public static bool IsDoorLike(RoomExitHint hint)
        => hint is RoomExitHint.Door
                or RoomExitHint.KeyLocked
                or RoomExitHint.Toll;

    // Returns true when hint classifies as a "hidden" exit ("hidden" or "secret").
    // These don't contribute to the MegaMUD exits code at all.
    public static bool IsHidden(RoomExitHint hint)
        => hint is RoomExitHint.SearchableHidden
                or RoomExitHint.MultiActionHidden;

    // Split an 8-char hashExits into its (nameHash, exitsCode) parts. Returns
    // (null, null) when the input is the wrong length or contains non-hex
    // characters.
    public static (string? NameHash, string? ExitsCode) Split(string? hashExits)
    {
        if (string.IsNullOrWhiteSpace(hashExits)) return (null, null);
        string h = hashExits.Trim();
        if (h.Length != 8) return (null, null);
        foreach (char c in h) if (!IsHex(c)) return (null, null);
        return (h[..3].ToUpperInvariant(), h[3..].ToUpperInvariant());
    }

    // ----- helpers ---------------------------------------------------

    private static char HexDigit(int v)
    {
        int d = v & 0xF;
        return d < 10 ? (char)('0' + d) : (char)('A' + d - 10);
    }

    private static bool IsHex(char c)
        => (c >= '0' && c <= '9')
        || (c >= 'A' && c <= 'F')
        || (c >= 'a' && c <= 'f');
}
