namespace FujinTerm.Models.Profile;

// What FujinTerm does when a profile loads and the connection comes up.
// User-configurable on the Settings → General tab. Consumed by the loop /
// Auto-Lair engines.
public enum InitialTask
{
    // Sit at the in-game prompt. User drives manually.
    DoNothing = 0,

    // Start the configured loop on logon (see GeneralSettings.DefaultLoopName).
    BeginLoop = 1,

    // Start the Auto-Lair scheduler on logon.
    BeginAutoLair = 2,
}
