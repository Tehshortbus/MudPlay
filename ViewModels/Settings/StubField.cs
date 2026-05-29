using System.Collections.Generic;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// What control kind to render for a <see cref="StubField"/> in the
/// shared <c>StubSectionView</c>. Each kind maps to a disabled
/// Avalonia primitive with the field's label + tooltip; the rendering
/// is purely cosmetic until the owning phase wires the field.
/// </summary>
public enum StubFieldKind
{
    Check,        // CheckBox
    Numeric,      // NumericUpDown (placeholder sample value, disabled)
    Combo,        // ComboBox (single placeholder entry, disabled)
    AutoComplete, // AutoCompleteBox — typeahead-filtered list (disabled stub)
    Text,         // TextBox (placeholder sample, disabled)
    Slider,       // Slider (disabled)
    Button,       // Button (disabled)
    Note,         // Indented muted-text note (no control)
}

/// <summary>
/// One labelled stub control in a stub settings tab. The tooltip
/// describes the owning phase / PR so users hovering on a stub get the
/// "wired in Phase X" trail without leaving the tab.
/// </summary>
public sealed record StubField(
    string Label,
    StubFieldKind Kind,
    string Tooltip,
    string? Suffix = null,
    double Min = 0,
    double Max = 100)
{
    /// <summary>
    /// True when the left-column label should render. Note rows hide
    /// it because their content spans the row; Check rows hide it
    /// because the CheckBox.Content carries the label to the right of
    /// the box (so the user sees `[box] Label` instead of
    /// `Label  [box]`).
    /// </summary>
    public bool HasLabel => Kind != StubFieldKind.Note && Kind != StubFieldKind.Check;
}

/// <summary>
/// A visually-grouped block of stub fields with an optional header.
/// Mirrors the section headings used by GeneralSectionView so the stubs
/// share the same shape as the real tabs.
/// </summary>
public sealed record StubGroup(string? Header, IReadOnlyList<StubField> Fields);
