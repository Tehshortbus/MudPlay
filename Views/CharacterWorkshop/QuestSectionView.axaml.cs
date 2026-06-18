using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FujinTerm.Views.CharacterWorkshop;

public partial class QuestSectionView : UserControl
{
    public QuestSectionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
