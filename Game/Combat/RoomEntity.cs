using FujinTerm.Models.GameData;

namespace FujinTerm.Game.Combat;

/// <summary>
/// What kind of "Also Here" occupant a name resolved to. Phase 9
/// CombatStateTracker (PR 9.0b) only treats <see cref="Monster"/>
/// entries as candidate targets; CombatManager (PR 9.A) further
/// filters by the per-monster <c>AttackPriority</c> in game data.
/// </summary>
public enum EntityKind
{
    /// <summary>Name matched a <see cref="PlayerRecord"/> in the active
    /// per-BBS player database (case-insensitive on
    /// <see cref="PlayerRecord.GivenName"/>).</summary>
    Player,

    /// <summary>Name matched a <see cref="MonsterMessageRecord.Name"/>
    /// in the active game-data set's MonsterMessageStore, either
    /// directly (when <see cref="MonsterMessageRecord.AllowNoPrefix"/>
    /// is set) or after stripping a known flavor prefix.</summary>
    Monster,

    /// <summary>Name matched neither — RoomEntityClassifier emits a
    /// Warn-level LogPane row carrying the raw "Also Here:" line as
    /// Context so the user can double-click to open the fix dialog
    /// (sub-G) and resolve it as a flavor-prefix addition or a player
    /// observation.</summary>
    Unknown,
}

/// <summary>
/// One classified occupant from an <c>Also here:</c> line.
/// </summary>
/// <param name="RawName">
/// The name verbatim as it appeared in the wire after splitting +
/// trimming trailing punctuation / parenthetical suffix (e.g.
/// <c>"nasty giant rat"</c>, <c>"Raijin"</c> from
/// <c>"Raijin (sneaking)"</c>).
/// </param>
/// <param name="ResolvedName">
/// Canonical name for downstream lookups: monster <c>BaseName</c> when
/// <see cref="Kind"/> is <see cref="EntityKind.Monster"/>;
/// <see cref="PlayerRecord.GivenName"/> when <see cref="EntityKind.Player"/>;
/// equal to <see cref="RawName"/> for <see cref="EntityKind.Unknown"/>.
/// CombatManager uses this to send <c>attack {ResolvedName}</c> — the
/// server resolves targeting by base name regardless of which prefix
/// the room display printed.
/// </param>
/// <param name="Kind">Player / Monster / Unknown.</param>
/// <param name="MonsterNumber">
/// <see cref="MonsterRecord.Number"/> when <see cref="Kind"/> is
/// <see cref="EntityKind.Monster"/>, else <c>null</c>. CombatManager
/// joins this against the Monsters table to read
/// <c>AttackPriority</c>.
/// </param>
public readonly record struct RoomEntity(
    string RawName,
    string ResolvedName,
    EntityKind Kind,
    int? MonsterNumber);
