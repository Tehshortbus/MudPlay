namespace FujinTerm.Game.Spells;

/// <summary>
/// Why a cast was rejected by the server (or the local
/// <see cref="CastCoordinator"/>'s gate). Surfaces on the
/// <see cref="CastCoordinator.CastFailed"/> event so downstream
/// engines (<c>CastingDirector</c>) can decide whether to skip the
/// spell for the rest of the round, the rest of the room, or retry.
/// </summary>
public enum CastFailureReason
{
    /// <summary>Local gate: a recent cast hasn't cleared its cooldown,
    /// or the cast-blocked latch is still held.</summary>
    Blocked,

    /// <summary>Server: "You attempt to cast X, but fail." — concentration
    /// roll lost or the spell fizzled. Retry next round.</summary>
    Fizzled,

    /// <summary>Server: "You do not have enough mana to cast that spell."</summary>
    NotEnoughMana,

    /// <summary>Server: "You have already cast a spell this round!"</summary>
    AlreadyCastThisRound,

    /// <summary>Server: "You lost your concentration on the spell!" —
    /// mid-cast interrupt (took damage during prep, broke stealth, etc.).</summary>
    Interrupted,
}
