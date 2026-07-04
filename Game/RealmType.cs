namespace FujinTerm.Game;

// Which MajorMUD formula family a game-data set targets. The distinction
// comes from the MDB's Info table (Legit == 2 → GreaterMUD, otherwise
// Stock); we derive it the same way via GameDataCache.ActiveRealm.
//
// The two families diverge in CP cost caps, HP/mana regen divisors,
// exp-curve modifier tables, and most combat formulas (hit floors/caps,
// dodge curves, accuracy stat weighting, smash multiplier). The shared
// calculators under Game/Calculators/ branch on this.
//
// ParaMud is the GreaterMUD / Paradigm lineage; Stock covers both the legit
// Stock database and custom non-GreaterMUD sets.
public enum RealmType
{
    // Classic Stock MajorMUD formulas (MDB Info.Legit ≠ 2).
    Stock,

    // GreaterMUD / Paradigm formulas (MDB Info.Legit == 2).
    ParaMud,
}
