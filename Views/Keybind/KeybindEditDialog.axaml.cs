using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FujinTerm.ViewModels.Keybind;

namespace FujinTerm.Views.Keybind;

public partial class KeybindEditDialog : Window
{
    public KeybindEditDialog()
    {
        InitializeComponent();
        // Tunnel-stage handlers intercept keys before any control claims
        // them — so Enter / arrows / Escape get captured into a rebind
        // rather than firing default focus traversal / commands.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent,   OnPreviewKeyUp,   RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not KeybindEditDialogViewModel vm) return;
        if (vm.ProcessCaptureKey(e.Key, e.KeyModifiers, isKeyDown: true))
            e.Handled = true;
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is not KeybindEditDialogViewModel vm) return;
        if (vm.ProcessCaptureKey(e.Key, e.KeyModifiers, isKeyDown: false))
            e.Handled = true;
    }
}
