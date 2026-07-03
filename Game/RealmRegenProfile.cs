namespace FujinTerm.Game;

/// <summary>
/// Per-realm regen tick <i>cadence</i> — the wall-clock interval at which each
/// of <see cref="RegenTracker"/>'s cycles delivers an observable uptick.
/// Selected off <see cref="Services.GameDataCache.ActiveRealm"/> and applied
/// via <see cref="RegenTracker.SetRealm"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stock</b> matches MMUD-Explorer's <c>modExpPerHour.bas</c> constants
/// (regen 30 s / rest 20 s / meditate 10 s) — one uptick per interval paying
/// the full per-tick amount.
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
/// Meditate is <i>not</i> split into thirds on ParaMud — it behaves the same
/// as Stock, keeping its native 10 s cadence.
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
    /// <summary>Classic Stock cadence — the MME-sourced 30 / 20 / 10 s intervals.</summary>
    public static readonly RealmRegenProfile Stock = new(
        RegenConstants.SeedStandingInterval,
        RegenConstants.SeedRestingInterval,
        RegenConstants.SeedMeditatingInterval);

    /// <summary>
    /// ParaMud cadence — the stock natural cycle split into thirds on a 10 s
    /// grid (measured), with rest riding the same grid. Meditate isn't split;
    /// it keeps the Stock cadence.
    /// </summary>
    public static readonly RealmRegenProfile ParaMud = new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(10),
        RegenConstants.SeedMeditatingInterval);   // unsplit — same as Stock.

    /// <summary>The cadence profile for a realm family — ParaMud, else Stock.</summary>
    public static RealmRegenProfile For(RealmType realm) =>
        realm == RealmType.ParaMud ? ParaMud : Stock;
}
