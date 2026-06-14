namespace FujinTerm.Models.Profile;

/// <summary>
/// Recovery state of a <see cref="DeathRecord"/>'s deathpile, surfaced
/// in the Workshop DEATH section with a red/yellow/green stoplight tint.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="Active"/> — the deathpile was made and we have
/// neither re-entered the room nor recovered anything from it.</item>
/// <item><see cref="Partial"/> — we returned to the death room but did
/// not recover every item that was lost.</item>
/// <item><see cref="Recovered"/> — we returned and recovered everything,
/// or the user manually marked the record recovered.</item>
/// </list>
/// </remarks>
public enum DeathRecoveryStatus
{
    Active,
    Partial,
    Recovered,
}
