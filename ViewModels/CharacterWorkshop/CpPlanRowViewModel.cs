using System;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game.Calculators;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// One editable row in the CP Allocation grid — a planned level's target stat
/// values (user-editable) plus the computed CP accounting columns (written back
/// by the section's recalc). Editing any stat invokes the supplied callback so
/// the whole grid re-totals.
/// </summary>
public sealed partial class CpPlanRowViewModel : ObservableObject
{
    private readonly Action<CpStat> _onEdited;

    /// <summary>The level this row trains to (read-only in the grid).</summary>
    public int Level { get; }

    // ----- editable target stats -----------------------------------------
    [ObservableProperty] private int _strength;
    [ObservableProperty] private int _intellect;
    [ObservableProperty] private int _willpower;
    [ObservableProperty] private int _agility;
    [ObservableProperty] private int _health;
    [ObservableProperty] private int _charm;

    // ----- computed CP columns (set by CpAllocationSectionViewModel) ------
    /// <summary>Cumulative CP available by this level (starting unspent + all gained through it).</summary>
    [ObservableProperty] private int _cpEarnedTotal;
    /// <summary>CP remaining after the plan's spending through this level (floored at 0).</summary>
    [ObservableProperty] private int _cpLeft;

    public CpPlanRowViewModel(int level, Action<CpStat> onEdited)
    {
        ArgumentNullException.ThrowIfNull(onEdited);
        Level = level;
        _onEdited = onEdited;
    }

    partial void OnStrengthChanged(int value) => _onEdited(CpStat.Strength);
    partial void OnIntellectChanged(int value) => _onEdited(CpStat.Intellect);
    partial void OnWillpowerChanged(int value) => _onEdited(CpStat.Willpower);
    partial void OnAgilityChanged(int value) => _onEdited(CpStat.Agility);
    partial void OnHealthChanged(int value) => _onEdited(CpStat.Health);
    partial void OnCharmChanged(int value) => _onEdited(CpStat.Charm);

    /// <summary>Snapshot this row's target stats as a persistable plan entry.</summary>
    public CpPlanEntry ToEntry() => new(Level, Strength, Intellect, Willpower, Agility, Health, Charm);
}
