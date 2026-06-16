using System.Collections.Generic;
using FujinTerm.Models.Profile;

namespace FujinTerm.Game.Calculators;

/// <summary>The six trainable character stats, in CP-grid column order.</summary>
public enum CpStat { Strength, Intellect, Willpower, Agility, Health, Charm }

/// <summary>
/// Per-row result of <see cref="CpPlanCalculator.Compute"/>: the (possibly
/// clamped) target stats for the level plus its CP accounting.
/// <see cref="CpEarnedTotal"/> is the cumulative CP available by this level
/// (starting unspent CP + all CP gained through it); <see cref="CpLeft"/> is
/// what remains after the plan's spending. Stats are clamped so CP Left never
/// goes negative (floored at 0).
/// </summary>
public readonly record struct CpRowResult(
    int Level,
    int Strength, int Intellect, int Willpower, int Agility, int Health, int Charm,
    int CpEarnedTotal, int CpLeft);

/// <summary>
/// Pure CP-plan grid math for the Workshop CP Allocation tab. Walks the planned
/// levels from a raw-base baseline, computing each level's CP spend and the
/// running CP balance, with per-stat cost from <see cref="CharacterCalculator"/>.
/// No UI / game-data reads — the caller resolves the baseline (live stats minus
/// equipment), the race min/max, the realm, and the current unspent CP.
/// </summary>
/// <remarks>
/// CP cost is cumulative: raising a stat costs more the further it is above the
/// race minimum (<see cref="CharacterCalculator.CalcCpCostForStatPoint"/>), so a
/// level's cost sums each stat's range from the previous level's value to this
/// level's (<see cref="CharacterCalculator.CalcTotalCpCostForStatRange"/>). A
/// target below the previous value (you can't untrain) clamps up to it; above
/// the race max clamps down to it.
/// <para>
/// <b>Affordability</b>: a level can't spend more CP than it has (carryover +
/// CP gained that level), so when a row's targets would overspend, the cell the
/// user just edited (<paramref name="preferredTrim"/>) is reduced point-by-point
/// until the row fits — falling back to the <i>most-raised</i> stat once the
/// edited one is back at the previous level (or when no edit context is given).
/// CP Left therefore never goes negative; it floors at 0.
/// </para>
/// </remarks>
public static class CpPlanCalculator
{
    private const int StatCount = 6;

    public static IReadOnlyList<CpRowResult> Compute(
        CpPlanEntry baseline,
        IReadOnlyList<CpPlanEntry> rows,
        CpPlanEntry raceMin,
        CpPlanEntry raceMax,
        int initialCp,
        RealmType realm,
        CpStat? preferredTrim = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(raceMin);
        ArgumentNullException.ThrowIfNull(raceMax);

        int[] min = ToArray(raceMin);
        int[] max = ToArray(raceMax);

        var results = new List<CpRowResult>(rows.Count);
        int[] prev = ToArray(baseline);
        int earnedTotal = initialCp;   // cumulative CP available (incl. starting unspent)
        int carryover = initialCp;     // running CP left after spending

        foreach (CpPlanEntry row in rows)
        {
            int[] target = ToArray(row);
            for (int i = 0; i < StatCount; i++)
                target[i] = Clamp(target[i], prev[i], max[i]);

            int gained = CharacterCalculator.CalcCpGainedAtLevel(row.Level);
            int available = carryover + gained;

            // Can't overspend: trim the edited cell (or most-raised) until it fits.
            while (RowCost(target, prev, min, realm) > available)
            {
                int s = ChooseTrim(target, prev, preferredTrim);
                if (s < 0) break;   // nothing left to reduce (shouldn't happen — prev costs 0)
                target[s]--;
            }

            int used = RowCost(target, prev, min, realm);
            earnedTotal += gained;
            carryover = available - used;
            if (carryover < 0) carryover = 0;   // safety; the trim loop keeps used <= available

            results.Add(new CpRowResult(
                row.Level, target[0], target[1], target[2], target[3], target[4], target[5],
                earnedTotal, carryover));

            prev = target;
        }

        return results;
    }

    private static int RowCost(int[] target, int[] prev, int[] min, RealmType realm)
    {
        int total = 0;
        for (int i = 0; i < StatCount; i++)
            total += CharacterCalculator.CalcTotalCpCostForStatRange(min[i], prev[i], target[i], realm);
        return total;
    }

    // The cell to trim when a row overspends: the one the user just edited (while
    // it's still above the previous level), else the most-raised stat. This keeps
    // an overspend confined to the cell being typed instead of clawing back an
    // unrelated stat the user already settled.
    private static int ChooseTrim(int[] target, int[] prev, CpStat? preferred)
    {
        if (preferred is { } p && target[(int)p] > prev[(int)p]) return (int)p;
        return MostRaisedStat(target, prev);
    }

    // Index of the stat raised furthest above the previous level, or -1 when
    // nothing is above the previous values.
    private static int MostRaisedStat(int[] target, int[] prev)
    {
        int best = -1, bestDelta = 0;
        for (int i = 0; i < StatCount; i++)
        {
            int delta = target[i] - prev[i];
            if (delta > bestDelta) { bestDelta = delta; best = i; }
        }
        return best;
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min) return min;
        if (max > 0 && value > max) return max;
        return value;
    }

    private static int[] ToArray(CpPlanEntry e) =>
        new[] { e.Strength, e.Intellect, e.Willpower, e.Agility, e.Health, e.Charm };
}
