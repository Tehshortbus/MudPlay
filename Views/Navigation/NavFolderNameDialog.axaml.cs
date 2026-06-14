using Avalonia.Controls;

namespace FujinTerm.Views.Navigation;

public sealed partial class NavFolderNameDialog : Window
{
    public NavFolderNameDialog()
    {
        InitializeComponent();
        // Focus the name box on open so the user can type immediately.
        Opened += (_, _) => NameBox.Focus();
    }
}
