namespace FujinTerm.Game.Calculators;

// Movement-speed cap solver, matching the ParaMUD realm movement formula. Base
// speed is 1100 ms; encumbrance adds (enc%/100)^2 * 2000, slowness adds
// slowness * 7, and quickness subtracts quickness * 10. The 1000 ms (one-second)
// cap is the target: below it you carry spare quickness, above it you move
// slower than the cap and need more. Pure math — the caller supplies the current
// encumbrance / quickness / slowness.
public static class MovementSpeedCalculator
{
    // The one-second (1000 ms) movement cap every speed is measured against.
    public const double CapMillis = 1000.0;

    private const double BaseMillis = 1100.0;

    // Solve the movement speed and its distance to the cap.
    //   encumbrancePercent — carry weight as a 0-100 percentage of max.
    //   quickness          — total quickness stat (subtracts from the speed).
    //   slowness           — any slowness effect (adds to the speed); 0 when none.
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
