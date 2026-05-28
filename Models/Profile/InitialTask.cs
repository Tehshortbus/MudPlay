namespace FujinTerm.Models.Profile;

/// <summary>
/// What FujinTerm does when a profile loads and the connection comes up.
/// User-configurable on the Settings → General tab. Consumed by the
/// loop / Auto-Lair engines as they ship (Phase 7).
/// </summary>
public enum InitialTask
{
    /// <summary>Sit at the in-game prompt. User drives manually.</summary>
    DoNothing = 0,

    /// <summary>Start the configured loop on logon (see <see cref="GeneralSettings.DefaultLoopName"/>).</summary>
    BeginLoop = 1,

    /// <summary>Start the Auto-Lair scheduler on logon.</summary>
    BeginAutoLair = 2,
}
