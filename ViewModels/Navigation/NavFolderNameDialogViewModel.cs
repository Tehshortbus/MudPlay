using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Navigation;

/// <summary>
/// Tiny single-text-box prompt for naming a navigation folder — shared
/// by the "New folder" and "Rename folder" actions on both the Manage
/// dialog (loops + lairs) and the rail (gotos). Returns the typed
/// <c>/</c>-separated folder path on Save, <c>null</c> on Cancel. The
/// caller normalises + applies the result via the relevant store /
/// <see cref="Game.Map.NavFolderManager"/>.
/// </summary>
public sealed partial class NavFolderNameDialogViewModel : ObservableObject, IDialogViewModel<string?>
{
    public event Action<string?>? CloseRequested;

    public NavFolderNameDialogViewModel(string title, string prompt, string initial = "")
    {
        Title = title;
        Prompt = prompt;
        _name = initial ?? string.Empty;
    }

    /// <summary>Window title — e.g. "New folder" / "Rename folder".</summary>
    public string Title { get; }

    /// <summary>One-line instruction shown above the input box.</summary>
    public string Prompt { get; }

    /// <summary>The folder path being entered. <c>/</c>-separated; empty is rejected by the caller.</summary>
    [ObservableProperty] private string _name;

    [RelayCommand]
    private void Save() => CloseRequested?.Invoke(Name?.Trim() ?? string.Empty);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
