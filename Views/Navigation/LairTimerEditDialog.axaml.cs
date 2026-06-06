using Avalonia.Controls;

namespace FujinTerm.Views.Navigation;

/// <summary>
/// Small modeless dialog for editing a single marked lair's respawn
/// timer override. Spawned by NavigationViewModel.EditLairTimer from
/// the CURRENT NAV row's ✎ button.
/// </summary>
public partial class LairTimerEditDialog : Window
{
    public LairTimerEditDialog()
    {
        InitializeComponent();
    }
}
