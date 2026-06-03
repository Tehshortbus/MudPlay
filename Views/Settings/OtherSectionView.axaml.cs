using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FujinTerm.Views.Settings;

public partial class OtherSectionView : UserControl
{
    public OtherSectionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
