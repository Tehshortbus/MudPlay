namespace FujinTerm.Game.Calculators;

// Movement time per step in milliseconds, whether it clears the 1-second cap,
// and the quickness distance to the cap (spare when above it, still-needed when
// below).
//   SpeedMillis    — movement time per step, in ms (1000 = the cap).
//   State          — whether the speed is above / at / below the cap.
//   QuicknessToCap — quickness you could shed (AboveCap) or still need (TooSlow);
//                    0 at the cap.
public readonly record struct MovementSpeedResult(
    double SpeedMillis,
    MovementCapState State,
    double QuicknessToCap);
