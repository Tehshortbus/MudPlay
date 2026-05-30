using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FujinTerm.ViewModels;

namespace FujinTerm.Views;

/// <summary>
/// Modeless system-event log pane. Bound to <see cref="LogPaneViewModel"/>;
/// code-behind handles two concerns XAML can't express cleanly: scrolling
/// the newest row into view when AutoScroll is on, and disposing the VM
/// on close.
/// </summary>
public partial class LogPaneWindow : Window
{
    private ListBox? _rowsList;

    public LogPaneWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        FujinTerm.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "logpane");
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOpened(object? sender, EventArgs e)
    {
        _rowsList = this.FindControl<ListBox>("RowsList");
        if (DataContext is LogPaneViewModel vm)
        {
            vm.Rows.CollectionChanged += OnRowsChanged;

            // Show the newest entries first — the log accumulates while
            // the window's closed, so opening it scrolled-to-top would
            // make the user scroll to find what just happened. Defer to
            // the next dispatcher tick so the ListBox has materialised
            // its items before we ask it to scroll one into view.
            if (vm.Rows.Count > 0)
            {
                object newest = vm.Rows[vm.Rows.Count - 1];
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => _rowsList?.ScrollIntoView(newest));
            }
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is LogPaneViewModel vm)
        {
            vm.Rows.CollectionChanged -= OnRowsChanged;
            vm.Dispose();
        }
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (DataContext is not LogPaneViewModel { AutoScroll: true }) return;
        if (_rowsList is null) return;
        if (e.NewItems is null || e.NewItems.Count == 0) return;
        object newest = e.NewItems[^1]!;
        _rowsList.ScrollIntoView(newest);
    }
}
