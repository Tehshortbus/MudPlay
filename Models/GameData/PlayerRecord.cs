namespace FujinTerm.Models.GameData;

/// <summary>
/// One observed-or-edited player. Stored at the BBS tier by default —
/// the same display name on a different BBS represents a different
/// person, so per-BBS storage is the natural fit. Per-character
/// promotion of a record (for personalised permission overrides) and
/// global promotion (for cross-BBS friend lists) use the standard
/// 4-tier resolver.
/// </summary>
/// <remarks>
/// Observation writes (from <c>who</c> output) refresh
/// <see cref="Class"/> / <see cref="Race"/> / <see cref="Alignment"/>
/// / <see cref="Title"/> / <see cref="LastSeenUtc"/>. User-edited
/// fields (<see cref="Notes"/>, <see cref="Permissions"/>) are never
/// overwritten by observation — Phase 5 PR 5.20 enforces this in the
/// service.
/// </remarks>
public sealed record PlayerRecord(
    string Name,
    string? Class,
    string? Race,
    string? Alignment,
    string? Title,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    string? Notes = null,
    PlayerPermissions Permissions = default);

/// <summary>
/// Per-player remote-command permission flags. Consumed by the Phase
/// 6 RemoteCommandManager when deciding whether to accept an
/// <c>@-command</c> from this player. Tri-state per category:
/// <c>true</c> = explicitly allowed, <c>false</c> = explicitly denied,
/// <c>null</c> = inherit from the tier above.
/// </summary>
/// <param name="AllowQuery">
/// Basic info commands — <c>@where</c>, <c>@health</c>, <c>@par</c>,
/// <c>@have</c>, <c>@wealth</c>, <c>@enc</c>, <c>@exp</c>.
/// </param>
/// <param name="AllowControl">
/// Direct-action commands — <c>@goto</c>, <c>@auto-*</c>,
/// <c>@invite</c>, <c>@hangup</c>.
/// </param>
/// <param name="AllowDo">
/// <c>@do &lt;command&gt;</c> passthrough — arbitrary commands run as
/// this character. Highest trust level.
/// </param>
/// <param name="AllowTrap">Trap-coordination commands — <c>@trap</c>.</param>
public readonly record struct PlayerPermissions(
    bool? AllowQuery = null,
    bool? AllowControl = null,
    bool? AllowDo = null,
    bool? AllowTrap = null);
