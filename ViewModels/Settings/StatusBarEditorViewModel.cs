using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.ViewModels.StatusBar;

namespace MudPlay.ViewModels.Settings;

// The status-bar layout editor on Settings → BBS + Display: up to four rows, each
// with a left, centre and right zone of items, and a preview that redraws on every
// edit so the bar can be judged before it is saved.
public sealed partial class StatusBarEditorViewModel : ObservableObject, IDisposable
{
    private bool _loading;

    public ObservableCollection<StatusBarRowEditor> Rows { get; } = new();

    // The bar as it would look with the edits so far. Null when nothing can draw
    // it (no main window).
    public StatusBarViewModel? Preview { get; }
    public bool HasPreview => Preview is not null;

    // Raised on any edit, so the section can mark itself unsaved.
    public event Action? Changed;

    public StatusBarEditorViewModel(Func<StatusBarViewModel?> createPreview)
    {
        ArgumentNullException.ThrowIfNull(createPreview);
        Preview = createPreview();
    }

    public void Load(StatusBarSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _loading = true;
        Rows.Clear();
        foreach (StatusBarRow row in settings.Rows.Take(StatusBarSettings.MaxRows))
            Rows.Add(new StatusBarRowEditor(this, row));
        if (Rows.Count == 0) Rows.Add(new StatusBarRowEditor(this, StatusBarRow.Default()));
        _loading = false;
        Renumber();
        Preview?.Apply(ToSettings());
    }

    public StatusBarSettings ToSettings() => new() { Rows = Rows.Select(r => r.ToRow()).ToList() };

    [RelayCommand(CanExecute = nameof(CanAddRow))]
    private void AddRow()
    {
        Rows.Add(new StatusBarRowEditor(this, new StatusBarRow()));
        Renumber();
        NotifyChanged();
    }

    private bool CanAddRow() => Rows.Count < StatusBarSettings.MaxRows;

    [RelayCommand]
    private void Reset()
    {
        Load(new StatusBarSettings());
        Changed?.Invoke();
    }

    public void RemoveRow(StatusBarRowEditor row)
    {
        if (Rows.Count <= 1 || !Rows.Remove(row)) return;
        Renumber();
        NotifyChanged();
    }

    public void NotifyChanged()
    {
        if (_loading) return;
        Preview?.Apply(ToSettings());
        Changed?.Invoke();
    }

    private void Renumber()
    {
        for (int i = 0; i < Rows.Count; i++)
        {
            Rows[i].Title = $"Status bar {i + 1}";
            Rows[i].CanRemove = Rows.Count > 1;
        }
        AddRowCommand.NotifyCanExecuteChanged();
    }

    public void Dispose() => Preview?.Dispose();
}
