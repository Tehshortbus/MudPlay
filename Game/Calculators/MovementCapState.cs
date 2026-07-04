namespace FujinTerm.Game.Calculators;

// Where a movement speed sits relative to the 1-second (1000 ms) cap.
public enum MovementCapState
{
    // Faster than the cap — quickness to spare.
    AboveCap,

    // Exactly at the 1-second cap.
    AtCap,

    // Slower than the cap — needs more quickness.
    TooSlow,
}
