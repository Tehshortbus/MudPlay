using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FujinTerm.ViewModels.GameData.Edit;

namespace FujinTerm.Views.GameData.Edit;

public partial class MacroEditDialog : Window
{
    public MacroEditDialog()
    {
        InitializeComponent();
        // While the VM is in capture mode, ALL key events on this
        // window funnel into ProcessCaptureKey instead of the normal
        // routing — so the user pressing Enter / Escape / arrows while
        // capturing rebinds them rather than triggering Save / Cancel /
        // focus traversal. Tunnel-stage handlers (Handled-routed but
        // pre-Bubble) catch the event before any control claims it.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent,   OnPreviewKeyUp,   RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MacroEditDialogViewModel vm) return;
        if (vm.ProcessCaptureKey(e.Key, e.KeyModifiers, isKeyDown: true))
            e.Handled = true;
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MacroEditDialogViewModel vm) return;
        if (vm.ProcessCaptureKey(e.Key, e.KeyModifiers, isKeyDown: false))
            e.Handled = true;
    }
}
