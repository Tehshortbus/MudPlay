using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FujinTerm.Controls;
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

            // Wait for the first arrange pass before we scroll to the live
            // tail — otherwise heights are still zero and the ScrollViewer
            // would no-op.
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
            vm.FindMatchRequested -= OnFindMatch;
            vm.GoToLiveRequested  -= OnGoToLive;
            vm.Dispose();
        }
    }

    /// <summary>
    /// Highlight a Find-next hit by setting the SelectableTextBlock's
    /// selection at the matched span and scrolling to the row.
    /// </summary>
    private void OnFindMatch(int rowIndex, int columnOffset, int length)
    {
        if (_transcript is null || _scroll is null) return;
        if (DataContext is not BackscrollViewModel vm) return;
        if ((uint)rowIndex >= (uint)vm.Rows.Count) return;
        if (rowIndex >= _transcript.RowCharOffsets.Count) return;

        // Each row's text is laid out as: "HH:mm:ss" + 2 spaces + cell text.
        int prefixLen = vm.Rows[rowIndex].TimestampText.Length + 2;
        int abs = _transcript.RowCharOffsets[rowIndex] + prefixLen + columnOffset;
        _transcript.SelectionStart = abs;
        _transcript.SelectionEnd = abs + length;

        // No per-row container to BringIntoView — approximate the y offset
        // by row index × cell height. Mx437 16pt cells = 16px line height.
        const double rowHeight = 16;
        double target = rowIndex * rowHeight - _scroll.Viewport.Height / 2;
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
