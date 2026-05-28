using Avalonia.Controls;
using FujinTerm.ViewModels.Settings;

namespace FujinTerm.Views.Settings;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        // Re-subscribe whenever the view-model is swapped; first attach happens
        // after construction when the host assigns DataContext.
        DataContextChanged += (_, _) => HookCloseRequested();
    }

    private SettingsWindowViewModel? _hooked;

    private void HookCloseRequested()
    {
        if (_hooked is not null)
        {
            _hooked.CloseRequested -= Close;
            _hooked = null;
        }
        if (DataContext is SettingsWindowViewModel vm)
        {
            vm.CloseRequested += Close;
            _hooked = vm;
        }
    }
}
