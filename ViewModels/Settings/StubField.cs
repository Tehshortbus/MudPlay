using System.Collections.Generic;

namespace FujinTerm.ViewModels.Settings;

// What control kind to render for a StubField in the shared StubSectionView.
// Each kind maps to a disabled Avalonia primitive with the field's label +
// tooltip; the rendering is purely cosmetic until the field is wired.
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

// One labelled stub control in a stub settings tab. The tooltip describes what
// the field will do, shown to a user hovering on the disabled control.
public sealed record StubField(
    string Label,
    StubFieldKind Kind,
    string Tooltip,
    string? Suffix = null,
    double Min = 0,
    double Max = 100)
{
    // True when the left-column label should render. Note rows hide it because
    // their content spans the row; Check rows hide it because the
    // CheckBox.Content carries the label to the right of the box (so the user
    // sees [box] Label instead of Label  [box]).
    public bool HasLabel => Kind != StubFieldKind.Note && Kind != StubFieldKind.Check;
}

// A visually-grouped block of stub fields with an optional header. Mirrors the
// section headings used by GeneralSectionView so the stubs share the same shape
// as the real tabs.
public sealed record StubGroup(string? Header, IReadOnlyList<StubField> Fields);
