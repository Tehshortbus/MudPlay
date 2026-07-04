using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace FujinTerm.Game.Map;

// One room in the active game-data set's static graph. Built once at
// set-switch time by RoomGraphManager and shared by reference to every
// subsystem that asks for it (room tracker, BFS mapper, walker, loop manager,
// auto-lair scheduler, navigation UI).
//
// All fields are init-only — the graph itself is immutable for the lifetime of
// an active set. User-tier room metadata (avoided, stash-room) lives outside
// this type in the per-character profile so the same Room instance can be
// reused across characters connected to the same realm.
//
// RawLairTag is preserved verbatim from the MDB cell (e.g.
// "(Max 2): 1141,2175,2176,[5-6-8-2]"). The [group-index] token is parsed
// elsewhere to look up the average-respawn delay in Lairs.json; HasLair is the
// boolean "this room belongs to Auto-Lair-eligible real estate" classification.
public sealed record Room
{
    // (Map, Room) primary key.
    public required RoomKey Key { get; init; }

    // Human-readable room name as it appears in-game (the line above "Obvious
    // exits:"). May be empty when the MDB shipped a null Name for the row —
    // typically ganghouse rooms in non-Paradigm 1.x exports where the sysop
    // fills the name on a separate table the importer doesn't read. Render
    // through DisplayName for the user-facing label so the nameless cases show a
    // stable placeholder.
    public required string Name { get; init; }

    // User-facing label: the room's Name when non-empty, or "???" when
    // null/empty. Use this everywhere a tooltip, status bar, search result, or
    // engine-action label needs to surface the room — never raw Name.
    public string DisplayName => string.IsNullOrEmpty(Name) ? "???" : Name;

    // True when the room shipped without a name in the imported data — the
    // trigger for the learn-on-observation auto-update flow.
    public bool HasUnknownName => string.IsNullOrEmpty(Name);

    // MajorMUD light level. Negative values mean dim/dark; 0 means fully lit.
    // RoomTracker treats deeply negative values as "contents may be obscured by
    // darkness" so the walker doesn't false-positive a missing monster/player
    // listing.
    public int Light { get; init; }

    // Shop record number (0 = none). Resolved against the Shops table by
    // consumers — CashManager filters on ShopType == 7 (bank) using this field.
    public int Shop { get; init; }

    // Spell record number triggered when the player steps into this room
    // (0 = none). Surfaced as the purple Spell room type on the map, per the
    // MajorMUD convention for cast-on-enter effects. Detailed interpretation of
    // the spell payload is the SpellMessages consumer's concern.
    public int Spell { get; init; }

    // NPC field from the MDB — the monster number of a monster "placed"
    // (fixed-spawn) in this room, or 0 for none. Unlike lair groups
    // (RawLairTag), a placed monster has a single home room and typically a
    // respawn timer (bosses, uniques, shopkeepers). The Game Data Browser uses
    // this to show a timed-respawn monster's "Placed In" room.
    public int Npc { get; init; }

    // Per-room respawn delay tick from the MDB. Encodes the lair's
    // time-to-repopulate via the GreaterMUD formula
    // seconds = (Delay - 1) × 60 + 30 (so Delay = 5 → 4 m 30 s). 0 means "no
    // per-room delay set" — LairTimerStore.DefaultRespawnSeconds falls through
    // to the GroupIndex → Lairs.AvgDelay path or the pre-1.83 monster-list
    // fallback when this is unset.
    public int Delay { get; init; }

    // CMD field from the MDB — when non-zero, indexes a row in TBInfoStore whose
    // Action chain resolves to a text-keyword → teleport command. The presence
    // of Cmd > 0 is the discriminator that promotes an (Item: N) exit on this
    // room from a plain inventory check (RoomExitHint.Item) to a party-breaking
    // teleport (RoomExitHint.Teleport). 0 means no associated TBInfo entry.
    public int Cmd { get; init; }

    // Raw Lair cell from the MDB. null when the row stored the NUL/empty
    // sentinel (no lair); non-null otherwise. Detailed parsing (mob list,
    // GroupIndex back-reference) happens in the Auto-Lair layer.
    public string? RawLairTag { get; init; }

    // True when RawLairTag is non-null/non-empty.
    public bool HasLair => !string.IsNullOrEmpty(RawLairTag);

    // Parsed exits keyed by Direction. Only real exits appear — "0" cells from
    // the MDB are dropped on the way in, so a missing key means "no exit that
    // way".
    public required IReadOnlyDictionary<Direction, RoomExit> Exits { get; init; }

    // Bit field of the directions present in Exits — (1u << (int)dir) per
    // direction. Used to key the graph's (Name, ExitMask) uniqueness index
    // without allocating a HashSet per lookup.
    public uint ExitMask { get; init; }

    // Empty-exits helper for tests and internal construction. Real rooms always
    // come back from RoomGraphManager with a populated Exits map.
    internal static readonly IReadOnlyDictionary<Direction, RoomExit> EmptyExits
        = new ReadOnlyDictionary<Direction, RoomExit>(new Dictionary<Direction, RoomExit>());

    // True when at least one outbound exit carries RoomExitHint.Trap. The map UI
    // renders a red half-connector glyph on the trapped exit, and the walker
    // routes through TrapDisarmManager before stepping in that direction.
    public bool HasTrappedExits => Exits.Values.Any(e => e.Hint == RoomExitHint.Trap);

    // Directions whose RoomExit.Hint is RoomExitHint.Trap. Empty when none.
    public IReadOnlyList<Direction> TrappedDirections =>
        Exits.Where(kvp => kvp.Value.Hint == RoomExitHint.Trap)
             .Select(kvp => kvp.Key)
             .ToArray();
}
