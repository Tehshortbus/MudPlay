using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
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
// live terminal). The transcript is rendered as one SelectableTextBlock (Content)
// plus an aligned timestamp gutter, so native drag-select spans lines and Ctrl+C
// copies the exact character range. The code-behind builds both from the VM's
// Rows once and drives Find-next / Jump-to-end by scrolling the viewer.
public partial class BackscrollWindow : Window
{
    // Fallback line height before the first layout pass measures the real one.
    private const double FallbackLineHeight = 16;

    // Absolute character offset (into the transcript's text) where each row's
    // text begins — translates a Find hit's (row, column) into a selection.
    private int[] _lineStartOffsets = [];
    private int _lineCount;

    // Named controls resolved from the XAML name scope after load. This project
    // hand-rolls InitializeComponent, so Avalonia's generated x:Name fields are
    // never populated — every window pulls its controls via FindControl (see
    // LogPaneWindow / ConversationWindow). Accessing the generated fields
    // directly leaves them null and faults on first use.
    private readonly ScrollViewer _scroll;
    private readonly SelectableTextBlock _transcript;
    private readonly TextBlock _gutter;

    public BackscrollWindow()
    {
        InitializeComponent();
        _scroll     = this.FindControl<ScrollViewer>("OuterScroll")!;
        _transcript = this.FindControl<SelectableTextBlock>("Transcript")!;
        _gutter     = this.FindControl<TextBlock>("Gutter")!;
        GlobalHotkeys.Attach(this);
        FujinTerm.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "backscroll");
        Opened += OnOpened;
        Closed += OnClosed;
        // Ctrl+C copies the transcript's character selection. The native
        // SelectableTextBlock handles it first when it owns focus (and marks the
        // event handled); this catches the case where focus drifted after the
        // drag but a selection is still live.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not BackscrollViewModel vm) return;

        vm.FindMatchRequested += OnFindMatch;
        vm.JumpToEndRequested += OnJumpToEnd;

        // Build once the template + first layout exist, then park at the newest
        // captured row so the window opens on the tail of the history.
        Dispatcher.UIThread.Post(() =>
        {
            Rebuild();
            Dispatcher.UIThread.Post(OnJumpToEnd, DispatcherPriority.Background);
        }, DispatcherPriority.Background);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is BackscrollViewModel vm)
        {
            vm.FindMatchRequested -= OnFindMatch;
            vm.JumpToEndRequested -= OnJumpToEnd;
        }
    }

    // Build the whole transcript once: colour runs into Transcript, timestamps
    // into the gutter, and record each line's start offset. Expensive by design —
    // the snapshot is frozen and capped, and one text block is what lets a
    // selection span rows.
    private void Rebuild()
    {
        if (DataContext is not BackscrollViewModel vm) return;

        InlineCollection inlines = new();
        StringBuilder gutter = new(vm.Rows.Count * 9);
        int[] offsets = new int[vm.Rows.Count];
        int abs = 0;

        for (int i = 0; i < vm.Rows.Count; i++)
        {
            BackscrollRowViewModel row = vm.Rows[i];
            if (i > 0)
            {
                inlines.Add(new LineBreak());
                gutter.Append('\n');
                abs += 1;                       // the newline counts as one char
            }
            offsets[i] = abs;
            abs += TranscriptInlineBuilder.AppendRow(inlines, row.Cells);
            gutter.Append(row.TimestampText);
        }

        _transcript.Inlines = inlines;
        _gutter.Text = gutter.ToString();
        _lineStartOffsets = offsets;
        _lineCount = vm.Rows.Count;
    }

    private double LineHeight()
        => _lineCount > 0 && _transcript.Bounds.Height > 0
            ? _transcript.Bounds.Height / _lineCount
            : FallbackLineHeight;

    // Highlight a Find-next hit by selecting its character range and scrolling it
    // into view (~a third down the viewport).
    private void OnFindMatch(int rowIndex, int columnOffset, int length)
    {
        if ((uint)rowIndex >= (uint)_lineStartOffsets.Length) return;

        int start = _lineStartOffsets[rowIndex] + columnOffset;
        _transcript.SelectionStart = start;
        _transcript.SelectionEnd = start + length;

        double lh = LineHeight();
        double maxY = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        double targetY = Math.Clamp(rowIndex * lh - _scroll.Viewport.Height / 3, 0, maxY);
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

        string sel = _transcript.SelectedText;
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
