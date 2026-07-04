using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Services;

namespace FujinTerm.ViewModels;

// Generic "Are you sure?" dialog VM, used by every confirm-prompt path
// surfaced through ConfirmService. The caller supplies the dialog title and
// body text; the dialog returns true on Yes, false on No (or window-X / Esc
// — modeless cancel).
public sealed partial class ConfirmDialogViewModel : ObservableObject, IDialogViewModel<bool>
{
    public event Action<bool>? CloseRequested;

    public string Title  { get; }
    public string Body   { get; }
    public string YesLabel { get; }
    public string NoLabel  { get; }

    public ConfirmDialogViewModel(string title, string body, string yesLabel = "Yes", string noLabel = "No")
    {
        Title    = title;
        Body     = body;
        YesLabel = yesLabel;
        NoLabel  = noLabel;
    }

    [RelayCommand] private void Yes() => CloseRequested?.Invoke(true);
    [RelayCommand] private void No()  => CloseRequested?.Invoke(false);
}
