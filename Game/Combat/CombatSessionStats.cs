namespace FujinTerm.Game.Combat;

/// <summary>
/// Immutable snapshot of the session's observed combat performance, produced
/// by <see cref="CombatSessionTracker.Snapshot"/> for the Phase 11 Session
/// Stats panel. Carries raw counts and damage extents; the percentage /
/// average helpers are derived so the window binds them directly and the user
/// can re-derive alternatives from the raw fields without a tracker change.
/// </summary>
/// <remarks>
/// "Physical" rolls up every landed melee swing — plain <see cref="Hits"/>,
/// <see cref="Crits"/>, and <see cref="Backstabs"/> — into one damage extent
/// (the player's "min/max damage seen for physical swings"); the backstab
/// extent is also tracked on its own. Configured attack-<see cref="SpellHits">
/// spell</see> casts and weapon <see cref="ProcHits">procs</see> get their own
/// rows: they're <i>not</i> swings, so they never move the physical extent or
/// the hit / miss / crit denominators, though their damage still counts toward
/// the per-round total (recognised via the game-data caster-message matcher).
/// </remarks>
public readonly record struct CombatSessionStats(
    // ----- Offensive: the player's own swings -----
    int Hits,
    int Crits,
    int Backstabs,
    int Misses,
    int PhysicalMinDamage,
    int PhysicalMaxDamage,
    long PhysicalTotalDamage,
    int BackstabMinDamage,
    int BackstabMaxDamage,
    long BackstabTotalDamage,
    // ----- Defensive: incoming attacks -----
    int MobHits,
    int MobMisses,
    int Dodges,
    // ----- Per-round damage dealt -----
    int RoundsWithDamage,
    int RoundMinDamage,
    int RoundMaxDamage,
    long RoundTotalDamage,
    // ----- Weapon procs & configured attack spells (own rows; not swings) -----
    int ProcHits,
    int ProcMinDamage,
    int ProcMaxDamage,
    long ProcTotalDamage,
    int SpellHits,
    int SpellMinDamage,
    int SpellMaxDamage,
    long SpellTotalDamage)
{
    /// <summary>Every swing that connected (hit + crit + backstab).</summary>
    public int LandedSwings => Hits + Crits + Backstabs;

    /// <summary>Total offensive swings observed (landed + missed).</summary>
    public int TotalSwings => LandedSwings + Misses;

    /// <summary>Fraction of swings that connected, 0–100.</summary>
    public double HitPercent => Pct(LandedSwings, TotalSwings);

    /// <summary>Fraction of swings that missed, 0–100.</summary>
    public double MissPercent => Pct(Misses, TotalSwings);

    /// <summary>Crit rate among landed swings, 0–100 (a miss can't crit).</summary>
    public double CritPercent => Pct(Crits, LandedSwings);

    /// <summary>Mean damage per landed physical swing.</summary>
    public double PhysicalAvgDamage => Avg(PhysicalTotalDamage, LandedSwings);

    /// <summary>Mean damage per landed backstab.</summary>
    public double BackstabAvgDamage => Avg(BackstabTotalDamage, Backstabs);

    /// <summary>All incoming attacks the mob made at us (hits + misses + dodges).</summary>
    public int IncomingAttacks => MobHits + MobMisses + Dodges;

    /// <summary>Fraction of incoming attacks we dodged, 0–100.</summary>
    public double DodgePercent => Pct(Dodges, IncomingAttacks);

    /// <summary>Mean damage we dealt per round that did damage.</summary>
    public double RoundAvgDamage => Avg(RoundTotalDamage, RoundsWithDamage);

    /// <summary>Mean damage per weapon proc that fired.</summary>
    public double ProcAvgDamage => Avg(ProcTotalDamage, ProcHits);

    /// <summary>Procs per landed basic swing, 0–100 — how often the weapon
    /// procs when it connects.</summary>
    public double ProcRate => Pct(ProcHits, LandedSwings);

    /// <summary>Mean damage per configured attack-spell cast that landed.</summary>
    public double SpellAvgDamage => Avg(SpellTotalDamage, SpellHits);

    private static double Pct(int part, int whole) => whole == 0 ? 0 : 100.0 * part / whole;
    private static double Avg(long total, int count) => count == 0 ? 0 : (double)total / count;
}
