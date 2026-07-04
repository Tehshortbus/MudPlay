using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.ViewModels.Settings;

// One self-bless row in the Settings → Spells tab: a 4-letter spell short-code
// picker the CastingDirector recasts when the buff isn't active. Row order is
// cast priority (the bless walk runs top-to-bottom). The active realm sets how
// many rows show — Stock 10, ParaMud 15 — but each row only carries its spell
// code; the sparse map keyed on Index is what persists.
public sealed partial class SelfBlessSlotViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private bool _suppress;

    // 1-based row number, surfaced as the "Bless N" label and used as the
    // persisted sparse-map key.
    public int Index { get; }

    // Display label, e.g. Bless 1.
    public string Label => $"Bless {Index}";

    // Committed 4-letter spell short-code, or blank for an unused slot. Bound
    // to the row's AutoCompleteBox.
    [ObservableProperty] private string? _spell;

    public SelfBlessSlotViewModel(int index, string? spell, Action onChanged)
    {
        Index = index;
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));

        _suppress = true;
        Spell = spell;
        _suppress = false;
    }

    partial void OnSpellChanged(string? value)
    {
        if (_suppress) return;
        _onChanged();
    }
}
