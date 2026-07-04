namespace FujinTerm.Game.Stealth;

// Coarse FSM state for StealthManager. The granular in-flight states
// (AttemptingSneak / AttemptingHide) are observable separately from the
// established states (Sneaking / Hidden) so CombatManager's backstab-window
// suppression can wait for the established transition before opening the backstab
// attempt.
public enum StealthState
{
    // Not stealthed. Default for fresh state + after any confirmed loss.
    Idle,

    // We sent sn and are waiting for the server echo. A clean "Attempting to
    // sneak..." moves us to Sneaking (armed); the failure suffix "You don't think
    // you're sneaking." bounces us to Failed (and, under auto-sneak, triggers a
    // resend that returns here).
    AttemptingSneak,

    // Sneak established — either the clean "Attempting to sneak..." ACK (armed,
    // ready to move) or a post-move "Sneaking..." confirmation.
    // PlayerState.IsSneaking is true while in this state.
    Sneaking,

    // We sent hide but the server's confirmation hasn't landed yet.
    AttemptingHide,

    // Confirmed hidden. PlayerState.IsHidden is true while in this state.
    Hidden,

    // Most recent attempt failed ("You may not sneak right now!" or "You don't
    // think you're sneaking."). A follow-up command resets to Idle or back to
    // AttemptingSneak.
    Failed,
}
