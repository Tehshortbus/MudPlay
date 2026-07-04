namespace FujinTerm.Game;

// Seed values for HP / MA regen tick intervals. Used by RegenTracker as the
// initial estimate until live observation refines the average. These are the
// documented Stock MajorMUD tick intervals (30 s natural / 20 s rest / 10 s
// meditate).
public static class RegenConstants
{
    // Passive (standing) regen tick interval.
    public static readonly TimeSpan SeedStandingInterval   = TimeSpan.FromSeconds(30);

    // Resting HP-recovery tick interval.
    public static readonly TimeSpan SeedRestingInterval    = TimeSpan.FromSeconds(20);

    // Meditating MA-recovery tick interval (kai / mana classes).
    public static readonly TimeSpan SeedMeditatingInterval = TimeSpan.FromSeconds(10);

    // How long after a heal-shaped command (cast / drink / quaff / etc.) to
    // ignore HP / MA increases — the conservative artifact filter. Hard-coded
    // for now; Settings.Health may surface a knob.
    public static readonly TimeSpan ArtifactGraceWindow    = TimeSpan.FromSeconds(3);

    // EWMA decay parameter — fraction of weight a fresh sample carries
    // (versus the running average). 0.2 = recent samples count 20 % per
    // observation. Empirically reasonable for "responsive but stable".
    public const double EwmaAlpha = 0.2;
}
