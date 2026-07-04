using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using FujinTerm.ViewModels;

namespace FujinTerm.Views;

// Modeless system-event log pane. Bound to LogPaneViewModel;
// code-behind handles two concerns XAML can't express cleanly: scrolling
// the newest row into view when AutoScroll is on, and disposing the VM
// on close.
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
            // BulkUpdateCompleted fires once at the end of each Rebuild.
            // We use it to do a single ScrollIntoView on the newest row
            // instead of the per-Add cascade — that cascade was the UI
            // lockup on Combat-filter toggles.
            vm.BulkUpdateCompleted += OnBulkUpdateCompleted;

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
            vm.BulkUpdateCompleted    -= OnBulkUpdateCompleted;
            vm.Dispose();
        }
    }

    private void OnBulkUpdateCompleted()
    {
        if (DataContext is not LogPaneViewModel { AutoScroll: true } vm) return;
        if (_rowsList is null) return;
        if (vm.Rows.Count == 0) return;
        object newest = vm.Rows[vm.Rows.Count - 1];
        // Defer so the ListBox has materialised the new containers
        // before we ask it to scroll one into view.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => _rowsList?.ScrollIntoView(newest));
    }

    // Find the LogPaneRowViewModel under the tapped point (walks up the
    // visual tree from the event source) and react:
    //  1. If the entry carries a LogEntry.Context payload (RoomClassifier
    //     emits these on Unknown rows with the raw "Also Here" line), copy
    //     the context to the clipboard so the user can immediately paste
    //     into a fix dialog. The transient confirmation goes to the
    //     LogService at Info severity.
    //  2. Dispatch via LogService.TryInvokeDetailHandler — opens the
    //     source-specific detail window when one is registered (e.g.
    //     SpellCoverageAuditor, the room fix dialog).
    // Either path can no-op without affecting the other.
    private void OnRowDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Visual src) return;
        // Walk up looking for a ListBoxItem whose DataContext is our row.
        Visual? cur = src;
        while (cur is not null)
        {
            if (cur is Control c && c.DataContext is LogPaneRowViewModel row)
            {
                if (row.Entry.Context is { Length: > 0 } ctx)
                    CopyContextToClipboard(ctx);
                // Both registration styles fire when both are present —
                // SpellCoverageAuditor's no-arg handler + the
                // RoomClassifier's entry-aware handler use different
                // sources today, but a future producer could register
                // both for the same Source without losing either.
                FujinTerm.Services.LogService logSvc =
                    FujinTerm.Services.AppServices.Current.Log;
                logSvc.TryInvokeDetailHandler(row.Entry.Source);
                logSvc.TryInvokeDetailHandler(row.Entry);
                return;
            }
            cur = cur.GetVisualParent();
        }
    }

    // Push text to the top-level window's clipboard asynchronously and
    // surface a one-line Info-severity confirmation in the LogPane so the
    // user sees the copy happened. Clipboard failures are surfaced as
    // Warn — usually a sandboxed environment or a missing top-level (the
    // latter shouldn't happen since we're running inside one).
    private void CopyContextToClipboard(string text)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top?.Clipboard is not { } cb)
        {
            FujinTerm.Services.AppServices.Current.Log.Warn("LogPane",
                "Clipboard unavailable; context not copied.");
            return;
        }
        _ = CopyAsync(cb, text);
    }

    private static async Task CopyAsync(Avalonia.Input.Platform.IClipboard cb, string text)
    {
        try
        {
            await cb.SetTextAsync(text).ConfigureAwait(false);
            FujinTerm.Services.AppServices.Current.Log.Info("LogPane",
                "Copied context to clipboard.");
        }
        catch (Exception ex)
        {
            FujinTerm.Services.AppServices.Current.Log.Warn("LogPane",
                $"Clipboard copy failed: {ex.Message}");
        }
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (DataContext is not LogPaneViewModel { AutoScroll: true } vm) return;
        // Skip the per-row scroll while a Rebuild is in flight — one
        // final scroll after the rebuild is enough, and the cascade
        // (1000s of ScrollIntoView calls in a tight loop) is what
        // locked the UI thread on Combat-filter toggles.
        if (vm.IsBulkUpdating) return;
        if (_rowsList is null) return;
        if (e.NewItems is null || e.NewItems.Count == 0) return;
        object newest = e.NewItems[^1]!;
        // Defer the scroll out of the CollectionChanged callback. Calling
        // ScrollIntoView synchronously here re-enters the layout pass while
        // the virtualizing panel is mid-update (the new container isn't
        // measured yet), which throws "Invalid Arrange rectangle" — it
        // crashed the app whenever a log line streamed in right as the pane
        // opened. Posting lets the panel finish its layout first.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => _rowsList?.ScrollIntoView(newest));
    }
}
