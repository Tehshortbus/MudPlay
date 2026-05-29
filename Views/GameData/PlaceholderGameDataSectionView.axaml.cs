using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FujinTerm.Views.GameData;

public partial class PlaceholderGameDataSectionView : UserControl
{
    public PlaceholderGameDataSectionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
