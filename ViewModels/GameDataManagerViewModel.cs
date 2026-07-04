using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Services;

namespace FujinTerm.ViewModels;

// Game Data → "Manage Sets…". Two immediate-action sections over
// GameDataSetManager: copy / move a set's loop library to another set, and
// delete a set (tables + loops). No staged Save — each button performs its
// op and reports via Status.
//
// All-windows-modeless rule: the destructive delete can't pop a confirm
// dialog, so it arms in place — the first click flips the button to "Click
// again to confirm"; the second click commits. Picking a different set to
// delete disarms it.
public sealed partial class GameDataManagerViewModel
    : ObservableObject, IDialogViewModel<bool>
{
    public event Action<bool>? CloseRequested;

    private readonly GameDataSetManager _manager;
    private readonly GameDataCache _cache;

    // Imported sets on disk, snapshotted on open and after each delete.
    public ObservableCollection<string> Sets { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopyOrMove))]
    private string? _sourceSet;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopyOrMove))]
    private string? _destSet;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private string? _deleteSet;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteButtonText))]
    private bool _confirmingDelete;

    // Enabled when both pickers hold a (different) set.
    public bool CanCopyOrMove =>
        !string.IsNullOrWhiteSpace(SourceSet)
        && !string.IsNullOrWhiteSpace(DestSet)
        && !string.Equals(SourceSet, DestSet, StringComparison.OrdinalIgnoreCase);

    public bool CanDelete => !string.IsNullOrWhiteSpace(DeleteSet);

    public string DeleteButtonText => ConfirmingDelete ? "Click again to confirm" : "Delete set";

    public GameDataManagerViewModel(GameDataSetManager manager, GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(cache);
        _manager = manager;
        _cache = cache;
        RefreshSets();
    }

    private void RefreshSets()
    {
        Sets.Clear();
        foreach (string s in _cache.AvailableSets) Sets.Add(s);
    }

    // Re-picking the delete target disarms a pending confirm so a stale
    // "click again" never deletes the wrong set.
    partial void OnDeleteSetChanged(string? value) => ConfirmingDelete = false;

    [RelayCommand]
    private void CopyLoops() => Status = _manager.CopyLoops(SourceSet ?? string.Empty, DestSet ?? string.Empty).Message;

    [RelayCommand]
    private void MoveLoops() => Status = _manager.MoveLoops(SourceSet ?? string.Empty, DestSet ?? string.Empty).Message;

    [RelayCommand]
    private void DeleteSelectedSet()
    {
        if (string.IsNullOrWhiteSpace(DeleteSet)) return;

        if (!ConfirmingDelete)
        {
            ConfirmingDelete = true;
            Status = $"Delete '{DeleteSet}' and its loops permanently? Click the button again to confirm.";
            return;
        }

        string target = DeleteSet!;
        GameDataSetManager.OpResult result = _manager.DeleteSet(target);
        ConfirmingDelete = false;
        Status = result.Message;

        if (!result.Ok) return;
        RefreshSets();
        if (string.Equals(SourceSet, target, StringComparison.OrdinalIgnoreCase)) SourceSet = null;
        if (string.Equals(DestSet, target, StringComparison.OrdinalIgnoreCase)) DestSet = null;
        DeleteSet = null;
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(false);
}
