using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Navigation;

/// <summary>
/// Modeless editor for an existing <see cref="Loop"/> — rename,
/// edit notes, reorder / insert / remove / edit
/// <see cref="CommandLoopStep"/>s. <see cref="MoveLoopStep"/>s are
/// surfaced read-only because re-ordering moves would break the
/// closed-cycle invariant; users fix routing in the builder pane
/// instead.
/// </summary>
/// <remarks>
/// Save mutates the loop in place + persists via
/// <see cref="LoopManager.Save"/>, which fires LoopsChanged so the
/// Navigation pane refreshes. Cancel / X discards every edit; the
/// dialog operates on its own row view-models until Save runs.
/// <see cref="Loop.UserWaypoints"/> is preserved as-is — manual step
/// edits can drift the rendered cycle from the saved waypoints, but
/// the waypoints stay authoritative for re-expansion (avoid-list
/// change) and approach-pick. A future PR can add a validation pass
/// for this drift.
/// </remarks>
public sealed partial class LoopEditorDialogViewModel : ObservableObject, IDialogViewModel<Loop?>
{
    public event Action<Loop?>? CloseRequested;

    private readonly Loop _original;
    private readonly LoopManager _loops;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;

    public ObservableCollection<LoopStepRowViewModel> Steps { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSelectedCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedRowIsCommand))]
    private LoopStepRowViewModel? _selectedRow;

    [ObservableProperty] private string _selectedCommand = string.Empty;
    [ObservableProperty] private int _selectedDelayMs = DefaultCommandDelayMs;

    private const int DefaultCommandDelayMs = 1200;

    /// <summary>True when a row is selected AND it's a command step.</summary>
    public bool CanEditSelectedCommand => SelectedRow?.IsCommand ?? false;

    /// <summary>Mirror of <see cref="CanEditSelectedCommand"/> for the AXAML.</summary>
    public bool SelectedRowIsCommand => CanEditSelectedCommand;

    public LoopEditorDialogViewModel(Loop loop, LoopManager loops)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(loops);
        _original = loop;
        _loops = loops;
        _name = loop.Name;
        _notes = loop.Notes ?? string.Empty;
        foreach (LoopStep step in loop.Steps)
            Steps.Add(new LoopStepRowViewModel(step));
        RenumberRows();
    }

    // ----- row commands ----------------------------------------------

    [RelayCommand]
    private void MoveUp(LoopStepRowViewModel? row)
    {
        if (row is null || !row.IsCommand) return;
        int idx = Steps.IndexOf(row);
        if (idx <= 0) return;
        Steps.Move(idx, idx - 1);
        RenumberRows();
    }

    [RelayCommand]
    private void MoveDown(LoopStepRowViewModel? row)
    {
        if (row is null || !row.IsCommand) return;
        int idx = Steps.IndexOf(row);
        if (idx < 0 || idx >= Steps.Count - 1) return;
        Steps.Move(idx, idx + 1);
        RenumberRows();
    }

    [RelayCommand]
    private void Remove(LoopStepRowViewModel? row)
    {
        if (row is null || !row.IsCommand) return;
        Steps.Remove(row);
        if (SelectedRow == row) SelectedRow = null;
        RenumberRows();
    }

    [RelayCommand]
    private void InsertCommandAbove()
    {
        int idx = SelectedRow is null ? 0 : Math.Max(0, Steps.IndexOf(SelectedRow));
        var newRow = new LoopStepRowViewModel(new CommandLoopStep(string.Empty, DefaultCommandDelayMs));
        Steps.Insert(idx, newRow);
        SelectedRow = newRow;
        RenumberRows();
    }

    [RelayCommand]
    private void InsertCommandBelow()
    {
        int idx = SelectedRow is null ? Steps.Count : Math.Min(Steps.Count, Steps.IndexOf(SelectedRow) + 1);
        var newRow = new LoopStepRowViewModel(new CommandLoopStep(string.Empty, DefaultCommandDelayMs));
        Steps.Insert(idx, newRow);
        SelectedRow = newRow;
        RenumberRows();
    }

    /// <summary>
    /// Replace the selected row's underlying command step with the
    /// editor fields' current values. No-op when the selection isn't
    /// a command step.
    /// </summary>
    [RelayCommand]
    private void ApplyCommandEdit()
    {
        if (SelectedRow is null || !SelectedRow.IsCommand) return;
        int delay = Math.Max(0, SelectedDelayMs);
        SelectedRow.Replace(new CommandLoopStep(SelectedCommand ?? string.Empty, delay));
    }

    // ----- dialog commit ---------------------------------------------

    [RelayCommand]
    private void Save()
    {
        var newSteps = new List<LoopStep>(Steps.Count);
        foreach (LoopStepRowViewModel row in Steps) newSteps.Add(row.UnderlyingStep);

        string newName = (Name ?? string.Empty).Trim();
        if (newName.Length == 0) return;        // refuse blank-name save

        // Rename: when the name changed, delete the old file before
        // saving under the new one. LoopManager.Save keys by Loop.Name.
        bool renamed = !string.Equals(newName, _original.Name, StringComparison.OrdinalIgnoreCase);
        string oldName = _original.Name;

        _original.Name = newName;
        _original.Notes = Notes ?? string.Empty;
        _original.Steps = newSteps;

        if (renamed) _loops.Delete(oldName);
        _loops.Save(_original);

        CloseRequested?.Invoke(_original);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    // ----- internals -------------------------------------------------

    partial void OnSelectedRowChanged(LoopStepRowViewModel? value)
    {
        if (value?.UnderlyingStep is CommandLoopStep cmd)
        {
            SelectedCommand = cmd.Command;
            SelectedDelayMs = cmd.DelayMs;
        }
        else
        {
            SelectedCommand = string.Empty;
            SelectedDelayMs = DefaultCommandDelayMs;
        }
    }

    private void RenumberRows()
    {
        for (int i = 0; i < Steps.Count; i++)
            Steps[i].Index = i + 1;
    }
}

/// <summary>
/// Per-row view model for the editor's Steps ListBox. Wraps one
/// <see cref="LoopStep"/> and exposes the display label + type
/// classification the AXAML colours on.
/// </summary>
public sealed partial class LoopStepRowViewModel : ObservableObject
{
    [ObservableProperty] private int _index;
    [ObservableProperty] private string _displayLabel = string.Empty;
    public LoopStep UnderlyingStep { get; private set; }

    public bool IsCommand => UnderlyingStep is CommandLoopStep;
    public bool IsMove    => UnderlyingStep is MoveLoopStep;

    public LoopStepRowViewModel(LoopStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        UnderlyingStep = step;
        _displayLabel = step.Display;
    }

    /// <summary>
    /// Replace the underlying step (used by ApplyCommandEdit when the
    /// user adjusts the selected command's text or delay). Refreshes
    /// the display label so the ListBox row updates in place.
    /// </summary>
    public void Replace(LoopStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        UnderlyingStep = step;
        DisplayLabel = step.Display;
        OnPropertyChanged(nameof(IsCommand));
        OnPropertyChanged(nameof(IsMove));
    }
}
