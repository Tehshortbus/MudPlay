using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FujinTerm.Views.CharacterWorkshop;

public partial class CharacterWorkshopWindow : Window
{
    public CharacterWorkshopWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        FujinTerm.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "workshop");
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
