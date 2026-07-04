namespace FujinTerm.Game.Combat;

// Immutable snapshot of how the session's wall-clock time divided across the
// player's activities, produced by TimeAnalysisTracker.Snapshot for the Time
// Analysis panel (a reproduction of the MegaMUD Time Analysis screen). Carries
// raw durations; the percentage helpers are derived so the window binds them
// directly.
//
// The five activity durations — Waiting, Moving, Attacking, RestingHp, RestingMa
// — are mutually exclusive (the player is in exactly one at any instant) and
// partition TimeOn, so they sum to it. Blinded, Poisoned, Diseased, Confused,
// and Held are overlays: an affliction runs concurrently with whatever the
// player is doing (you stay poisoned while you fight or rest), so each is
// measured independently and is at most TimeOn — they are not part of the
// activity partition and must not be added to it.
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
    // Total time resting, regardless of which pool was regenerating.
    public TimeSpan Resting => RestingHp + RestingMa;

    // Each *Percent is that activity / overlay's share of TimeOn, 0–100.
    public double WaitingPercent => Pct(Waiting);
    public double MovingPercent => Pct(Moving);
    public double AttackingPercent => Pct(Attacking);
    public double RestingHpPercent => Pct(RestingHp);
    public double RestingMaPercent => Pct(RestingMa);
    public double BlindedPercent => Pct(Blinded);
    public double PoisonedPercent => Pct(Poisoned);
    public double DiseasedPercent => Pct(Diseased);
    public double ConfusedPercent => Pct(Confused);
    public double HeldPercent => Pct(Held);

    private double Pct(TimeSpan part) =>
        TimeOn <= TimeSpan.Zero ? 0 : 100.0 * part.TotalSeconds / TimeOn.TotalSeconds;
}
