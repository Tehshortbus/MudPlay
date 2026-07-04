using System;
using System.Collections.Generic;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// Live CP-plan state shared between the CP Allocation tab (writer) and the Level
// Projection tab (reader), so projected HP / HP-regen / MP-regen reflect the
// stats the user plans to train at each level. The CP Allocation VM pushes its
// raw-base baseline + clamped plan rows on every recalc; the Level Projection VM
// reads StatsAtLevel and recomputes on Changed.
public sealed class CpPlanState
{
    // Raised whenever the plan or baseline changes.
    public event Action? Changed;

    // True once the CP Allocation tab has populated the baseline.
    public bool HasData { get; private set; }

    // The character's current level (the baseline's level).
    public int CurrentLevel { get; private set; }

    // Raw-base stats at the current level (the plan's row 0).
    public CpPlanEntry Baseline { get; private set; } = new();

    private List<CpPlanEntry> _rows = new();

    // Replace the plan + baseline and notify readers.
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

    // The planned stats in effect at the given level: the latest plan row at or
    // below it, or the baseline when none applies (so levels at/below the current
    // one return the baseline).
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
