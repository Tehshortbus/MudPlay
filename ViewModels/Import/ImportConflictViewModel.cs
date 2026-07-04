using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.Import;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Import;

// View-model for the unified Import Conflict dialog. Holds one
// ImportConflictRowViewModel per supplied conflict, exposes the bulk-apply commands
// ("Skip remaining" / "Overwrite remaining" / "Merge remaining") plus the per-row pick
// the user makes, and yields an ImportConflictResult on OK.
//
// Every importer that can produce row-level conflicts (MDB tables, MegaMUD spell
// messages, MegaMUD .mp paths, favourites) routes its conflicts through this single
// dialog instead of shipping a per-importer variant — one configurable component
// rather than four near-identical ones.
//
// The dialog never opens with zero conflicts; the importer checks the list before
// calling and skips straight to commit when empty.
public sealed partial class ImportConflictViewModel : ObservableObject, IDialogViewModel<ImportConflictResult>
{
    // Fired with the user's resolutions on OK, or null on Cancel / window close.
    // DialogService tears down the hosting window when this fires.
    public event Action<ImportConflictResult?>? CloseRequested;

    // One row per supplied conflict, in input order. The dialog's left rail binds
    // against this collection; selection drives the right-pane diff view via SelectedRow.
    public ObservableCollection<ImportConflictRowViewModel> Rows { get; }

    // Window title — set by the importer so the user knows where the conflicts came from.
    public string Title { get; }

    // Human-readable subtitle — usually "{n} conflicts found in {category}".
    public string Summary { get; }

    // Selected row in the left rail — drives the diff pane.
    [ObservableProperty]
    private ImportConflictRowViewModel? _selectedRow;

    public ImportConflictViewModel(
        string title,
        string summary,
        IReadOnlyList<ImportConflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        if (conflicts.Count == 0)
            throw new ArgumentException("ImportConflictViewModel requires at least one conflict.", nameof(conflicts));

        Title = title ?? string.Empty;
        Summary = summary ?? string.Empty;
        Rows = new ObservableCollection<ImportConflictRowViewModel>(
            conflicts.Select(c => new ImportConflictRowViewModel(c)));
        SelectedRow = Rows[0];
    }

    // Apply Skip to every row that's still on the default.
    [RelayCommand]
    private void SkipAll() => ApplyToAll(ImportAction.Skip);

    // Apply Overwrite to every row.
    [RelayCommand]
    private void OverwriteAll() => ApplyToAll(ImportAction.Overwrite);

    // Apply Merge to every row.
    [RelayCommand]
    private void MergeAll() => ApplyToAll(ImportAction.Merge);

    private void ApplyToAll(ImportAction action)
    {
        foreach (ImportConflictRowViewModel row in Rows) row.Action = action;
    }

    // True when every Rename row has a non-empty target value — OK is enabled only then.
    public bool CanCommit
        => Rows.All(r => r.Action != ImportAction.Rename
                      || !string.IsNullOrWhiteSpace(r.RenameTo));

    // OK — emit the resolutions in input order.
    [RelayCommand(CanExecute = nameof(CanCommit))]
    private void Ok()
    {
        IReadOnlyList<ImportResolution> resolutions = Rows.Select(r => r.ToResolution()).ToArray();
        CloseRequested?.Invoke(new ImportConflictResult(resolutions));
    }

    // Cancel — close with no resolutions.
    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
