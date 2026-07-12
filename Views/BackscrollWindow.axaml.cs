using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FujinTerm.Controls;
using FujinTerm.ViewModels;

namespace FujinTerm.Views;

// Modeless terminal-history window. Bound to BackscrollViewModel, which holds a
// frozen snapshot captured when the window opened (the window never tracks the
// live terminal). The transcript is a BackscrollView — a virtualized canvas that
// renders only the visible rows and owns its own (row, col) selection — so
// drag-select and copy stay flat even on a deep history. The code-behind feeds it
// the VM's rows once and drives Find-next / Jump-to-end by scrolling the viewer.
public partial class BackscrollWindow : Window
{
    // Named controls resolved from the XAML name scope after load. This project
    // hand-rolls InitializeComponent, so Avalonia's generated x:Name fields are
    // never populated — every window pulls its controls via FindControl (see
    // LogPaneWindow / ConversationWindow). Accessing the generated fields
    // directly leaves them null and faults on first use.
    private readonly ScrollViewer _scroll;
    private readonly BackscrollView _view;

    public BackscrollWindow()
    {
        InitializeComponent();
        _scroll = this.FindControl<ScrollViewer>("OuterScroll")!;
        _view   = this.FindControl<BackscrollView>("Transcript")!;
        GlobalHotkeys.Attach(this);
        FujinTerm.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "backscroll");
        Opened += OnOpened;
        Closed += OnClosed;
        // Ctrl+C copies the view's (row, col) selection. The view doesn't own a
        // native text selection, so the window handles the copy itself.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not BackscrollViewModel vm) return;

        vm.FindMatchRequested += OnFindMatch;
        vm.JumpToEndRequested += OnJumpToEnd;

        _view.SetRows(vm.Rows.Select(r => r.Source).ToArray());

        // Park at the newest captured row once the first layout pass has sized
        // the viewport, so the window opens on the tail of the history.
        Dispatcher.UIThread.Post(OnJumpToEnd, DispatcherPriority.Background);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is BackscrollViewModel vm)
        {
            vm.FindMatchRequested -= OnFindMatch;
            vm.JumpToEndRequested -= OnJumpToEnd;
        }
    }

    // Highlight a Find-next hit by selecting its (row, col) span and scrolling it
    // into view (~a third down the viewport).
    private void OnFindMatch(int rowIndex, int columnOffset, int length)
    {
        _view.SelectMatch(rowIndex, columnOffset, length);

        double maxY = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        double targetY = Math.Clamp(_view.RowTop(rowIndex) - _scroll.Viewport.Height / 3, 0, maxY);
        _scroll.Offset = _scroll.Offset.WithY(targetY);
    }

    private void OnJumpToEnd()
    {
        double maxY = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        _scroll.Offset = _scroll.Offset.WithY(maxY);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key != Key.C || (e.KeyModifiers & KeyModifiers.Control) == 0) return;
        // The search box owns its own Ctrl+C.
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox) return;

        string sel = _view.SelectedText;
        if (string.IsNullOrEmpty(sel)) return;

        if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb)
        {
            _ = CopyAsync(cb, sel);
            e.Handled = true;
        }
    }

    // Clipboard writes route through DBus on Linux; on a host with no
    // activatable clipboard service the Task faults. Observing it inside a
    // try/catch keeps a best-effort copy from surfacing as an unobserved
    // TaskScheduler exception that would fault the finalizer thread.
    private static async Task CopyAsync(IClipboard cb, string text)
    {
        try
        {
            await cb.SetTextAsync(text).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort copy; a missing/broken platform clipboard is non-fatal.
        }
    }
}
