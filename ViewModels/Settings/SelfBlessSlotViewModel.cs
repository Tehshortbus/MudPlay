using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// One self-bless row in the Settings → Spells tab: a 4-letter spell
/// short-code picker the <see cref="Game.Spells.CastingDirector"/> recasts
/// when the buff isn't active. Row order is cast priority (the bless walk
/// runs top-to-bottom). The active realm sets how many rows show — Stock 10,
/// ParaMud 15 — but each row only carries its spell code; the sparse map
/// keyed on <see cref="Index"/> is what persists.
/// </summary>
public sealed partial class SelfBlessSlotViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private bool _suppress;

    /// <summary>1-based row number, surfaced as the "Bless N" label and used
    /// as the persisted sparse-map key.</summary>
    public int Index { get; }

    /// <summary>Display label, e.g. <c>Bless 1</c>.</summary>
    public string Label => $"Bless {Index}";

    /// <summary>Committed 4-letter spell short-code, or blank for an unused
    /// slot. Bound to the row's AutoCompleteBox.</summary>
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
