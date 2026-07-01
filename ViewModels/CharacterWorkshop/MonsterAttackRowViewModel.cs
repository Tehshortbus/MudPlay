using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// One attack row in the Calculators tab's incoming-hit table. The accuracy is
/// user-editable so the player can dial it up or down and watch the resulting
/// hit chance change against their (also editable) AC + dodge;
/// <see cref="HitPercent"/> is recomputed by the parent section whenever this
/// row's accuracy or the shared AC/dodge inputs change. Rows can be added and
/// removed to model any number of attacks, so <see cref="Label"/> is mutable
/// (the parent renumbers on add/remove) and each row can remove itself.
/// </summary>
public sealed partial class MonsterAttackRowViewModel : ObservableObject
{
    // Notify the parent so it can recompute this row's hit% against the shared
    // (editable) player AC + dodge — the formula needs both sides.
    private readonly Action<MonsterAttackRowViewModel> _onAccuracyChanged;
    private readonly Action<MonsterAttackRowViewModel> _onRemove;

    /// <summary>Attack label, e.g. "Attack 1" — renumbered by the parent on add/remove.</summary>
    [ObservableProperty] private string _label;

    /// <summary>Raw min-max damage of this attack (before the player's DR), for reference.</summary>
    public string Damage { get; }

    /// <summary>Attack accuracy — editable so the user can explore the hit curve.</summary>
    [ObservableProperty] private int _accuracy;

    /// <summary>Resulting hit chance vs the player's current AC + dodge ("NN%").</summary>
    [ObservableProperty] private string _hitPercent = "—";

    public MonsterAttackRowViewModel(string label, string damage, int accuracy,
                                     Action<MonsterAttackRowViewModel> onAccuracyChanged,
                                     Action<MonsterAttackRowViewModel> onRemove)
    {
        _label = label;
        Damage = damage;
        _accuracy = accuracy;
        _onAccuracyChanged = onAccuracyChanged;
        _onRemove = onRemove;
    }

    partial void OnAccuracyChanged(int value) => _onAccuracyChanged(this);

    [RelayCommand]
    private void Remove() => _onRemove(this);
}
