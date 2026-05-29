using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.Import;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Import;

/// <summary>
/// View-model for the unified Import Conflict dialog. Holds one
/// <see cref="ImportConflictRowViewModel"/> per supplied conflict,
/// exposes the bulk-apply commands ("Skip remaining" / "Overwrite
/// remaining" / "Merge remaining") plus the per-row pick the user
/// makes, and yields an <see cref="ImportConflictResult"/> on OK.
/// </summary>
/// <remarks>
/// <para>
/// Every importer in Phase 5+ that can produce row-level conflicts
/// (MDB tables, MegaMUD spell messages, MegaMUD <c>.mp</c> paths,
/// favourites) routes its conflicts through this single dialog
/// instead of shipping a per-importer variant — matching MudProxy's
/// 4-dialog problem with one configurable component.
/// </para>
/// <para>
/// The dialog never opens with zero conflicts; the importer checks
/// the list before calling and skips straight to commit when empty.
/// </para>
/// </remarks>
public sealed partial class ImportConflictViewModel : ObservableObject, IDialogViewModel<ImportConflictResult>
{
    /// <summary>
    /// Fired with the user's resolutions on OK, or <c>null</c> on
    /// Cancel / window close. <see cref="DialogService"/> tears down
    /// the hosting window when this fires.
    /// </summary>
    public event Action<ImportConflictResult?>? CloseRequested;

    /// <summary>
    /// One row per supplied conflict, in input order. The dialog's left
    /// rail binds against this collection; selection drives the right-
    /// pane diff view via <see cref="SelectedRow"/>.
    /// </summary>
    public ObservableCollection<ImportConflictRowViewModel> Rows { get; }

    /// <summary>Window title — set by the importer so the user knows where the conflicts came from.</summary>
    public string Title { get; }

    /// <summary>Human-readable subtitle — usually "{n} conflicts found in {category}".</summary>
    public string Summary { get; }

    /// <summary>Selected row in the left rail — drives the diff pane.</summary>
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

    /// <summary>Apply <see cref="ImportAction.Skip"/> to every row that's still on the default.</summary>
    [RelayCommand]
    private void SkipAll() => ApplyToAll(ImportAction.Skip);

    /// <summary>Apply <see cref="ImportAction.Overwrite"/> to every row.</summary>
    [RelayCommand]
    private void OverwriteAll() => ApplyToAll(ImportAction.Overwrite);

    /// <summary>Apply <see cref="ImportAction.Merge"/> to every row.</summary>
    [RelayCommand]
    private void MergeAll() => ApplyToAll(ImportAction.Merge);

    private void ApplyToAll(ImportAction action)
    {
        foreach (ImportConflictRowViewModel row in Rows) row.Action = action;
    }

    /// <summary>True when every Rename row has a non-empty target value — OK is enabled only then.</summary>
    public bool CanCommit
        => Rows.All(r => r.Action != ImportAction.Rename
                      || !string.IsNullOrWhiteSpace(r.RenameTo));

    /// <summary>OK — emit the resolutions in input order.</summary>
    [RelayCommand(CanExecute = nameof(CanCommit))]
    private void Ok()
    {
        IReadOnlyList<ImportResolution> resolutions = Rows.Select(r => r.ToResolution()).ToArray();
        CloseRequested?.Invoke(new ImportConflictResult(resolutions));
    }

    /// <summary>Cancel — close with no resolutions.</summary>
    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
