namespace FujinTerm.Game;

/// <summary>
/// Which MajorMUD formula family a game-data set targets. MMUD Explorer
/// reads the same distinction from the MDB's <c>Info</c> table
/// (<c>Legit == 2</c> → GreaterMUD, otherwise Stock); FujinTerm derives
/// it the same way via <see cref="Services.GameDataCache.ActiveRealm"/>.
/// </summary>
/// <remarks>
/// <para>
/// The two families diverge in CP cost caps, HP/mana regen divisors,
/// exp-curve modifier tables, and most combat formulas (hit floors/caps,
/// dodge curves, accuracy stat weighting, smash multiplier). The shared
/// calculators under <c>Game/Calculators/</c> branch on this.
/// </para>
/// <para>
/// <see cref="ParaMud"/> is MMUD Explorer's <c>bGreaterMUD = True</c>
/// (the GreaterMUD / Paradigm lineage); <see cref="Stock"/> covers both
/// the legit Stock database and custom non-GreaterMUD sets.
/// </para>
/// </remarks>
public enum RealmType
{
    /// <summary>Classic Stock MajorMUD formulas (MDB <c>Info.Legit ≠ 2</c>).</summary>
    Stock,

    /// <summary>GreaterMUD / Paradigm formulas (MDB <c>Info.Legit == 2</c>).</summary>
    ParaMud,
}
