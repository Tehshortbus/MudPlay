namespace FujinTerm.Game.Combat;

/// <summary>
/// Immutable snapshot of the session's activity counters, produced by
/// <see cref="SessionActivityTracker.Snapshot"/> for the Phase 11 Session Stats
/// panel's "Session Statistics" section: how long the session has run, how many
/// monsters fell, and how much experience was earned. The per-hour rates are
/// derived so the window binds them directly off the same time base.
/// </summary>
public readonly record struct SessionActivityStats(
    TimeSpan TimeOnline,
    int MonstersKilled,
    long ExperienceEarned)
{
    /// <summary>Monsters killed per hour across the whole session, 0 before any
    /// time has elapsed.</summary>
    public double KillsPerHour => Rate(MonstersKilled);

    /// <summary>Experience earned per hour across the whole session, 0 before any
    /// time has elapsed.</summary>
    public double ExperiencePerHour => Rate(ExperienceEarned);

    private double Rate(double total) =>
        TimeOnline.TotalHours <= 0 ? 0 : total / TimeOnline.TotalHours;
}
