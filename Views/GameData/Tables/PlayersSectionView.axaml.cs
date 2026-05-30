using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FujinTerm.Views.GameData.Tables;

public partial class PlayersSectionView : UserControl
{
    public PlayersSectionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
