using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FujinTerm.Views.Settings;

public partial class AutoLightSectionView : UserControl
{
    public AutoLightSectionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
