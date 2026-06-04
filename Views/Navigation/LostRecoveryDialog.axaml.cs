using Avalonia.Controls;

namespace FujinTerm.Views.Navigation;

/// <summary>
/// Modeless info dialog for
/// <see cref="ViewModels.Navigation.LostRecoveryDialogViewModel"/>.
/// Pops from <see cref="Game.Map.EngineRecoveryGate.RecoveryFailed"/>.
/// </summary>
public partial class LostRecoveryDialog : Window
{
    public LostRecoveryDialog()
    {
        InitializeComponent();
    }
}
