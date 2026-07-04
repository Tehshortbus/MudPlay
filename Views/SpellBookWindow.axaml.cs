using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FujinTerm.ViewModels;

namespace FujinTerm.Views;

// Modeless Spell Book window. Bound to SpellBookViewModel;
// code-behind only attaches the persisted window-layout, wires the
// global-hotkeys handler, and disposes the VM on close.
public partial class SpellBookWindow : Window
{
    public SpellBookWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        FujinTerm.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "spellbook");
        Closed += OnClosed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is SpellBookViewModel vm) vm.Dispose();
    }
}
