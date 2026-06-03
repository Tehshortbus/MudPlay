using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FujinTerm.Views.Settings;

public partial class TalkSectionView : UserControl
{
    public TalkSectionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
