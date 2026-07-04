using System.Collections.Generic;

namespace FujinTerm.Game.Map.MpFile;

// Reproduces the MegaMUD rooms.md hash + exits encoding used by every .mp
// path/loop file. Each step row in a .mp file starts with an 8-char hashExits
// token: a 3-char name hash followed by a 5-char exit signature. Encoded here
// without behaviour change so a known room name + exit set produces the exact
// same 8-char token MegaMUD wrote into its rooms.md entries.
//
// Used by MpFileImporter to (a) decode a recorded step's hashExits into a
// (nameHash, exitSet) candidate filter and (b) verify our graph's room produces
// the same hashExits when walking a candidate path (per-leg integrity check).
public static class MegaMudHash
{
    // Direction ordering matches MegaMUD's: N, S, E, W, NE, NW, SE, SW, U, D.
    private static readonly Direction[] OrderedDirs =
    {
        Direction.N, Direction.S, Direction.E, Direction.W,
        Direction.NE, Direction.NW, Direction.SE, Direction.SW,
        Direction.U, Direction.D,
    };

    // Per-exit weight added when the exit is present.
    // Doors count double — MegaMUD multiplies by 2 for door/gate/key
    // exits — but our graph has no door-vs-normal distinction at the
    // exit level, so we always use the "normal" weight. Door-only
    // mismatches mean we'll occasionally produce a different exits
    // code than rooms.md; downstream callers should treat the
    // per-step hash check as soft for that reason.
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

    // Encode an exit set into the 5-char MegaMUD exits code without door / hidden
    // awareness. Use the EncodeExits(Room) overload when computing against an
    // actual graph room — that one weights doors ×2 (matching MegaMUD) and
    // excludes hidden exits.
    public static string EncodeExits(IReadOnlySet<Direction> exits)
    {
        ArgumentNullException.ThrowIfNull(exits);
        return EncodeExitsInternal(exits, doors: null);
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

    // Compute the full 8-char hashExits token from a room name and its exit set.
    // Door-naive — use the ComputeHashExits(Room) overload against a graph room so
    // doors / hidden exits get their MegaMUD-correct weighting.
    public static string ComputeHashExits(string? roomName, IReadOnlySet<Direction> exits)
        => ComputeNameHash(roomName) + EncodeExits(exits);

    // Compute the 8-char hashExits token for room using its name and
    // door-/hidden-aware exit encoding.
    public static string ComputeHashExits(Room room)
    {
        ArgumentNullException.ThrowIfNull(room);
        return ComputeNameHash(room.Name) + EncodeExits(room);
    }

    private static string EncodeExitsInternal(IReadOnlySet<Direction> exits, IReadOnlySet<Direction>? doors)
    {
        Span<int> buckets = stackalloc int[6];
        for (int i = 0; i < OrderedDirs.Length; i++)
        {
            Direction dir = OrderedDirs[i];
            if (!exits.Contains(dir)) continue;
            int pos = ExitPosition[i];
            int weight = ExitVal[i];
            // MegaMUD doubles the weight for door/gate/key-locked exits.
            if (doors is not null && doors.Contains(dir)) weight *= 2;
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

    // Decode a 5-char exits code into the set of Direction values that produces it.
    // When an exits digit could be satisfied by multiple combinations of
    // doors/normals (which doesn't distinguish here because we ignore the door
    // bit), the smallest "any-on" interpretation wins. Returns null when the code
    // is malformed.
    //
    // Decoding the digit means inferring which of the 1-or-2 exits contributing to
    // that digit are present. Per the ExitPosition table digits 1–5 each receive
    // contributions from a single pair of exits:
    //   - digit 1 (rightmost) ← U, D
    //   - digit 2 ← SE, SW
    //   - digit 3 ← NE, NW
    //   - digit 4 ← E, W
    //   - digit 5 ← N, S
    // Each pair has weights 1 + 4 (see ExitVal), so the digit's value identifies
    // which combination of the two is on: 0=neither, 1=first, 4=second, 5=both.
    // Doors double those weights (2 / 8 / A) but we collapse those back to "exit
    // present" since the door bit isn't carried in our graph.
    public static IReadOnlySet<Direction>? DecodeExits(string? exitsCode)
    {
        if (string.IsNullOrWhiteSpace(exitsCode)) return null;
        string code = exitsCode.Trim();
        if (code.Length != 5) return null;

        // Per-digit pair: [low-weight exit, high-weight exit].
        Direction[][] digitPairs =
        {
            new[] { Direction.U,  Direction.D  },  // digit 1
            new[] { Direction.SE, Direction.SW },  // digit 2
            new[] { Direction.NE, Direction.NW },  // digit 3
            new[] { Direction.E,  Direction.W  },  // digit 4
            new[] { Direction.N,  Direction.S  },  // digit 5
        };

        var set = new HashSet<Direction>();
        // Encoder writes buckets[1] → code[0] (i.e. MegaMUD's "digit 1"
        // = U/D pair, the leftmost char) and buckets[5] → code[4]
        // (digit 5 = N/S, rightmost char). So strIdx maps directly:
        // code[0] ↔ digit 1 ↔ digitPairs[0], code[4] ↔ digit 5 ↔ digitPairs[4].
        for (int strIdx = 0; strIdx < 5; strIdx++)
        {
            int digit = HexValue(code[strIdx]);
            if (digit < 0) return null;
            Direction[] pair = digitPairs[strIdx];

            // Low-weight bit (value 1) — including the door variant
            // value 2. Either means the low-weight exit is present.
            if ((digit & 0x3) != 0) set.Add(pair[0]);
            // High-weight bit (value 4) — including door variant 8.
            if ((digit & 0xC) != 0) set.Add(pair[1]);
        }
        return set;
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

    private static int HexValue(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'A' && c <= 'F') return 10 + (c - 'A');
        if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
        return -1;
    }
}
