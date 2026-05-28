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
