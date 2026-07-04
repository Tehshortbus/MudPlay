using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FujinTerm.Controls;
using FujinTerm.ViewModels;

namespace FujinTerm.Views;

// Modeless terminal-history window. Bound to BackscrollViewModel.
// Code-behind handles concerns XAML can't express cleanly: scrolling to a
// Find-next match, scrolling to the live tail, gating live mirroring on focus
// + scroll position, and disposing the VM on close.
public partial class BackscrollWindow : Window
{
    // Mx437 16pt cell height; rows are a fixed line-height in the transcript.
    private const double RowHeight = 16;

    private ScrollViewer? _scroll;
    private SelectableTranscript? _transcript;

    public BackscrollWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        FujinTerm.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "backscroll");
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOpened(object? sender, EventArgs e)
    {
        _scroll = this.FindControl<ScrollViewer>("RowsScroll");
        _transcript = this.FindControl<SelectableTranscript>("Transcript");
        if (DataContext is BackscrollViewModel vm)
        {
            vm.FindMatchRequested += OnFindMatch;
            vm.GoToLiveRequested  += OnGoToLive;

            // Live mirroring is only worthwhile — and only affordable — while
            // the user is watching the tail: window focused AND scrolled to the
            // bottom. Track both so the VM can defer transcript-wide refreshes
            // otherwise (see BackscrollViewModel.AutoFollow).
            if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
            Activated   += OnActivationChanged;
            Deactivated += OnActivationChanged;

            // Wait for the first arrange pass before we scroll to the live
            // tail — otherwise heights are still zero and the ScrollViewer
            // would no-op.
            Dispatcher.UIThread.Post(OnGoToLive, DispatcherPriority.Background);
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

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => UpdateAutoFollow();

    private void OnActivationChanged(object? sender, EventArgs e) => UpdateAutoFollow();

    // Mirror the live screen only while the user is actually watching the
    // tail — window focused AND scrolled to the bottom. Otherwise the VM
    // defers the (full-transcript) refresh, keeping the main terminal smooth.
    private void UpdateAutoFollow()
    {
        if (_scroll is null || DataContext is not BackscrollViewModel vm) return;
        double maxOffset = _scroll.Extent.Height - _scroll.Viewport.Height;
        // A couple rows of slack so a single appended live line doesn't drop
        // us out of follow while we're parked at the bottom.
        bool atBottom = maxOffset <= RowHeight || _scroll.Offset.Y >= maxOffset - RowHeight * 2;
        vm.AutoFollow = IsActive && atBottom;
    }

    // Highlight a Find-next hit by setting the SelectableTextBlock's
    // selection at the matched span and scrolling to the row.
    private void OnFindMatch(int rowIndex, int columnOffset, int length)
    {
        if (_transcript is null || _scroll is null) return;
        if (DataContext is not BackscrollViewModel vm) return;
        if ((uint)rowIndex >= (uint)vm.Rows.Count) return;
        if (rowIndex >= _transcript.RowCharOffsets.Count) return;

        // Transcript text is cell content only (timestamps live in the
        // sibling TimestampGutter), so the column offset within the row
        // maps directly to the absolute position.
        int abs = _transcript.RowCharOffsets[rowIndex] + columnOffset;
        _transcript.SelectionStart = abs;
        _transcript.SelectionEnd = abs + length;

        // No per-row container to BringIntoView — approximate the y offset
        // by row index × cell height.
        double target = rowIndex * RowHeight - _scroll.Viewport.Height / 2;
        target = Math.Max(0, Math.Min(target, _scroll.Extent.Height - _scroll.Viewport.Height));
        _scroll.Offset = new Avalonia.Vector(_scroll.Offset.X, target);
    }

    private void OnGoToLive()
    {
        if (_scroll is null) return;
        _scroll.Offset = new Avalonia.Vector(_scroll.Offset.X,
            Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height));
    }
}
