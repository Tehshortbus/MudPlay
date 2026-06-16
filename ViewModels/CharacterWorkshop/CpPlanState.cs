using System;
using System.Collections.Generic;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// Live CP-plan state shared between the CP Allocation tab (writer) and the
/// Level Projection tab (reader), so projected HP / HP-regen / MP-regen reflect
/// the stats the user plans to train at each level. The CP Allocation VM pushes
/// its raw-base baseline + clamped plan rows on every recalc; the Level
/// Projection VM reads <see cref="StatsAtLevel"/> and recomputes on
/// <see cref="Changed"/>.
/// </summary>
public sealed class CpPlanState
{
    /// <summary>Raised whenever the plan or baseline changes.</summary>
    public event Action? Changed;

    /// <summary>True once the CP Allocation tab has populated the baseline.</summary>
    public bool HasData { get; private set; }

    /// <summary>The character's current level (the baseline's level).</summary>
    public int CurrentLevel { get; private set; }

    /// <summary>Raw-base stats at the current level (the plan's row 0).</summary>
    public CpPlanEntry Baseline { get; private set; } = new();

    private List<CpPlanEntry> _rows = new();

    /// <summary>Replace the plan + baseline and notify readers.</summary>
    public void Update(CpPlanEntry baseline, int currentLevel, IReadOnlyList<CpPlanEntry> rows)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(rows);
        Baseline = baseline;
        CurrentLevel = currentLevel;
        _rows = new List<CpPlanEntry>(rows);
        HasData = true;
        Changed?.Invoke();
    }

    /// <summary>
    /// The planned stats in effect at <paramref name="level"/>: the latest plan
    /// row at or below it, or the baseline when none applies (so levels at/below
    /// the current one return the baseline).
    /// </summary>
    public CpPlanEntry StatsAtLevel(int level)
    {
        CpPlanEntry best = Baseline;
        int bestLevel = CurrentLevel;
        foreach (CpPlanEntry r in _rows)
            if (r.Level <= level && r.Level > bestLevel)
            {
                best = r;
                bestLevel = r.Level;
            }
        return best;
    }
}
