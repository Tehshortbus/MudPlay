using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace FujinTerm.Game.Map;

/// <summary>
/// One room in the active game-data set's static graph. Built once at
/// set-switch time by <see cref="RoomGraphManager"/> and shared by
/// reference to every subsystem that asks for it (room tracker, BFS
/// mapper, walker, loop manager, auto-lair scheduler, navigation UI).
/// </summary>
/// <remarks>
/// <para>
/// All fields are init-only — the graph itself is immutable for the
/// lifetime of an active set. User-tier room metadata (avoided,
/// stash-room) lives outside this type in the per-character profile so
/// the same Room instance can be reused across characters connected to
/// the same realm.
/// </para>
/// <para>
/// <see cref="RawLairTag"/> is preserved verbatim from the MDB cell
/// (e.g. <c>"(Max 2): 1141,2175,2176,[5-6-8-2]"</c>). PR 7.18+ will
/// parse the <c>[group-index]</c> token to look up the average-respawn
/// delay in <c>Lairs.json</c>; PR 7.4 only needs the boolean
/// <see cref="HasLair"/> for "this room belongs to Auto-Lair-eligible
/// real estate" classification.
/// </para>
/// </remarks>
public sealed record Room
{
    /// <summary>(Map, Room) primary key.</summary>
    public required RoomKey Key { get; init; }

    /// <summary>Human-readable room name as it appears in-game (the line above <c>Obvious exits:</c>).</summary>
    public required string Name { get; init; }

    /// <summary>
    /// MajorMUD light level. Negative values mean dim/dark; 0 means
    /// fully lit. RoomTracker treats deeply negative values as
    /// "contents may be obscured by darkness" so the walker doesn't
    /// false-positive a missing monster/player listing.
    /// </summary>
    public int Light { get; init; }

    /// <summary>
    /// Shop record number (<c>0</c> = none). Resolved against the
    /// <c>Shops</c> table by consumers — Phase 13 <c>CashManager</c>
    /// filters on <c>ShopType == 7</c> (bank) using this field.
    /// </summary>
    public int Shop { get; init; }

    /// <summary>
    /// Spell record number triggered when the player steps into this
    /// room (<c>0</c> = none). Surfaced as the purple Spell room
    /// type on the map, per the MajorMUD convention for cast-on-enter
    /// effects. Detailed interpretation of the spell payload is the
    /// SpellMessages consumer's concern.
    /// </summary>
    public int Spell { get; init; }

    /// <summary>Per-room delay seconds from the MDB. Preserved for completeness; not used by Phase 7.</summary>
    public int Delay { get; init; }

    /// <summary>
    /// <c>CMD</c> field from the MDB — when non-zero, indexes a row in
    /// <see cref="Services.TBInfoStore"/> whose <c>Action</c> chain
    /// resolves to a text-keyword → teleport command. The presence of
    /// <c>Cmd &gt; 0</c> is the discriminator that promotes an
    /// <c>(Item: N)</c> exit on this room from a plain inventory check
    /// (<see cref="RoomExitHint.Item"/>) to a party-breaking teleport
    /// (<see cref="RoomExitHint.Teleport"/>). <c>0</c> means no
    /// associated TBInfo entry.
    /// </summary>
    public int Cmd { get; init; }

    /// <summary>
    /// Raw <c>Lair</c> cell from the MDB. <c>null</c> when the row
    /// stored the NUL/empty sentinel (no lair); non-null otherwise.
    /// Detailed parsing (mob list, <c>GroupIndex</c> back-reference) is
    /// deferred to the Auto-Lair PRs.
    /// </summary>
    public string? RawLairTag { get; init; }

    /// <summary>Convenience flag — <c>true</c> when <see cref="RawLairTag"/> is non-null/non-empty.</summary>
    public bool HasLair => !string.IsNullOrEmpty(RawLairTag);

    /// <summary>
    /// Parsed exits keyed by <see cref="Direction"/>. Only real exits
    /// appear — <c>"0"</c> cells from the MDB are dropped on the way in,
    /// so a missing key means "no exit that way".
    /// </summary>
    public required IReadOnlyDictionary<Direction, RoomExit> Exits { get; init; }

    /// <summary>
    /// Bit field of the directions present in <see cref="Exits"/> —
    /// <c>(1u &lt;&lt; (int)dir)</c> per direction. Used to key the
    /// graph's <c>(Name, ExitMask)</c> uniqueness index without
    /// allocating a HashSet per lookup.
    /// </summary>
    public uint ExitMask { get; init; }

    /// <summary>
    /// Empty-exits helper for tests and internal construction. Real
    /// rooms always come back from <see cref="RoomGraphManager"/> with
    /// a populated <see cref="Exits"/> map.
    /// </summary>
    internal static readonly IReadOnlyDictionary<Direction, RoomExit> EmptyExits
        = new ReadOnlyDictionary<Direction, RoomExit>(new Dictionary<Direction, RoomExit>());

    /// <summary>
    /// <c>true</c> when at least one outbound exit carries
    /// <see cref="RoomExitHint.Trap"/>. The map UI renders a red
    /// half-connector glyph on the trapped exit, and the walker
    /// (PR 7.22) routes through <c>TrapDisarmManager</c> before
    /// stepping in that direction.
    /// </summary>
    public bool HasTrappedExits => Exits.Values.Any(e => e.Hint == RoomExitHint.Trap);

    /// <summary>Directions whose <see cref="RoomExit.Hint"/> is <see cref="RoomExitHint.Trap"/>. Empty when none.</summary>
    public IReadOnlyList<Direction> TrappedDirections =>
        Exits.Where(kvp => kvp.Value.Hint == RoomExitHint.Trap)
             .Select(kvp => kvp.Key)
             .ToArray();
}
