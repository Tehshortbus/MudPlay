using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FujinTerm.Views.GameData.Tables;

public partial class FavoritesSectionView : UserControl
{
    public FavoritesSectionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
