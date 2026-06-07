using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FujinTerm.Views.CharacterWorkshop;

public partial class DeathSectionView : UserControl
{
    public DeathSectionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
