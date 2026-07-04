using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FujinTerm.ViewModels;

namespace FujinTerm.Views;

// Modeless Session Stats → Transaction history window. Bound to
// TransactionHistoryViewModel; code-behind only disposes the VM on close
// (unsubscribing it from the tracker) — everything else is XAML.
public partial class TransactionHistoryWindow : Window
{
    public TransactionHistoryWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        FujinTerm.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "transactions");
        Closed += OnClosed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is TransactionHistoryViewModel vm) vm.Dispose();
    }
}
