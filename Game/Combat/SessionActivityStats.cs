namespace FujinTerm.Game.Combat;

// Immutable snapshot of the session's activity counters, produced by
// SessionActivityTracker.Snapshot for the Session Stats panel's "Session
// Statistics" section: how many monsters fell, how much experience was earned,
// and the copper-value currency picked up vs. stashed/deposited.
//
// The MonstersKilled / ExperienceEarned / Currency* figures are LIFETIME totals
// for the session (cleared only by a full reset). The per-hour rates divide the
// RATE-WINDOW figures — kills / experience / currency accrued within the rate
// window — by TimeOnline, the elapsed time of that same window. The window is
// capped at four hours: while the session is younger than that it spans the whole
// session (windowed figures equal the totals, rates read as lifetime rates); once
// it passes four hours the window trails, so the rates reflect the recent pace
// rather than the whole night blended. Resetting Time Analysis re-anchors the
// window: the totals stay put while the rates fall back to zero and climb again.
public readonly record struct SessionActivityStats(
    TimeSpan TimeOnline,
    int MonstersKilled,
    long ExperienceEarned,
    long CurrencyCollected,
    long CurrencyStashed,
    int RateKills,
    long RateExperience,
    long RateCurrency)
{
    // Monsters killed per hour across the current rate window, 0 before any time
    // has elapsed.
    public double KillsPerHour => Rate(RateKills);

    // Experience earned per hour across the current rate window, 0 before any
    // time has elapsed.
    public double ExperiencePerHour => Rate(RateExperience);

    // Currency picked up per hour, in copper value, across the current rate
    // window — 0 before any time has elapsed.
    public double CurrencyPerHour => Rate(RateCurrency);

    private double Rate(double windowed) =>
        TimeOnline.TotalHours <= 0 ? 0 : windowed / TimeOnline.TotalHours;
}
