using Avalonia.Controls;

namespace FujinTerm.Views;

// Modeless "Bug report" prompt. Collects the user's problem description;
// the client-state capture happens in the caller at click time. Lands focus
// on the description box so the user can type immediately.
public partial class BugReportDialog : Window
{
    public BugReportDialog()
    {
        InitializeComponent();
        Opened += (_, _) => DescriptionBox.Focus();
    }
}
