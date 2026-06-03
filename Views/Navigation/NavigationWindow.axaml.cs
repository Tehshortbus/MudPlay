using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FujinTerm.Views.Navigation;

/// <summary>
/// Modeless Navigation window. Bound to
/// <see cref="ViewModels.Navigation.NavigationViewModel"/>; PR 7.10
/// ships the shell with status strip + mode bar + map placeholder.
/// The MapControl lands in PR 7.11; the right-rail tree / favourites
/// / loop builder land in PRs 7.12–7.17.
/// </summary>
public partial class NavigationWindow : Window
{
    public NavigationWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        FujinTerm.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "navigation");
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
