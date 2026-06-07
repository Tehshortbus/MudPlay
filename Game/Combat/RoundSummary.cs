namespace FujinTerm.Game.Combat;

/// <summary>
/// One closed combat round emitted by <see cref="RoundDamageTracker"/>.
/// Carries the aggregate damage / hit-rate snapshot the Phase 9
/// <see cref="Game.Spells.CastingDirector"/> (PR 9.D) uses to decide
/// next-round casts and the Phase 11 <c>CombatSessionTracker</c>
/// aggregates over the session.
/// </summary>
/// <param name="RoundNumber">Monotonic counter; resets on
/// <see cref="RoundDamageTracker.Reset"/> (called on new BBS
/// connection / character switch).</param>
/// <param name="StartedAt">Wall-clock time of the first damage line
/// belonging to this round.</param>
/// <param name="EndedAt">Wall-clock time the round closed — either
/// 5s+ since <see cref="StartedAt"/> as observed on a fresh damage
/// line, or via an external close trigger (CombatStatus=Off, manual
/// Reset).</param>
/// <param name="DamageDealt">Sum of <c>damage</c> captures across all
/// <c>UserHits</c> lines in this round.</param>
/// <param name="DamageTaken">Sum across all <c>MobHits</c> lines.</param>
/// <param name="Hits">Count of damage-bearing lines (UserHits +
/// MobHits) regardless of side.</param>
/// <param name="Misses">Count of <c>MobMisses</c> lines.</param>
/// <param name="HpBefore">Snapshot of <see cref="PlayerState.Hp"/> at
/// <see cref="StartedAt"/>.</param>
/// <param name="HpAfter">Snapshot of <see cref="PlayerState.Hp"/> at
/// <see cref="EndedAt"/>.</param>
/// <param name="MaBefore">Snapshot of <see cref="PlayerState.Ma"/> at
/// <see cref="StartedAt"/>.</param>
/// <param name="MaAfter">Snapshot of <see cref="PlayerState.Ma"/> at
/// <see cref="EndedAt"/>.</param>
public readonly record struct RoundSummary(
    int RoundNumber,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int DamageDealt,
    int DamageTaken,
    int Hits,
    int Misses,
    int HpBefore,
    int HpAfter,
    int MaBefore,
    int MaAfter);
