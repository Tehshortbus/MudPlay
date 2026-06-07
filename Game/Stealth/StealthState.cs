namespace FujinTerm.Game.Stealth;

/// <summary>
/// Coarse FSM state for <see cref="StealthManager"/>. The granular
/// in-flight states (<see cref="AttemptingSneak"/> /
/// <see cref="AttemptingHide"/>) are observable separately from the
/// confirmed states (<see cref="Sneaking"/> / <see cref="Hidden"/>)
/// so CombatManager's backstab-window suppression can wait for the
/// confirmed transition before opening the backstab attempt.
/// </summary>
public enum StealthState
{
    /// <summary>Not stealthed. Default for fresh state + after any
    /// confirmed loss.</summary>
    Idle,

    /// <summary>We sent <c>sneak</c>; the server replied
    /// <c>Attempting to sneak...</c> but we haven't observed the
    /// confirming <c>Sneaking...</c> on the next room entry yet.</summary>
    AttemptingSneak,

    /// <summary>Confirmed sneaking — last room entry carried
    /// <c>Sneaking...</c>. <see cref="PlayerState.IsSneaking"/> is
    /// true while in this state.</summary>
    Sneaking,

    /// <summary>We sent <c>hide</c> but the server's confirmation
    /// hasn't landed yet.</summary>
    AttemptingHide,

    /// <summary>Confirmed hidden. <see cref="PlayerState.IsHidden"/>
    /// is true while in this state.</summary>
    Hidden,

    /// <summary>Most recent attempt failed (<c>You may not sneak right
    /// now!</c> or <c>You don't think you're sneaking.</c>). A
    /// follow-up command resets to <see cref="Idle"/> or back to
    /// <see cref="AttemptingSneak"/>.</summary>
    Failed,
}
