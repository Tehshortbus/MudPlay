namespace FujinTerm.Game.Stealth;

/// <summary>
/// Coarse FSM state for <see cref="StealthManager"/>. The granular
/// in-flight states (<see cref="AttemptingSneak"/> /
/// <see cref="AttemptingHide"/>) are observable separately from the
/// established states (<see cref="Sneaking"/> / <see cref="Hidden"/>)
/// so CombatManager's backstab-window suppression can wait for the
/// established transition before opening the backstab attempt.
/// </summary>
public enum StealthState
{
    /// <summary>Not stealthed. Default for fresh state + after any
    /// confirmed loss.</summary>
    Idle,

    /// <summary>We sent <c>sn</c> and are waiting for the server echo.
    /// A clean <c>Attempting to sneak...</c> moves us to
    /// <see cref="Sneaking"/> (armed); the failure suffix
    /// <c>You don't think you're sneaking.</c> bounces us to
    /// <see cref="Failed"/> (and, under auto-sneak, triggers a resend
    /// that returns here).</summary>
    AttemptingSneak,

    /// <summary>Sneak established — either the clean
    /// <c>Attempting to sneak...</c> ACK (armed, ready to move) or a
    /// post-move <c>Sneaking...</c> confirmation.
    /// <see cref="PlayerState.IsSneaking"/> is true while in this
    /// state.</summary>
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
