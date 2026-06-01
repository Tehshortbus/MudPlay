using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
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
        // Double-click a row → look up a detail handler by Source and
        // invoke it. Lets services like SpellCoverageAuditor register
        // a Source ("GameData/Coverage") + open a detail window
        // without the LogPane needing to know what their domain is.
        AddHandler(InputElement.DoubleTappedEvent, OnRowDoubleTapped, RoutingStrategies.Bubble);
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

    /// <summary>
    /// Find the LogPaneRowViewModel under the tapped point (walks up
    /// the visual tree from the event source) and dispatch via
    /// <see cref="FujinTerm.Services.LogService.TryInvokeDetailHandler"/>.
    /// No registered handler ⇒ no-op (same behaviour as today for
    /// every existing entry — only opt-in sources get a detail
    /// surface).
    /// </summary>
    private void OnRowDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Visual src) return;
        // Walk up looking for a ListBoxItem whose DataContext is our row.
        Visual? cur = src;
        while (cur is not null)
        {
            if (cur is Control c && c.DataContext is LogPaneRowViewModel row)
            {
                FujinTerm.Services.AppServices.Current.Log
                    .TryInvokeDetailHandler(row.Entry.Source);
                return;
            }
            cur = cur.GetVisualParent();
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
