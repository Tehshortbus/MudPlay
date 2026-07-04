using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FujinTerm.ViewModels;

namespace FujinTerm.Views;

// Modeless terminal-history window. Bound to BackscrollViewModel.
// Code-behind handles concerns XAML can't express cleanly: scrolling a
// Find-next match into view, scrolling to the live tail, gating live
// mirroring on focus + scroll position, copying the selected rows, and
// disposing the VM on close.
public partial class BackscrollWindow : Window
{
    // Mx437 16pt cell height; used for the at-bottom slack in UpdateAutoFollow.
    private const double RowHeight = 16;

    private ListBox? _rowsList;
    // The ListBox's own inner ScrollViewer, resolved lazily once the control
    // template is applied. Only used for at-bottom detection — scroll actions
    // go through ListBox.ScrollIntoView.
    private ScrollViewer? _scroll;

    public BackscrollWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        FujinTerm.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "backscroll");
        Opened += OnOpened;
        Closed += OnClosed;
        // Ctrl+C over the row list (not inside a row's own text selection)
        // copies the selected rows' text. A per-row TranscriptRow that owns an
        // active character selection handles the copy first and marks the event
        // handled, so this only fires for whole-row selections.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOpened(object? sender, EventArgs e)
    {
        _rowsList = this.FindControl<ListBox>("RowsList");
        if (DataContext is BackscrollViewModel vm)
        {
            vm.FindMatchRequested += OnFindMatch;
            vm.GoToLiveRequested  += OnGoToLive;

            Activated   += OnActivationChanged;
            Deactivated += OnActivationChanged;

            // Wait for the first layout pass before resolving the inner
            // ScrollViewer (the ListBox template isn't applied yet on Opened)
            // and scrolling to the live tail.
            Dispatcher.UIThread.Post(() =>
            {
                HookScrollViewer();
                OnGoToLive();
            }, DispatcherPriority.Background);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_scroll is not null) _scroll.ScrollChanged -= OnScrollChanged;
        Activated   -= OnActivationChanged;
        Deactivated -= OnActivationChanged;
        if (DataContext is BackscrollViewModel vm)
        {
            vm.FindMatchRequested -= OnFindMatch;
            vm.GoToLiveRequested  -= OnGoToLive;
            vm.Dispose();
        }
    }

    private void HookScrollViewer()
    {
        if (_scroll is not null || _rowsList is null) return;
        _scroll = _rowsList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => UpdateAutoFollow();

    private void OnActivationChanged(object? sender, EventArgs e) => UpdateAutoFollow();

    // Mirror the live screen only while the user is actually watching the
    // tail — window focused AND scrolled to the bottom. Otherwise the VM
    // defers the live-tail refresh so a review of history isn't yanked down.
    private void UpdateAutoFollow()
    {
        if (_scroll is null || DataContext is not BackscrollViewModel vm) return;
        double maxOffset = _scroll.Extent.Height - _scroll.Viewport.Height;
        // A couple rows of slack so a single appended live line doesn't drop
        // us out of follow while we're parked at the bottom.
        bool atBottom = maxOffset <= RowHeight || _scroll.Offset.Y >= maxOffset - RowHeight * 2;
        vm.AutoFollow = IsActive && atBottom;
    }

    // Highlight a Find-next hit by selecting the matched row and scrolling it
    // into view. Row-granular (the ListBox owns selection); the column /
    // length the VM reports are unused here.
    private void OnFindMatch(int rowIndex, int columnOffset, int length)
    {
        if (_rowsList is null || DataContext is not BackscrollViewModel vm) return;
        if ((uint)rowIndex >= (uint)vm.Rows.Count) return;
        _rowsList.SelectedIndex = rowIndex;
        Dispatcher.UIThread.Post(() => _rowsList?.ScrollIntoView(rowIndex));
    }

    private void OnGoToLive()
    {
        if (_rowsList is null || DataContext is not BackscrollViewModel vm) return;
        if (vm.Rows.Count == 0) return;
        _rowsList.ScrollIntoView(vm.Rows.Count - 1);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key != Key.C || (e.KeyModifiers & KeyModifiers.Control) == 0) return;
        if (_rowsList is null || _rowsList.SelectedItems is not { Count: > 0 } sel) return;

        StringBuilder sb = new();
        foreach (object? item in sel)
        {
            if (item is BackscrollRowViewModel row) sb.AppendLine(row.PlainText);
        }
        if (sb.Length == 0) return;

        if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb)
        {
            _ = cb.SetTextAsync(sb.ToString());
            e.Handled = true;
        }
    }
}
