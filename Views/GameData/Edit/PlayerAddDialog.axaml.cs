using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FujinTerm.Views.GameData.Edit;

public partial class PlayerAddDialog : Window
{
    public PlayerAddDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
