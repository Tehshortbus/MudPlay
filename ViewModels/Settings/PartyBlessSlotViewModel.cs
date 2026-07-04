using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.Settings;

// One editable party-bless row in the Settings → Party tab: a spell
// short-code picker plus a checkbox per loaded class. A party member receives
// the buff only when their class checkbox is ticked. Row order is priority
// order; the bless engine walks slots top-to-bottom.
public sealed partial class PartyBlessSlotViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private bool _suppress;

    // Tracks whether the slot's spell was blank before the latest edit, so a
    // blank→filled transition can auto-tick every class.
    private bool _spellWasEmpty;

    // 1-based row number, surfaced as the "Bless N" label.
    public int Index { get; }

    // Display label, e.g. Bless 1.
    public string Label => $"Bless {Index}";

    // Committed 4-letter spell short-code, or blank for an unused slot. Bound
    // to the row's AutoCompleteBox.
    [ObservableProperty] private string? _spell;

    // One toggle per loaded class — ticking it adds that class number to the
    // slot's target set.
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
        _spellWasEmpty = string.IsNullOrWhiteSpace(Spell);

        Classes = classes
            .Select(c => new PartyBlessClassToggle(
                c.Number, c.Name,
                isChecked: dto.ClassNumbers.Contains(c.Number),
                onChanged))
            .ToList();
    }

    // Snapshot this row back into a persistable DTO.
    public PartyBlessSlot ToDto() => new()
    {
        Spell = string.IsNullOrWhiteSpace(Spell) ? null : Spell.Trim(),
        ClassNumbers = Classes.Where(c => c.IsChecked).Select(c => c.Number).ToList(),
    };

    partial void OnSpellChanged(string? value)
    {
        bool isEmpty = string.IsNullOrWhiteSpace(value);

        // First time a spell lands in a previously-empty slot, tick every
        // class by default — the common intent is "bless the whole party",
        // and unticking a few is far less work than ticking them all.
        if (!_suppress && _spellWasEmpty && !isEmpty)
            foreach (PartyBlessClassToggle toggle in Classes)
                toggle.IsChecked = true;

        _spellWasEmpty = isEmpty;

        if (_suppress) return;
        _onChanged();
    }
}

// A single class checkbox inside a PartyBlessSlotViewModel. Carries the
// gamedata Classes.Number + display name and reports edits back to the owning
// section for dirty tracking.
public sealed partial class PartyBlessClassToggle : ObservableObject
{
    private readonly Action _onChanged;
    private readonly bool _ready;

    // Gamedata Classes.Number this toggle represents.
    public int Number { get; }

    // Class display name shown next to the checkbox.
    public string Name { get; }

    // Whether members of this class receive the slot's buff.
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
