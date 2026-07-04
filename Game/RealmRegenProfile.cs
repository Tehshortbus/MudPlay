namespace FujinTerm.Game;

/// <summary>
/// Per-realm regen tick <i>cadence</i> — the wall-clock interval at which each
/// of <see cref="RegenTracker"/>'s cycles delivers an observable uptick.
/// Selected off <see cref="Services.GameDataCache.ActiveRealm"/> and applied
/// via <see cref="RegenTracker.SetRealm"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stock</b> takes MMUD-Explorer's <c>modExpPerHour.bas</c> constants for
/// natural / rest (regen 30 s / rest 20 s), one uptick per interval paying the
/// full per-tick amount. Meditate rides a 15 s grid from live re-measurement,
/// overriding the MME <c>SEC_PER_MEDI_TICK</c> seed of 10 s.
/// </para>
/// <para>
/// <b>ParaMud</b> (GreaterMUD / Paradigm) splits each cycle's amount into three
/// even thirds delivered on a faster grid, so the same per-minute total arrives
/// as three-times-as-frequent, one-third-size upticks. MME carries no
/// ParaMud-specific <i>player</i> tick constants, so this cadence is derived
/// from live captures via <see cref="RegenDiagnosticsRecorder"/>: a druid's
/// natural HP tick (rate 9) was observed paying +3 every 10 s, and resting
/// (rate 27) paying +9 on the same 10 s grid — i.e. the stock 30 s natural
/// cadence divided into thirds, with rest riding the same grid at 3× the
/// amount (the resting multiplier lives in
/// <see cref="Calculators.CharacterCalculator.CalcHpRegen"/>, not here).
/// Meditate is <i>not</i> split into thirds on ParaMud — it keeps its native
/// 10 s cadence (the MME seed), pending re-verification against live captures.
/// </para>
/// <para>
/// This models only the observable <i>interval</i> — the per-tick amount is
/// learned live by <see cref="RegenStat"/>. Making the interval realm-correct
/// is what keeps the status-bar countdown honest on ParaMud (a 10 s natural
/// tick, not a stock 30 s one).
/// </para>
/// </remarks>
public readonly record struct RealmRegenProfile(
    TimeSpan StandingInterval,
    TimeSpan RestingInterval,
    TimeSpan MeditatingInterval)
{
    /// <summary>
    /// Classic Stock cadence — MME-sourced 30 / 20 s for standing / resting.
    /// Meditate rides a 15 s grid (live re-measurement), overriding the
    /// MME <c>SEC_PER_MEDI_TICK</c> seed of 10 s.
    /// </summary>
    public static readonly RealmRegenProfile Stock = new(
        RegenConstants.SeedStandingInterval,
        RegenConstants.SeedRestingInterval,
        TimeSpan.FromSeconds(15));   // re-measured; overrides the 10 s MME seed.

    /// <summary>
    /// ParaMud cadence — the stock natural cycle split into thirds on a 10 s
    /// grid (measured), with rest riding the same grid. Meditate isn't split;
    /// it keeps the 10 s MME seed cadence pending re-verification.
    /// </summary>
    public static readonly RealmRegenProfile ParaMud = new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(10),
        RegenConstants.SeedMeditatingInterval);   // 10 s — to be re-verified.

    /// <summary>The cadence profile for a realm family — ParaMud, else Stock.</summary>
    public static RealmRegenProfile For(RealmType realm) =>
        realm == RealmType.ParaMud ? ParaMud : Stock;
}
