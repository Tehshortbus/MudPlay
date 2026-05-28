namespace FujinTerm.Game;

/// <summary>
/// Seed values for HP / MA regen tick intervals. Used by
/// <see cref="RegenTracker"/> as the initial estimate until live
/// observation refines the average. Sourced from
/// syntax53/MMUD-Explorer (<c>modExpPerHour.bas</c>:
/// <c>SEC_PER_REGEN_TICK</c> / <c>SEC_PER_REST_TICK</c> /
/// <c>SEC_PER_MEDI_TICK</c>).
/// </summary>
public static class RegenConstants
{
    /// <summary>Passive (standing) regen tick interval.</summary>
    public static readonly TimeSpan SeedStandingInterval   = TimeSpan.FromSeconds(30);

    /// <summary>Resting HP-recovery tick interval.</summary>
    public static readonly TimeSpan SeedRestingInterval    = TimeSpan.FromSeconds(20);

    /// <summary>Meditating MA-recovery tick interval (kai / mana classes).</summary>
    public static readonly TimeSpan SeedMeditatingInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long after a heal-shaped command (cast / drink / quaff / etc.)
    /// to ignore HP / MA increases — the conservative artifact filter.
    /// Hard-coded for now; Phase 4 Settings.Health may surface a knob.
    /// </summary>
    public static readonly TimeSpan ArtifactGraceWindow    = TimeSpan.FromSeconds(3);

    /// <summary>
    /// EWMA decay parameter — fraction of weight a fresh sample carries
    /// (versus the running average). 0.2 = recent samples count 20 % per
    /// observation. Empirically reasonable for "responsive but stable".
    /// </summary>
    public const double EwmaAlpha = 0.2;
}
