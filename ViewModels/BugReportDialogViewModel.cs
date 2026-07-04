using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Services;

namespace FujinTerm.ViewModels;

/// <summary>
/// Prompts the user to describe the problem for a bug report. The client
/// state is captured at click time (before this dialog opens); this dialog
/// only collects the "what went wrong" prose. Commit returns the trimmed
/// description; Cancel / X returns <c>null</c> so the caller writes nothing.
/// </summary>
public sealed partial class BugReportDialogViewModel : ObservableObject, IDialogViewModel<string>
{
    public event Action<string?>? CloseRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private string _description = string.Empty;

    /// <summary>Submit is enabled only once the user has typed something.</summary>
    public bool CanSubmit => !string.IsNullOrWhiteSpace(Description);

    [RelayCommand]
    private void Submit()
    {
        if (!CanSubmit) return;
        CloseRequested?.Invoke(Description.Trim());
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
