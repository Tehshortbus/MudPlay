namespace FujinTerm.Game.Combat;

// One closed combat round emitted by RoundDamageTracker. Carries the aggregate
// damage / hit-rate snapshot CastingDirector uses to decide next-round casts and
// CombatSessionTracker aggregates over the session.
//
// RoundNumber is a monotonic counter; resets on RoundDamageTracker.Reset (called
// on new BBS connection / character switch). StartedAt is the wall-clock time of
// the first damage line belonging to this round. EndedAt is the wall-clock time
// the round closed — either 5s+ since StartedAt as observed on a fresh damage
// line, or via an external close trigger (CombatStatus=Off, manual Reset).
// DamageDealt is the sum of damage captures across all UserHits lines in this
// round; DamageTaken is the sum across all MobHits lines. Hits is the count of
// damage-bearing lines (UserHits + MobHits) regardless of side; Misses is the
// count of MobMisses lines. HpBefore/HpAfter and MaBefore/MaAfter snapshot
// PlayerState.Hp / .Ma at StartedAt and EndedAt.
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
