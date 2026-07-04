namespace FujinTerm.Game.Combat;

// Immutable snapshot of the session's activity counters, produced by
// SessionActivityTracker.Snapshot for the Session Stats panel's "Session
// Statistics" section: how long the session has run, how many monsters fell, how
// much experience was earned, and the copper-value currency picked up vs.
// stashed/deposited. The per-hour rates are derived so the window binds them
// directly off the same time base.
public readonly record struct SessionActivityStats(
    TimeSpan TimeOnline,
    int MonstersKilled,
    long ExperienceEarned,
    long CurrencyCollected,
    long CurrencyStashed)
{
    // Monsters killed per hour across the whole session, 0 before any time has
    // elapsed.
    public double KillsPerHour => Rate(MonstersKilled);

    // Experience earned per hour across the whole session, 0 before any time has
    // elapsed.
    public double ExperiencePerHour => Rate(ExperienceEarned);

    // Currency picked up per hour, in copper value, across the whole session — 0
    // before any time has elapsed.
    public double CurrencyPerHour => Rate(CurrencyCollected);

    private double Rate(double total) =>
        TimeOnline.TotalHours <= 0 ? 0 : total / TimeOnline.TotalHours;
}
