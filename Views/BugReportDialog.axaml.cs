using Avalonia.Controls;

namespace MudPlay.Views;

// Modeless "Bug report" prompt. Collects the user's problem description;
// the client-state capture happens in the caller at click time. Lands focus
// on the description box so the user can type immediately. Closed by its X, a
// GitHub sign-in or upload under way is stopped.
public partial class BugReportDialog : Window, MudPlay.Services.IShowsNoGameData
{
    public BugReportDialog()
    {
        InitializeComponent();
        Opened += (_, _) => DescriptionBox.Focus();
        Closed += (_, _) => (DataContext as MudPlay.ViewModels.BugReportDialogViewModel)?.Abandon();
    }
}
