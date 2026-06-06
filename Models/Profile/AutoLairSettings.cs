namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character "Auto-Lair" settings — the scheduler's tuning surface.
/// Stored as the <c>"AutoLair"</c> entry in <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// <para>
/// The marker set itself is BBS-scoped — saved
/// <see cref="LairSetup"/>s live under <c>Data/BBS/{bbs}/Lairs/</c>
/// because lair locations are game-data facts (every character on the
/// same BBS sees the same rooms). The knobs here are per-character
/// because they describe the player's playstyle and class capabilities
/// (a thief on light boots walks at a different pace than a wizard
/// dragging full plate).
/// </para>
/// <para>
/// All fields ship with sensible defaults the user can run on day one
/// without touching the tab. The encumbrance-gated travel-cost table
/// is seeded from a single-character run-without-encumbrance measurement
/// (1.5 s / hop), then refined per bucket once
/// <see cref="OtherSettings.LogMovementHopTiming"/> has produced data
/// for the user to enter.
/// </para>
/// </remarks>
public sealed class AutoLairSettings
{
    // ----- Routing heuristic ----------------------------------------

    /// <summary>
    /// Scoring heuristic the scheduler uses to rank candidate lairs.
    /// Maps 1:1 onto <see cref="Game.Map.AutoLairHeuristic"/>:
    /// <list type="bullet">
    ///   <item><see cref="Game.Map.AutoLairHeuristic.Default"/> — balance
    ///   wasted-respawn vs idle-wait under <see cref="IdlePenalty"/>.</item>
    ///   <item><see cref="Game.Map.AutoLairHeuristic.Throughput"/> —
    ///   minimise wasted respawn only; idle wait is free.</item>
    /// </list>
    /// </summary>
    public Game.Map.AutoLairHeuristic Heuristic { get; set; } = Game.Map.AutoLairHeuristic.Default;

    /// <summary>
    /// Weight on idle-wait time under the
    /// <see cref="Game.Map.AutoLairHeuristic.Default"/> heuristic.
    /// 0 = ignore idle (= Throughput); 1 = treat idle and wasted equally
    /// (default); higher = prefer ready-now lairs over standing around.
    /// </summary>
    public double IdlePenalty { get; set; } = 1.0;

    /// <summary>
    /// How long (in seconds) the scheduler stays in
    /// <see cref="Game.Map.AutoLairPhase.Engaging"/> after entering a
    /// lair before re-evaluating. Stop-gap until Phase 13's
    /// CombatManager exposes a combat-ended signal. Default 30 s.
    /// </summary>
    public int EngageTimeoutSeconds { get; set; } = 30;

    // ----- Travel cost model ----------------------------------------

    /// <summary>
    /// Picks which <see cref="Game.Map.ITravelCostModel"/> the scheduler
    /// uses for hop → duration conversion.
    /// </summary>
    public AutoLairTravelCostMode TravelCostMode { get; set; } = AutoLairTravelCostMode.Flat;

    /// <summary>
    /// Seconds per hop for <see cref="AutoLairTravelCostMode.Flat"/>.
    /// Default 1.5 s — a single-character run-without-encumbrance
    /// observation. Tune with <see cref="OtherSettings.LogMovementHopTiming"/>.
    /// </summary>
    public double FlatSecondsPerHop { get; set; } = 1.5;

    /// <summary>
    /// Encumbrance bucket → seconds per hop, used when
    /// <see cref="TravelCostMode"/> is
    /// <see cref="AutoLairTravelCostMode.EncumbranceGated"/>. Defaults
    /// scale with bucket — None is the fastest, Encumbered the slowest.
    /// </summary>
    public EncumbranceHopTimes HopTimesByEncumbrance { get; set; } = new();
}

/// <summary>
/// Which travel-cost model <see cref="AutoLairSettings"/> hands the
/// scheduler at <see cref="Services.AppServices"/> wire-up.
/// </summary>
public enum AutoLairTravelCostMode
{
    /// <summary>Single flat seconds-per-hop, regardless of encumbrance.</summary>
    Flat = 0,
    /// <summary>Per-bucket seconds-per-hop (None / Light / Medium / Heavy / Encumbered).</summary>
    EncumbranceGated = 1,
}

/// <summary>
/// Per-encumbrance seconds-per-hop lookup. Plain POCO so
/// <see cref="System.Text.Json"/> round-trips it without a converter.
/// Defaults are scaled from the flat 1.5 s baseline; the
/// <see cref="HopTimingCalibrator"/> feeds real numbers once the user
/// runs a calibration session.
/// </summary>
public sealed class EncumbranceHopTimes
{
    public double None       { get; set; } = 1.0;
    public double Light      { get; set; } = 1.5;
    public double Medium     { get; set; } = 2.5;
    public double Heavy      { get; set; } = 4.0;
    public double Encumbered { get; set; } = 6.0;

    /// <summary>Lookup helper for the encumbrance-gated travel-cost model.</summary>
    public double For(Game.EncumbranceLevel level) => level switch
    {
        Game.EncumbranceLevel.None       => None,
        Game.EncumbranceLevel.Light      => Light,
        Game.EncumbranceLevel.Medium     => Medium,
        Game.EncumbranceLevel.Heavy      => Heavy,
        Game.EncumbranceLevel.Encumbered => Encumbered,
        _ => Light, // Unknown — fall back to the centre bucket
    };
}
