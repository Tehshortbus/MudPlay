namespace FujinTerm.Game;

// Reasons an engine can ask the party leader to pause via
// PartyRestSync.RequestWait. The on-the-wire @wait signal carries no reason
// ("an @wait doesn't need to say why") — the reason exists only inside
// PartyRestSync so that several independent gates (auto-rest recovery plus each
// curable ailment) can hold the wait at once and @ok only fires when the LAST
// reason clears. Without this, an ailment recovering would prematurely release a
// wait the HealthManager still needs (or vice-versa).
public enum WaitReason
{
    // HealthManager auto-rest / recovery gate.
    Health,

    // Local character is poisoned.
    Poison,

    // Local character is blinded.
    Blindness,

    // Local character is confused.
    Confusion,

    // Local character is diseased.
    Disease,

    // Local character is held (movement-prevented). Registered silently — the
    // held announce travels on say as .@held and the leader is paused directly by
    // the inbound say handler, not by an @wait telepath. The reason still
    // participates in the balanced @ok-on-last-clear so a held player who is also
    // poisoned doesn't release the wait prematurely.
    Held,
}
