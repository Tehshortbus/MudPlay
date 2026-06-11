using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// One editable party-bless row in the Settings → Party tab: a spell
/// short-code picker plus a checkbox per loaded class. A party member
/// receives the buff only when their class checkbox is ticked. Row order
/// is priority order; the bless engine walks slots top-to-bottom.
/// </summary>
public sealed partial class PartyBlessSlotViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private bool _suppress;

    /// <summary>1-based row number, surfaced as the "Bless N" label.</summary>
    public int Index { get; }

    /// <summary>Display label, e.g. <c>Bless 1</c>.</summary>
    public string Label => $"Bless {Index}";

    /// <summary>Committed 4-letter spell short-code, or blank for an
    /// unused slot. Bound to the row's AutoCompleteBox.</summary>
    [ObservableProperty] private string? _spell;

    /// <summary>One toggle per loaded class — ticking it adds that class
    /// number to the slot's target set.</summary>
    public IReadOnlyList<PartyBlessClassToggle> Classes { get; }

    public PartyBlessSlotViewModel(
        int index,
        IReadOnlyList<(int Number, string Name)> classes,
        PartyBlessSlot dto,
        Action onChanged)
    {
        Index = index;
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));

        _suppress = true;
        Spell = dto.Spell;
        _suppress = false;

        Classes = classes
            .Select(c => new PartyBlessClassToggle(
                c.Number, c.Name,
                isChecked: dto.ClassNumbers.Contains(c.Number),
                onChanged))
            .ToList();
    }

    /// <summary>Snapshot this row back into a persistable DTO.</summary>
    public PartyBlessSlot ToDto() => new()
    {
        Spell = string.IsNullOrWhiteSpace(Spell) ? null : Spell.Trim(),
        ClassNumbers = Classes.Where(c => c.IsChecked).Select(c => c.Number).ToList(),
    };

    partial void OnSpellChanged(string? value)
    {
        if (_suppress) return;
        _onChanged();
    }
}

/// <summary>
/// A single class checkbox inside a <see cref="PartyBlessSlotViewModel"/>.
/// Carries the gamedata <c>Classes.Number</c> + display name and reports
/// edits back to the owning section for dirty tracking.
/// </summary>
public sealed partial class PartyBlessClassToggle : ObservableObject
{
    private readonly Action _onChanged;
    private readonly bool _ready;

    /// <summary>Gamedata <c>Classes.Number</c> this toggle represents.</summary>
    public int Number { get; }

    /// <summary>Class display name shown next to the checkbox.</summary>
    public string Name { get; }

    /// <summary>Whether members of this class receive the slot's buff.</summary>
    [ObservableProperty] private bool _isChecked;

    public PartyBlessClassToggle(int number, string name, bool isChecked, Action onChanged)
    {
        Number = number;
        Name = name;
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        IsChecked = isChecked;
        _ready = true;
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (!_ready) return;
        _onChanged();
    }
}
