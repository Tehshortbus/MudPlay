using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FujinTerm.Views.CharacterWorkshop;

public partial class EquipmentSectionView : UserControl
{
    public EquipmentSectionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
