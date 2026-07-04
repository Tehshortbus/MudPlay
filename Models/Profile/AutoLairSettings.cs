namespace FujinTerm.Models.Profile;

// Per-character "Auto-Lair" settings — the scheduler's tuning surface.
// Stored as the "AutoLair" entry in CharacterProfile.Settings.
//
// The marker set itself is BBS-scoped — saved LairSetups live under
// Data/BBS/{bbs}/Lairs/ because lair locations are game-data facts (every
// character on the same BBS sees the same rooms). The knobs here are
// per-character because they describe the player's playstyle and class
// capabilities (a thief on light boots walks at a different pace than a
// wizard dragging full plate).
//
// All fields ship with sensible defaults the user can run on day one
// without touching the tab. The encumbrance-gated travel-cost table is
// seeded from a single-character run-without-encumbrance measurement
// (1.5 s / hop), then refined per bucket once
// OtherSettings.LogMovementHopTiming has produced data for the user to enter.
public sealed class AutoLairSettings
{
    // ----- Routing heuristic ----------------------------------------

    // Scoring heuristic the scheduler uses to rank candidate lairs.
    // Maps 1:1 onto Game.Map.AutoLairHeuristic:
    //   Default — balance wasted-respawn vs idle-wait under IdlePenalty.
    //   Throughput — minimise wasted respawn only; idle wait is free.
    public Game.Map.AutoLairHeuristic Heuristic { get; set; } = Game.Map.AutoLairHeuristic.Default;

    // Weight on idle-wait time under the Default heuristic.
    // 0 = ignore idle (= Throughput); 1 = treat idle and wasted equally
    // (default); higher = prefer ready-now lairs over standing around.
    public double IdlePenalty { get; set; } = 1.0;

    // How long (in seconds) the scheduler stays in the Engaging phase after
    // entering a lair before re-evaluating. Default 30 s.
    public int EngageTimeoutSeconds { get; set; } = 30;

    // ----- Travel cost model ----------------------------------------

    // Picks which Game.Map.ITravelCostModel the scheduler uses for
    // hop → duration conversion.
    public AutoLairTravelCostMode TravelCostMode { get; set; } = AutoLairTravelCostMode.Flat;

    // Seconds per hop for the Flat cost mode. Default 1.5 s — a
    // single-character run-without-encumbrance observation. Tune with
    // OtherSettings.LogMovementHopTiming.
    public double FlatSecondsPerHop { get; set; } = 1.5;

    // Encumbrance bucket → seconds per hop, used when TravelCostMode is
    // EncumbranceGated. Defaults scale with bucket — None is the fastest,
    // Encumbered the slowest.
    public EncumbranceHopTimes HopTimesByEncumbrance { get; set; } = new();
}

// Which travel-cost model AutoLairSettings hands the scheduler at
// AppServices wire-up.
public enum AutoLairTravelCostMode
{
    // Single flat seconds-per-hop, regardless of encumbrance.
    Flat = 0,
    // Per-bucket seconds-per-hop (None / Light / Medium / Heavy / Encumbered).
    EncumbranceGated = 1,
}

// Per-encumbrance seconds-per-hop lookup. Plain POCO so System.Text.Json
// round-trips it without a converter. Defaults are scaled from the flat
// 1.5 s baseline; the HopTimingCalibrator feeds real numbers once the user
// runs a calibration session.
public sealed class EncumbranceHopTimes
{
    public double None       { get; set; } = 1.0;
    public double Light      { get; set; } = 1.5;
    public double Medium     { get; set; } = 2.5;
    public double Heavy      { get; set; } = 4.0;
    public double Encumbered { get; set; } = 6.0;

    // Lookup helper for the encumbrance-gated travel-cost model.
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
