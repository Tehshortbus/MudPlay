using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FujinTerm.Views.Settings;

public partial class CombatSectionView : UserControl
{
    public CombatSectionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
