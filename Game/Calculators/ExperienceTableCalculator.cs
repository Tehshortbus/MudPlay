namespace FujinTerm.Game.Calculators;

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
    // TimeSpan.Zero when already there.
    public static System.TimeSpan? CalcTimeToLevel(long expNeeded, long currentExp, long expPerHour)
    {
        if (expPerHour <= 0) return null;
        long remaining = expNeeded - currentExp;
        if (remaining <= 0) return System.TimeSpan.Zero;
        return System.TimeSpan.FromHours((double)remaining / expPerHour);
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

            if (i < 26)
            {
                (scaleMul, scaleDiv) = GetExpModifiers_ParaMud(i + 1);
            }
            else if (i < 54)
            {
                scaleMul = 115.0;
                scaleDiv = 100.0;
            }
            else if (i < 57)
            {
                scaleMul = 109.0;
                scaleDiv = 100.0;
            }
            else
            {
                scaleMul = 108.0;
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
