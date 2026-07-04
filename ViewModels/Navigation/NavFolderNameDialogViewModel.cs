using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Navigation;

// Tiny single-text-box prompt for naming a navigation folder — shared by
// the "New folder" and "Rename folder" actions on both the Manage dialog
// (loops + lairs) and the rail (gotos). Returns the typed /-separated folder
// path on Save, null on Cancel. The caller normalises + applies the result
// via the relevant store / NavFolderManager.
public sealed partial class NavFolderNameDialogViewModel : ObservableObject, IDialogViewModel<string?>
{
    public event Action<string?>? CloseRequested;

    public NavFolderNameDialogViewModel(string title, string prompt, string initial = "")
    {
        Title = title;
        Prompt = prompt;
        _name = initial ?? string.Empty;
    }

    // Window title — e.g. "New folder" / "Rename folder".
    public string Title { get; }

    // One-line instruction shown above the input box.
    public string Prompt { get; }

    // The folder path being entered. /-separated; empty is rejected by the caller.
    [ObservableProperty] private string _name;

    [RelayCommand]
    private void Save() => CloseRequested?.Invoke(Name?.Trim() ?? string.Empty);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
