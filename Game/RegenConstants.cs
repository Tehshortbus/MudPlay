namespace MudPlay.Game;

// Seed values for HP / MA regen tick intervals, the cycle lengths a regen cycle
// starts with. These are the Stock engine's own tick intervals (30 s natural / 21 s rest / 15 s meditate —
// GAME_MECHANICS "Rest and meditate tick timing"); ParaMud runs its own cadence, see
// RealmRegenProfile.
public static class RegenConstants
{
    // Passive (standing) regen tick interval.
    public static readonly TimeSpan SeedStandingInterval   = TimeSpan.FromSeconds(30);

    // Resting HP-recovery tick interval.
    public static readonly TimeSpan SeedRestingInterval    = TimeSpan.FromSeconds(21);

    // Meditating MA-recovery tick interval (kai / mana classes).
    public static readonly TimeSpan SeedMeditatingInterval = TimeSpan.FromSeconds(15);

    // How long after a heal-shaped command (cast / drink / quaff / etc.) to
    // ignore HP / MA increases — the conservative artifact filter. Hard-coded
    // for now; Settings.Health may surface a knob.
    public static readonly TimeSpan ArtifactGraceWindow    = TimeSpan.FromSeconds(3);
}
