namespace FujinTerm.Game.Combat;

// Immutable snapshot of the session's observed combat performance, produced by
// CombatSessionTracker.Snapshot for the Session Stats panel. Carries raw counts
// and damage extents; the percentage / average helpers are derived so the
// window binds them directly and the user can re-derive alternatives from the
// raw fields without a tracker change.
//
// Each landed-swing category — plain Hits, Crits, and Backstabs — carries its
// own damage extent so the panel shows them on separate rows. "Physical" is the
// derived roll-up across all three (the player's min/max damage seen for
// physical swings). Configured attack-spell casts and weapon procs get their
// own rows: they're not swings, so they never move the physical extent or the
// hit / miss / crit denominators, though their damage still counts toward the
// per-round total (recognised via the game-data caster-message matcher).
public readonly record struct CombatSessionStats(
    // ----- Offensive: the player's own swings (per-category extents) -----
    int Hits,
    int Crits,
    int Backstabs,
    int Misses,
    int HitMinDamage,
    int HitMaxDamage,
    long HitTotalDamage,
    int CritMinDamage,
    int CritMaxDamage,
    long CritTotalDamage,
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
    // Every swing that connected (hit + crit + backstab).
    public int LandedSwings => Hits + Crits + Backstabs;

    // Total offensive swings observed (landed + missed).
    public int TotalSwings => LandedSwings + Misses;

    // Fraction of swings that connected, 0–100.
    public double HitPercent => Pct(LandedSwings, TotalSwings);

    // Fraction of swings that missed, 0–100.
    public double MissPercent => Pct(Misses, TotalSwings);

    // Crit rate among landed swings, 0–100 (a miss can't crit).
    public double CritPercent => Pct(Crits, LandedSwings);

    // Mean damage per plain (non-crit, non-backstab) hit.
    public double HitAvgDamage => Avg(HitTotalDamage, Hits);

    // Mean damage per critical hit.
    public double CritAvgDamage => Avg(CritTotalDamage, Crits);

    // Smallest single-swing damage across the non-empty landed categories
    // (plain hit / crit / backstab); 0 before any swing lands. Derived so each
    // category can still be read on its own.
    public int PhysicalMinDamage
    {
        get
        {
            int min = int.MaxValue;
            if (Hits > 0) min = Math.Min(min, HitMinDamage);
            if (Crits > 0) min = Math.Min(min, CritMinDamage);
            if (Backstabs > 0) min = Math.Min(min, BackstabMinDamage);
            return min == int.MaxValue ? 0 : min;
        }
    }

    // Largest single-swing damage across the landed categories; 0 before any
    // swing lands.
    public int PhysicalMaxDamage => Math.Max(HitMaxDamage, Math.Max(CritMaxDamage, BackstabMaxDamage));

    // Total damage across every landed physical swing.
    public long PhysicalTotalDamage => HitTotalDamage + CritTotalDamage + BackstabTotalDamage;

    // Mean damage per landed physical swing.
    public double PhysicalAvgDamage => Avg(PhysicalTotalDamage, LandedSwings);

    // Mean damage per landed backstab.
    public double BackstabAvgDamage => Avg(BackstabTotalDamage, Backstabs);

    // All incoming attacks the mob made at us (hits + misses + dodges).
    public int IncomingAttacks => MobHits + MobMisses + Dodges;

    // Fraction of incoming attacks we dodged, 0–100.
    public double DodgePercent => Pct(Dodges, IncomingAttacks);

    // Incoming attacks that didn't connect — the mob whiffed or we dodged. The
    // Session Stats panel folds dodge and miss into one defensive "avoided"
    // readout, since both mean the blow never landed.
    public int AvoidedAttacks => MobMisses + Dodges;

    // Fraction of incoming attacks we avoided (mob missed or we dodged), 0–100 —
    // the combined dodge-and-miss defensive rate.
    public double AvoidPercent => Pct(AvoidedAttacks, IncomingAttacks);

    // Mean damage we dealt per round that did damage.
    public double RoundAvgDamage => Avg(RoundTotalDamage, RoundsWithDamage);

    // Mean damage per weapon proc that fired.
    public double ProcAvgDamage => Avg(ProcTotalDamage, ProcHits);

    // Procs per landed basic swing, 0–100 — how often the weapon procs when it
    // connects.
    public double ProcRate => Pct(ProcHits, LandedSwings);

    // Mean damage per configured attack-spell cast that landed.
    public double SpellAvgDamage => Avg(SpellTotalDamage, SpellHits);

    private static double Pct(int part, int whole) => whole == 0 ? 0 : 100.0 * part / whole;
    private static double Avg(long total, int count) => count == 0 ? 0 : (double)total / count;
}
