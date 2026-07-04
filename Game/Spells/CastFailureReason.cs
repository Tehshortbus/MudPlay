namespace FujinTerm.Game.Spells;

// Why a cast was rejected by the server (or the local CastCoordinator's gate).
// Surfaces on the CastCoordinator.CastFailed event so downstream engines
// (CastingDirector) can decide whether to skip the spell for the rest of the
// round, the rest of the room, or retry.
public enum CastFailureReason
{
    // Local gate: a recent cast hasn't cleared its cooldown, or the cast-blocked
    // latch is still held.
    Blocked,

    // Server: "You attempt to cast X, but fail." — concentration roll lost or the
    // spell fizzled. Retry next round.
    Fizzled,

    // Server: "You do not have enough mana to cast that spell."
    NotEnoughMana,

    // Server: "You have already cast a spell this round!"
    AlreadyCastThisRound,

    // Server: "You lost your concentration on the spell!" — mid-cast interrupt
    // (took damage during prep, broke stealth, etc.).
    Interrupted,
}
