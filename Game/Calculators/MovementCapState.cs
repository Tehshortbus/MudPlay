namespace FujinTerm.Game.Calculators;

/// <summary>Where a movement speed sits relative to the 1-second (1000 ms) cap.</summary>
public enum MovementCapState
{
    /// <summary>Faster than the cap — quickness to spare.</summary>
    AboveCap,

    /// <summary>Exactly at the 1-second cap.</summary>
    AtCap,

    /// <summary>Slower than the cap — needs more quickness.</summary>
    TooSlow,
}
