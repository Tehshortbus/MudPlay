using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FujinTerm.Views.Settings;

public partial class StatlineSectionView : UserControl
{
    public StatlineSectionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
