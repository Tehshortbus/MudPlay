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

        // Title-bar X / Alt-F4 / parent-window-close: treat as Cancel
        // (discard pending edits) so the window can't sneak past the
        // user's explicit commit decision. Routed through DiscardChanges
        // rather than DiscardAndClose to avoid re-entering Close.
        Closing += (_, _) =>
        {
            if (DataContext is SettingsWindowViewModel vm && !vm.IsCommitted)
            {
                vm.DiscardChanges();
            }
        };
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
