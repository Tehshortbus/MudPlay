using FujinTerm.Models.GameData;

namespace FujinTerm.Game.Combat;

// What kind of "Also Here" occupant a name resolved to. CombatStateTracker only
// treats Monster entries as candidate targets; CombatManager further filters by
// the per-monster AttackPriority in game data.
public enum EntityKind
{
    // Name matched a PlayerRecord in the active per-BBS player database
    // (case-insensitive on PlayerRecord.GivenName).
    Player,

    // Name matched a MonsterMessageRecord.Name in the active game-data set's
    // MonsterMessageStore, either directly (when
    // MonsterMessageRecord.AllowNoPrefix is set) or after stripping a known
    // flavor prefix.
    Monster,

    // Name matched neither — RoomEntityClassifier emits a Warn-level LogPane row
    // carrying the raw "Also Here:" line as Context so the user can double-click
    // to open the fix dialog and resolve it as a flavor-prefix addition or a
    // player observation.
    Unknown,
}

// One classified occupant from an "Also here:" line.
//
// RawName is the name verbatim as it appeared on the wire after splitting +
// trimming trailing punctuation / parenthetical suffix (e.g. "nasty giant rat",
// "Raijin" from "Raijin (sneaking)"). ResolvedName is the canonical name for
// downstream lookups: monster BaseName when Kind is Monster,
// PlayerRecord.GivenName when Player, equal to RawName for Unknown. CombatManager
// uses ResolvedName to send "attack {ResolvedName}" — the server resolves
// targeting by base name regardless of which prefix the room display printed.
// Kind is Player / Monster / Unknown. MonsterNumber is MonsterRecord.Number when
// Kind is Monster, else null; CombatManager joins this against the Monsters
// table to read AttackPriority.
public readonly record struct RoomEntity(
    string RawName,
    string ResolvedName,
    EntityKind Kind,
    int? MonsterNumber);
