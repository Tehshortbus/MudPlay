using System.Collections.Specialized;
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

// Modeless terminal-history window. Bound to BackscrollViewModel.
// The transcript is rendered as one SelectableTextBlock (Content) plus an
// aligned timestamp gutter, so native drag-select spans lines and Ctrl+C copies
// the exact character range. The code-behind rebuilds both from the VM's Rows,
// positions the live/history divider against the scroll offset, follows the live
// tail, and drives Find-next / Go-to-live by scrolling the viewer.
public partial class BackscrollWindow : Window
{
    // Fallback line height before the first layout pass measures the real one.
    private const double FallbackLineHeight = 16;
    // _scroll.Padding top — the transcript's first line sits this far below
    // the viewport top, so the divider math must add it back.
    private const double ContentPaddingTop = 4;

    // Absolute character offset (into the transcript's text) where each row's
    // text begins — translates a Find hit's (row, column) into a selection.
    private int[] _lineStartOffsets = [];
    private int _lineCount;
    private bool _rebuildScheduled;

    // Named controls resolved from the XAML name scope after load. This project
    // hand-rolls InitializeComponent, so Avalonia's generated x:Name fields are
    // never populated — every window pulls its controls via FindControl (see
    // LogPaneWindow / ConversationWindow). Accessing the generated fields
    // directly leaves them null and faults on first use.
    private readonly ScrollViewer _scroll;
    private readonly SelectableTextBlock _transcript;
    private readonly TextBlock _gutter;
    private readonly Border _separator;

    public BackscrollWindow()
    {
        InitializeComponent();
        _scroll     = this.FindControl<ScrollViewer>("OuterScroll")!;
        _transcript = this.FindControl<SelectableTextBlock>("Transcript")!;
        _gutter     = this.FindControl<TextBlock>("Gutter")!;
        _separator  = this.FindControl<Border>("LiveSeparator")!;
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

        vm.Rows.CollectionChanged += OnRowsChanged;
        vm.FindMatchRequested += OnFindMatch;
        vm.GoToLiveRequested  += OnGoToLive;

        _scroll.ScrollChanged += OnScrollChanged;
        Activated   += OnActivationChanged;
        Deactivated += OnActivationChanged;

        // Build once the template + first layout exist, then park at the
        // history/live boundary (not the absolute tail) so the window opens on
        // the newest scrollback with the live line just in view.
        Dispatcher.UIThread.Post(() =>
        {
            Rebuild();
            // Opening off the bottom means we're not following the tail; clear
            // AutoFollow now so Rebuild's post-layout callback doesn't yank us
            // down to live before ScrollToLiveBoundary runs.
            vm.AutoFollow = false;
            Dispatcher.UIThread.Post(ScrollToLiveBoundary, DispatcherPriority.Background);
        }, DispatcherPriority.Background);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _scroll.ScrollChanged -= OnScrollChanged;
        Activated   -= OnActivationChanged;
        Deactivated -= OnActivationChanged;
        if (DataContext is BackscrollViewModel vm)
        {
            vm.Rows.CollectionChanged -= OnRowsChanged;
            vm.FindMatchRequested -= OnFindMatch;
            vm.GoToLiveRequested  -= OnGoToLive;
            vm.Dispose();
        }
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleRebuild();

    // Coalesce the many CollectionChanged events a single live-tail refresh emits
    // (replace ~25 rows + add/remove) into one rebuild per dispatcher tick.
    private void ScheduleRebuild()
    {
        if (_rebuildScheduled) return;
        _rebuildScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _rebuildScheduled = false;
            Rebuild();
        }, DispatcherPriority.Background);
    }

    // Rebuild the whole transcript: colour runs into Transcript, timestamps into
    // the gutter, and record each line's start offset. Expensive by design — the
    // ring is capped, and one text block is what lets a selection span rows.
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

        // After layout settles: keep the tail in view while following, and
        // re-place the divider for the new content height.
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is BackscrollViewModel v && v.AutoFollow) OnGoToLive();
            UpdateSeparator();
        }, DispatcherPriority.Background);
    }

    private double LineHeight()
        => _lineCount > 0 && _transcript.Bounds.Height > 0
            ? _transcript.Bounds.Height / _lineCount
            : FallbackLineHeight;

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        UpdateAutoFollow();
        UpdateSeparator();
    }

    private void OnActivationChanged(object? sender, EventArgs e) => UpdateAutoFollow();

    // Mirror the live screen only while the user is actually watching the tail —
    // window focused AND scrolled to the bottom. Otherwise the VM defers the
    // live-tail refresh so a review of history isn't yanked down.
    private void UpdateAutoFollow()
    {
        if (DataContext is not BackscrollViewModel vm) return;
        double maxOffset = _scroll.Extent.Height - _scroll.Viewport.Height;
        double lh = LineHeight();
        bool atBottom = maxOffset <= lh || _scroll.Offset.Y >= maxOffset - lh * 2;
        vm.AutoFollow = IsActive && atBottom;
    }

    // Position the amber history/live divider at the boundary row, offset by the
    // scroll. Hidden when the boundary is scrolled out of view or there's no
    // history above the live tail.
    private void UpdateSeparator()
    {
        if (DataContext is not BackscrollViewModel vm) { _separator.IsVisible = false; return; }
        int boundary = vm.ScrollbackCount;
        if (boundary <= 0 || boundary >= vm.Rows.Count) { _separator.IsVisible = false; return; }

        double y = ContentPaddingTop + boundary * LineHeight() - _scroll.Offset.Y;
        if (y < 0 || y > _scroll.Viewport.Height)
        {
            _separator.IsVisible = false;
            return;
        }
        _separator.Margin = new Thickness(0, y, 0, 0);
        _separator.IsVisible = true;
    }

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

    private void OnGoToLive()
    {
        double maxY = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        _scroll.Offset = _scroll.Offset.WithY(maxY);
    }

    // Open parked at the history/live boundary rather than the absolute tail:
    // land the amber divider a couple of lines up from the viewport bottom so
    // the newest scrollback fills the view and the live line is just in sight.
    private void ScrollToLiveBoundary()
    {
        if (DataContext is not BackscrollViewModel vm) { OnGoToLive(); return; }
        int boundary = vm.ScrollbackCount;
        if (boundary <= 0 || boundary >= vm.Rows.Count) { OnGoToLive(); return; }

        double lh = LineHeight();
        double dividerY = ContentPaddingTop + boundary * lh;
        double maxY = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        double targetY = Math.Clamp(dividerY - _scroll.Viewport.Height + lh * 2, 0, maxY);
        _scroll.Offset = _scroll.Offset.WithY(targetY);
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
