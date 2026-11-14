namespace MudPlay.Game.Calculators;

// MajorMUD experience-curve formulas. Produces the per-class/race exp chart
// percentage and the cumulative exp required to reach a level, with separate
// Stock and ParaMUD progressions that match the game's own overflow handling.
// All methods are pure.
public static class ExperienceTableCalculator
{
    // Experience chart percentage from a class + race exp-table value:
    // (classExpTable + 100) + raceExpTable.
    public static int CalcExpChart(int classExpTable, int raceExpTable)
    {
        return (classExpTable + 100) + raceExpTable;
    }

    // Cumulative exp needed to reach the given level for a character whose exp
    // chart is chart. Dispatches to the Stock or ParaMUD progression. Saturates
    // at long.MaxValue rather than overflowing.
    public static long CalcExpNeeded(int level, int chart, RealmType realmType)
    {
        return realmType == RealmType.ParaMud
            ? CalcExpNeeded_ParaMud(level, chart)
            : CalcExpNeeded_Stock(level, chart);
    }

    // Estimate time to reach a target level given a current exp total and an
    // exp-per-hour rate. Returns null when the rate is non-positive (no data), or
    // TimeSpan.Zero when already there. Capped at MaxTimeToLevel: the first kill of a
    // session at a high level (billions of exp to go at 15 an hour) comes to
    // thousands of years, which callers add to the clock, and past the year 9999 that
    // throws (Crash-20261114-125113).
    public static System.TimeSpan? CalcTimeToLevel(long expNeeded, long currentExp, long expPerHour)
    {
        if (expPerHour <= 0) return null;
        long remaining = expNeeded - currentExp;
        if (remaining <= 0) return System.TimeSpan.Zero;
        double hours = (double)remaining / expPerHour;
        return hours >= MaxTimeToLevel.TotalHours ? MaxTimeToLevel : System.TimeSpan.FromHours(hours);
    }

    // Further off than any session can mean; still a time that reads as "not soon".
    public static readonly System.TimeSpan MaxTimeToLevel = System.TimeSpan.FromDays(100 * 365);

    // A time to level past a year is shown as only that (user, 2026-11-14): a figure
    // that far off says no more than that the rate is too low yet to mean anything.
    public static readonly System.TimeSpan OneYear = System.TimeSpan.FromDays(365);
    public const string OverOneYear = "over 1 year";

    // Compact time-to-level rendering, shared by every surface that shows a TNL
    // (the @exp reply, the status bar). Tiered so each magnitude reads cleanly:
    //   > 1 year → "over 1 year"
    //   >= 25h  → "1d 1h 0m"   (rolls up to days rather than a 25+ hour count)
    //   >= 90m  → "4h 10m"     (hours + minutes)
    //   >= 10m  → "89m"        (plain minutes — 60–89m stays "89m", not "1h 29m")
    //   >= 1m   → "4m 12s"     (the last ten minutes count down in seconds)
    //   else    → "9s"
    // The 90-minute / 25-hour thresholds are deliberate: an hour count only earns
    // its "Hh Mm" shape once there's more than an hour and a half left, and days
    // only once past a full day plus an hour.
    public static string FormatTimeToLevel(System.TimeSpan ts)
    {
        if (ts > OneYear) return OverOneYear;
        if (ts.TotalHours >= 25) return $"{ts.Days}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 90) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 10) return $"{(int)ts.TotalMinutes}m";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    // ----- Private progressions --------------------------------------------

    private static long CalcExpNeeded_ParaMud(int level, int chart)
    {
        // nRes = IDiv((nChart * 1000), 100) = chart * 10
        double nRes = chart * 10.0;

        int nIters = level - 1;
        if (nIters < 0) nIters = 0;

        for (int i = 0; i < nIters; i++)
        {
            double scaleMul, scaleDiv;

            int levelTarget = i + 1;

            if (i < 26)
            {
                (scaleMul, scaleDiv) = GetExpModifiers_ParaMud(levelTarget);
            }
            else if (levelTarget < 34)
            {
                scaleMul = 115.0;
                scaleDiv = 100.0;
            }
            else
            {
                // ParaMUD 1.9 tapers the growth from level 34: one point off the
                // 115% every 5 levels, floored at 108%. The flat 115/109/108
                // brackets (115% through level 55) are the pre-1.9 curve, and
                // they overstate the exp for every level past 35 — up to ~22%
                // around level 60.
                scaleMul = System.Math.Max(108.0, 115.0 - ((levelTarget - 34) / 5));
                scaleDiv = 100.0;
            }

            // Overflow-safe multiplication matching the game's own 64-bit
            // multiply / integer-divide overflow handling.
            double prod = nRes * scaleMul;
            if (prod <= 9.2e18)
            {
                nRes = System.Math.Truncate(prod / scaleDiv);
            }
            else
            {
                double reduced = System.Math.Truncate(nRes / 100.0);
                prod = reduced * scaleMul;
                if (prod <= 9.2e18)
                {
                    nRes = System.Math.Truncate(prod / scaleDiv) * 100.0;
                }
                else
                {
                    double reduced2 = System.Math.Truncate(reduced / 100.0);
                    nRes = System.Math.Truncate(reduced2 * scaleMul / scaleDiv) * 100.0 * 100.0;
                }
            }
        }

        try
        {
            return checked((long)nRes);
        }
        catch (System.OverflowException)
        {
            return long.MaxValue;
        }
    }

    private static long CalcExpNeeded_Stock(int level, int chart)
    {
        // The game uses UINT rollover handling + a billions tabulator for very
        // large values; we use double to match its fixed-point intermediate
        // precision.
        double runningExp = 0;
        double billionsTabulator = 0;
        const double MAX_UINT = 4294967295.0;

        for (int i = 1; i <= level; i++)
        {
            if (i == 1)
            {
                runningExp = 0;
            }
            else if (i == 2)
            {
                runningExp = chart * 10.0;
            }
            else
            {
                double expMul, expDiv;

                // The boundary is `i <= 27` — level 27 takes the modifier
                // "else" 23/20 step (value-equivalent to the 115/100 bracket,
                // but kept exact to the game's progression).
                if (i <= 27)
                {
                    (expMul, expDiv) = GetExpModifiers_Stock(i);
                }
                else if (i <= 55)
                {
                    expMul = 115.0;
                    expDiv = 100.0;
                }
                else if (i <= 58)
                {
                    expMul = 109.0;
                    expDiv = 100.0;
                }
                else
                {
                    expMul = 108.0;
                    expDiv = 100.0;
                }

                if (expMul == 0 || expDiv == 0)
                {
                    // No progression for this step.
                }
                else
                {
                    double potentialNewExp = runningExp * expMul;

                    double alternateNewExp;
                    if (potentialNewExp > MAX_UINT)
                    {
                        // UINT rollover handling — scale down until it fits.
                        int numDivides = 0;
                        double tempRunning = runningExp;
                        while (potentialNewExp > MAX_UINT)
                        {
                            tempRunning = System.Math.Truncate(tempRunning / 100.0);
                            potentialNewExp = tempRunning * expMul;
                            numDivides++;
                        }
                        if (numDivides > 1)
                            alternateNewExp = System.Math.Truncate(tempRunning * expMul * 100.0 / expDiv);
                        else
                            alternateNewExp = System.Math.Truncate(potentialNewExp / expDiv);

                        for (int d = 0; d < numDivides; d++)
                            alternateNewExp *= 100.0;
                    }
                    else
                    {
                        alternateNewExp = System.Math.Truncate(potentialNewExp / expDiv);
                    }

                    // Billions tabulator rollover handling.
                    double j = 1000000.0 * expMul * billionsTabulator;
                    while (j > MAX_UINT)
                        j = j - MAX_UINT - 1.0;
                    while (j >= 1000000000.0)
                    {
                        j -= 1000000000.0;
                        billionsTabulator += 1.0;
                    }

                    double k = j + alternateNewExp;
                    while (k >= 1000000000.0)
                    {
                        k -= 1000000000.0;
                        billionsTabulator += 1.0;
                    }

                    runningExp = k;
                }
            }
        }

        double totalExp = runningExp + (billionsTabulator * 1000000000.0);
        try
        {
            return checked((long)totalExp);
        }
        catch (System.OverflowException)
        {
            return long.MaxValue;
        }
    }

    private static (double mul, double div) GetExpModifiers_ParaMud(int level)
    {
        return level switch
        {
            1 => (1, 1),
            2 => (40, 20),
            3 or 4 => (44, 24),
            5 or 6 => (48, 28),
            7 or 8 => (52, 32),
            9 or 10 => (56, 36),
            11 or 12 => (60, 40),
            13 or 14 => (65, 45),
            15 or 16 => (70, 50),
            17 => (75, 55),
            >= 18 and <= 25 => (50, 40),
            >= 26 and <= 32 => (23, 20),
            _ => (0, 0)
        };
    }

    private static (double mul, double div) GetExpModifiers_Stock(int level)
    {
        return level switch
        {
            3 => (40, 20),
            4 or 5 => (44, 24),
            6 or 7 => (48, 28),
            8 or 9 => (52, 32),
            10 or 11 => (56, 36),
            12 or 13 => (60, 40),
            14 or 15 => (65, 45),
            16 or 17 => (70, 50),
            18 => (75, 55),
            _ when level <= 26 => (50, 40),
            _ => (23, 20)
        };
    }
}
