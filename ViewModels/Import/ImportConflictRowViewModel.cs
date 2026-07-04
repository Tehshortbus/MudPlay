using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Import;

namespace FujinTerm.ViewModels.Import;

// One row in the import-conflict dialog's left rail. Wraps a single ImportConflict
// with the per-row mutable state (chosen action, rename text) and a pre-computed list
// of field diffs the right-pane detail view binds to.
public sealed partial class ImportConflictRowViewModel : ObservableObject
{
    // The original conflict; passed back unchanged in the resolution.
    public ImportConflict Conflict { get; }

    public string Category   => Conflict.Category;
    public string Identifier => Conflict.Identifier;

    // Pre-computed row-level diff — every field that appears in either the existing or
    // the incoming record gets a FieldDiff, sorted by field name. Bindings render
    // changed fields differently from unchanged ones.
    public IReadOnlyList<FieldDiff> FieldDiffs { get; }

    // Selected resolution for this row. Defaults to Skip — the safe non-destructive choice.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRenameSelected))]
    private ImportAction _action = ImportAction.Skip;

    // New identifier for the Rename action. Empty until the user picks Rename and starts
    // typing; the dialog's OK gate requires non-empty values when Rename is the chosen action.
    [ObservableProperty]
    private string _renameTo = string.Empty;

    // Convenience for view bindings — shows the rename textbox only when the action is Rename.
    public bool IsRenameSelected => Action == ImportAction.Rename;

    public ImportConflictRowViewModel(ImportConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        Conflict = conflict;
        FieldDiffs = BuildDiffs(conflict.Existing, conflict.Incoming);
    }

    // Project this row's mutable state into an ImportResolution for the result payload.
    public ImportResolution ToResolution()
    {
        string? rename = Action == ImportAction.Rename
            ? (string.IsNullOrWhiteSpace(RenameTo) ? null : RenameTo.Trim())
            : null;
        return new ImportResolution(Conflict, Action, rename);
    }

    private static IReadOnlyList<FieldDiff> BuildDiffs(
        IReadOnlyDictionary<string, string?> existing,
        IReadOnlyDictionary<string, string?> incoming)
    {
        HashSet<string> allFields = new(StringComparer.OrdinalIgnoreCase);
        foreach (string k in existing.Keys)  allFields.Add(k);
        foreach (string k in incoming.Keys) allFields.Add(k);

        return allFields
            .OrderBy(static f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f =>
            {
                existing.TryGetValue(f, out string? oldVal);
                incoming.TryGetValue(f, out string? newVal);
                bool changed = !string.Equals(oldVal ?? string.Empty, newVal ?? string.Empty, StringComparison.Ordinal);
                return new FieldDiff(f, oldVal, newVal, changed);
            })
            .ToArray();
    }
}

// One field's existing-vs-incoming pair for the dialog's detail view. Pre-computed by
// ImportConflictRowViewModel so the XAML can render each row with a single template
// binding. FieldName is the column / property name; ExistingValue is the value in the
// store and IncomingValue the value in the import (a null either side renders as a
// muted "(empty)"); Changed is true when the two differ — the view template highlights
// changed rows.
public sealed record FieldDiff(
    string FieldName,
    string? ExistingValue,
    string? IncomingValue,
    bool Changed);
