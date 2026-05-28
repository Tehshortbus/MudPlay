using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FujinTerm.ViewModels;

namespace FujinTerm.Views;

/// <summary>
/// Modeless terminal-history window. Bound to <see cref="BackscrollViewModel"/>.
/// Code-behind handles three concerns XAML can't express cleanly: scrolling
/// to a Find-next match, scrolling to the live tail, and disposing the VM
/// on close.
/// </summary>
public partial class BackscrollWindow : Window
{
    private ListBox? _rowsList;

    public BackscrollWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOpened(object? sender, EventArgs e)
    {
        _rowsList = this.FindControl<ListBox>("RowsList");
        if (DataContext is BackscrollViewModel vm)
        {
            vm.ScrollToRowRequested += OnScrollToRow;
            vm.GoToLiveRequested    += OnGoToLive;
            OnGoToLive();   // open on the freshest content.

            if (vm.FocusSearchOnOpen)
            {
                this.FindControl<TextBox>("SearchBox")?.Focus();
            }
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is BackscrollViewModel vm)
        {
            vm.ScrollToRowRequested -= OnScrollToRow;
            vm.GoToLiveRequested    -= OnGoToLive;
            vm.Dispose();
        }
    }

    private void OnScrollToRow(int index)
    {
        if (_rowsList is null) return;
        if (DataContext is not BackscrollViewModel vm) return;
        if ((uint)index >= (uint)vm.Rows.Count) return;
        _rowsList.ScrollIntoView(vm.Rows[index]);
    }

    private void OnGoToLive()
    {
        if (_rowsList is null) return;
        if (DataContext is not BackscrollViewModel vm) return;
        if (vm.Rows.Count == 0) return;
        _rowsList.ScrollIntoView(vm.Rows[^1]);
    }
}
