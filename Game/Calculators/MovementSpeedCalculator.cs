namespace FujinTerm.Game.Calculators;

/// <summary>
/// Movement-speed cap solver, matching the ParaMUD utilities' movement formula.
/// Base speed is 1100 ms; encumbrance adds <c>(enc%/100)² × 2000</c>, slowness
/// adds <c>slowness × 7</c>, and quickness subtracts <c>quickness × 10</c>. The
/// 1000 ms (one-second) cap is the target: below it you carry spare quickness,
/// above it you move slower than the cap and need more. Pure math — the caller
/// supplies the current encumbrance / quickness / slowness.
/// </summary>
public static class MovementSpeedCalculator
{
    /// <summary>The one-second (1000 ms) movement cap every speed is measured against.</summary>
    public const double CapMillis = 1000.0;

    private const double BaseMillis = 1100.0;

    /// <summary>Solve the movement speed and its distance to the cap.</summary>
    /// <param name="encumbrancePercent">Carry weight as a 0–100 percentage of max.</param>
    /// <param name="quickness">Total quickness stat (subtracts from the speed).</param>
    /// <param name="slowness">Any slowness effect (adds to the speed); 0 when none.</param>
    public static MovementSpeedResult Compute(int encumbrancePercent, int quickness, int slowness)
    {
        double speed = BaseMillis;

        if (encumbrancePercent > 0)
        {
            double frac = encumbrancePercent / 100.0;
            speed += frac * frac * 2000.0;
        }
        if (slowness > 0) speed += slowness * 7.0;
        if (quickness > 0) speed -= quickness * 10.0;

        if (speed < CapMillis)
            return new MovementSpeedResult(speed, MovementCapState.AboveCap, (CapMillis - speed) / 10.0);
        if (speed > CapMillis)
            return new MovementSpeedResult(speed, MovementCapState.TooSlow, (speed - CapMillis) / 10.0);
        return new MovementSpeedResult(speed, MovementCapState.AtCap, 0.0);
    }
}
