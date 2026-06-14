using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FujinTerm.Views;

public partial class UnknownEntityFixDialog : Window
{
    public UnknownEntityFixDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
