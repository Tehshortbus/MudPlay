using Avalonia.Controls;

namespace FujinTerm.Views;

public partial class ManualCenterDialog : Window
{
    public ManualCenterDialog()
    {
        InitializeComponent();
        Opened += (_, _) => MapBox.Focus();
    }
}
