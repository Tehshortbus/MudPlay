namespace FujinTerm.Game;

// Per-realm regen tick cadence — the wall-clock interval at which each of
// RegenTracker's cycles delivers an observable uptick. Selected off
// GameDataCache.ActiveRealm and applied via RegenTracker.SetRealm.
//
// Stock uses the known MajorMUD constants for natural / rest (regen 30 s /
// rest 20 s), one uptick per interval paying the full per-tick amount.
// Meditate rides a 15 s grid from live re-measurement, overriding the
// documented 10 s meditate-tick seed.
//
// ParaMud (GreaterMUD / Paradigm) splits each cycle's amount into three even
// thirds delivered on a faster grid, so the same per-minute total arrives as
// three-times-as-frequent, one-third-size upticks. There are no published
// ParaMud-specific player tick constants, so this cadence is derived from
// live captures via RegenDiagnosticsRecorder: a druid's natural HP tick
// (rate 9) was observed paying +3 every 10 s, and resting (rate 27) paying
// +9 on the same 10 s grid — i.e. the stock 30 s natural cadence divided
// into thirds, with rest riding the same grid at 3× the amount (the resting
// multiplier lives in CharacterCalculator.CalcHpRegen, not here). Meditate is
// not split into thirds on ParaMud — it keeps its native 10 s cadence,
// pending re-verification against live captures.
//
// This models only the observable interval — the per-tick amount is learned
// live by RegenStat. Making the interval realm-correct is what keeps the
// status-bar countdown honest on ParaMud (a 10 s natural tick, not a stock
// 30 s one).
public readonly record struct RealmRegenProfile(
    TimeSpan StandingInterval,
    TimeSpan RestingInterval,
    TimeSpan MeditatingInterval)
{
    // Classic Stock cadence — 30 / 20 s for standing / resting. Meditate
    // rides a 15 s grid (live re-measurement), overriding the documented
    // 10 s meditate-tick seed.
    public static readonly RealmRegenProfile Stock = new(
        RegenConstants.SeedStandingInterval,
        RegenConstants.SeedRestingInterval,
        TimeSpan.FromSeconds(15));   // re-measured; overrides the 10 s seed.

    // ParaMud cadence — the stock natural cycle split into thirds on a 10 s
    // grid (measured), with rest riding the same grid. Meditate isn't split;
    // it keeps the 10 s seed cadence pending re-verification.
    public static readonly RealmRegenProfile ParaMud = new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(10),
        RegenConstants.SeedMeditatingInterval);   // 10 s — to be re-verified.

    // The cadence profile for a realm family — ParaMud, else Stock.
    public static RealmRegenProfile For(RealmType realm) =>
        realm == RealmType.ParaMud ? ParaMud : Stock;
}
