using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FujinTerm.Views;

// Modeless Party window. Bound to ViewModels.PartyViewModel;
// code-behind only attaches the persisted window-layout and wires the
// global-hotkeys handler so chord forwards (Ctrl+G etc.) still work
// when the Party window has focus.
public partial class PartyWindow : Window
{
    public PartyWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        FujinTerm.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "party");
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
