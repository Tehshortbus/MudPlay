namespace FujinTerm.Game.Calculators;

/// <summary>
/// Result of <see cref="MovementSpeedCalculator.Compute"/> — the movement time
/// per step in milliseconds, whether it clears the 1-second cap, and the
/// quickness distance to the cap (spare when above it, still-needed when below).
/// </summary>
/// <param name="SpeedMillis">Movement time per step, in milliseconds (1000 = the cap).</param>
/// <param name="State">Whether the speed is above / at / below the cap.</param>
/// <param name="QuicknessToCap">Quickness you could shed (<see cref="MovementCapState.AboveCap"/>) or still need (<see cref="MovementCapState.TooSlow"/>); 0 at the cap.</param>
public readonly record struct MovementSpeedResult(
    double SpeedMillis,
    MovementCapState State,
    double QuicknessToCap);
