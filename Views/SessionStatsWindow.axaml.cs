using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FujinTerm.ViewModels;

namespace FujinTerm.Views;

/// <summary>
/// Modeless Session Stats window. Bound to <see cref="SessionStatsViewModel"/>;
/// code-behind only attaches the persisted window-layout, wires the
/// global-hotkeys handler, and disposes the VM on close.
/// </summary>
public partial class SessionStatsWindow : Window
{
    public SessionStatsWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        FujinTerm.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "session-stats");
        Closed += OnClosed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is SessionStatsViewModel vm) vm.Dispose();
    }
}
