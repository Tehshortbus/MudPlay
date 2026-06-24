namespace FujinTerm.Game.Combat;

/// <summary>
/// Immutable snapshot of how the session's wall-clock time divided across the
/// player's activities, produced by <see cref="TimeAnalysisTracker.Snapshot"/>
/// for the Phase 11 Time Analysis panel (a reproduction of the MegaMUD Time
/// Analysis screen). Carries raw durations; the percentage helpers are derived
/// so the window binds them directly.
/// </summary>
/// <remarks>
/// The five <i>activity</i> durations — <see cref="Waiting"/>,
/// <see cref="Moving"/>, <see cref="Attacking"/>, <see cref="RestingHp"/>,
/// <see cref="RestingMa"/> — are mutually exclusive (the player is in exactly
/// one at any instant) and partition <see cref="TimeOn"/>, so they sum to it.
/// <see cref="Blinded"/>, <see cref="Poisoned"/>, <see cref="Diseased"/>,
/// <see cref="Confused"/>, and <see cref="Held"/> are <i>overlays</i>: an
/// affliction runs concurrently with whatever the player is doing (you stay
/// poisoned while you fight or rest), so each is measured independently and is
/// at most <see cref="TimeOn"/> — they are <b>not</b> part of the activity
/// partition and must not be added to it.
/// </remarks>
public readonly record struct TimeAnalysisStats(
    TimeSpan TimeOn,
    // ----- Activity partition (mutually exclusive; sums to TimeOn) -----
    TimeSpan Waiting,
    TimeSpan Moving,
    TimeSpan Attacking,
    TimeSpan RestingHp,
    TimeSpan RestingMa,
    // ----- Affliction overlays (concurrent; each ≤ TimeOn) -----
    TimeSpan Blinded,
    TimeSpan Poisoned,
    TimeSpan Diseased,
    TimeSpan Confused,
    TimeSpan Held)
{
    /// <summary>Total time resting, regardless of which pool was regenerating.</summary>
    public TimeSpan Resting => RestingHp + RestingMa;

    /// <summary>Fraction of the session spent idle and standing, 0–100.</summary>
    public double WaitingPercent => Pct(Waiting);

    /// <summary>Fraction of the session spent travelling between rooms, 0–100.</summary>
    public double MovingPercent => Pct(Moving);

    /// <summary>Fraction of the session spent in combat, 0–100.</summary>
    public double AttackingPercent => Pct(Attacking);

    /// <summary>Fraction of the session spent resting for HP, 0–100.</summary>
    public double RestingHpPercent => Pct(RestingHp);

    /// <summary>Fraction of the session spent resting / meditating for mana, 0–100.</summary>
    public double RestingMaPercent => Pct(RestingMa);

    /// <summary>Fraction of the session spent blinded, 0–100 (overlay).</summary>
    public double BlindedPercent => Pct(Blinded);

    /// <summary>Fraction of the session spent poisoned, 0–100 (overlay).</summary>
    public double PoisonedPercent => Pct(Poisoned);

    /// <summary>Fraction of the session spent diseased, 0–100 (overlay).</summary>
    public double DiseasedPercent => Pct(Diseased);

    /// <summary>Fraction of the session spent confused, 0–100 (overlay).</summary>
    public double ConfusedPercent => Pct(Confused);

    /// <summary>Fraction of the session spent held / movement-prevented, 0–100 (overlay).</summary>
    public double HeldPercent => Pct(Held);

    private double Pct(TimeSpan part) =>
        TimeOn <= TimeSpan.Zero ? 0 : 100.0 * part.TotalSeconds / TimeOn.TotalSeconds;
}
