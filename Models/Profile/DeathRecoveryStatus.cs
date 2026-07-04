namespace FujinTerm.Models.Profile;

// Recovery state of a DeathRecord's deathpile, surfaced in the Workshop DEATH
// section with a red/yellow/green stoplight tint.
//   Active — the deathpile was made and we have neither re-entered the room nor
//     recovered anything from it.
//   Partial — we returned to the death room but did not recover every item that
//     was lost.
//   Recovered — we returned and recovered everything, or the user manually
//     marked the record recovered.
public enum DeathRecoveryStatus
{
    Active,
    Partial,
    Recovered,
}
