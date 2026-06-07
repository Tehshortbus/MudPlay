using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FujinTerm.ViewModels.CharacterWorkshop;

namespace FujinTerm.Views.CharacterWorkshop;

public partial class CharacterWorkshopWindow : Window
{
    public CharacterWorkshopWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        FujinTerm.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "workshop");

        DataContextChanged += (_, _) =>
        {
            if (DataContext is CharacterWorkshopViewModel vm)
                vm.CloseRequested += Close;
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
