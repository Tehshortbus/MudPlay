using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    private ScrollViewer? _scroll;
    private ItemsControl? _rowsList;

    public BackscrollWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOpened(object? sender, EventArgs e)
    {
        _scroll = this.FindControl<ScrollViewer>("RowsScroll");
        _rowsList = this.FindControl<ItemsControl>("RowsList");
        if (DataContext is BackscrollViewModel vm)
        {
            vm.ScrollToRowRequested += OnScrollToRow;
            vm.GoToLiveRequested    += OnGoToLive;

            // Wait for the first arrange pass before we scroll to the live
            // tail — otherwise the container heights are still zero and the
            // ScrollViewer would no-op.
            Dispatcher.UIThread.Post(OnGoToLive, DispatcherPriority.Background);

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

        Control? container = _rowsList.ContainerFromIndex(index) as Control;
        container?.BringIntoView();
    }

    private void OnGoToLive()
    {
        if (_scroll is null) return;
        _scroll.Offset = new Avalonia.Vector(_scroll.Offset.X,
            Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height));
    }
}
