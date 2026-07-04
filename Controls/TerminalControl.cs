using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using FujinTerm.Terminal;

namespace FujinTerm.Controls;

// Custom Avalonia control that draws the terminal grid and forwards keyboard
// input back out to the view-model.
//
// Rendering pipeline:
//   1. Compute per-cell pixel size from the chosen monospace font.
//   2. For each row, walk left-to-right grouping consecutive cells that
//      share the same attributes into "runs" (single fill + per-glyph draw).
//   3. After all cells are drawn, paint the cursor caret if visible.
//
// Input pipeline:
//   - OnTextInput catches normal printable text and posts the bytes
//     (Latin-1 encoded) to UserInput.
//   - OnKeyDown maps non-text keys (arrows, Enter, Ctrl+letter, F1–F4) to
//     the matching ANSI/VT escape sequences and emits those.
public sealed class TerminalControl : Control
{
    // The emulator whose screen we render. Bound from XAML.
    public static readonly StyledProperty<TerminalEmulator?> EmulatorProperty =
        AvaloniaProperty.Register<TerminalControl, TerminalEmulator?>(nameof(Emulator));

    // Bitmap-style monospace font; defaults to embedded MX437.
    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<TerminalControl, FontFamily>(
            nameof(FontFamily),
            new FontFamily("avares://FujinTerm/Assets/Fonts/Mx437_IBM_VGA_8x16.ttf#Mx437 IBM VGA 8x16"));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<TerminalControl, double>(nameof(FontSize), 16.0);

    public TerminalEmulator? Emulator
    {
        get => GetValue(EmulatorProperty);
        set => SetValue(EmulatorProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    // Raised on the UI thread with bytes to send to the host.
    public event Action<byte[]>? UserInput;

    // Optional client-side line buffer. When set, printable keystrokes
    // accumulate locally and only flush to the wire on Enter — so engine
    // auto-sends (par poll, AutoParty invite, @health round-trip, etc.) can't
    // interleave into the user's half-typed input on the server side. See
    // Terminal.LocalInputBuffer for rationale. When null (no buffer attached)
    // the control falls back to the classic character-mode path — every
    // keystroke straight to the wire.
    public LocalInputBuffer? InputBuffer { get; set; }

    private Typeface _typeface;
    private double _cellW = 8;
    private double _cellH = 16;
    private bool _cursorBlinkOn = true;
    private DispatcherTimer? _blinkTimer;
    private Action? _onBufferChanged;

    // ----- Post-Enter "pending" overlay ---------------------------------
    // Without this, hitting Enter clears the local buffer immediately,
    // the overlay disappears, and the user sees a half-second of empty
    // space before the server's echo arrives and repaints the line.
    // We capture the flushed text + cursor position so the overlay
    // continues to render at the SAME spot — when the server echoes
    // back, the real cells underneath fill in with the same text and
    // there's no visual transition. Cleared on the next screen
    // update (which is almost always the echo itself).
    private string? _pendingFlushText;
    private int _pendingFlushRow;
    private int _pendingFlushCol;

    // Per-control cursor over the shared command-recall history. Built
    // lazily on first keystroke (AppServices.Current is live by then; it
    // may not be at design-time construction).
    private FujinTerm.Services.CommandHistoryNavigator? _historyNav;
    private FujinTerm.Services.CommandHistoryNavigator HistoryNav =>
        _historyNav ??= new(FujinTerm.Services.AppServices.Current.CommandHistory);

    public TerminalControl()
    {
        Focusable = true;
        ClipToBounds = true;
        _typeface = new Typeface(FontFamily);
        // Bitmap-style fonts (Mx437) need aliased rendering to avoid color
        // smearing across cell boundaries; subpixel AA fringes box-drawing chars.
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Alias);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    static TerminalControl()
    {
        // Wire dependency-property change reactions: rebuild metrics when
        // the font changes, repaint when the emulator pointer changes.
        EmulatorProperty.Changed.AddClassHandler<TerminalControl>((c, e) => c.OnEmulatorChanged(
            (TerminalEmulator?)e.OldValue, (TerminalEmulator?)e.NewValue));
        FontFamilyProperty.Changed.AddClassHandler<TerminalControl>((c, _) => c.RecalculateMetrics());
        FontSizeProperty.Changed.AddClassHandler<TerminalControl>((c, _) => c.RecalculateMetrics());
        AffectsRender<TerminalControl>(EmulatorProperty);
    }

    private void OnEmulatorChanged(TerminalEmulator? oldEm, TerminalEmulator? newEm)
    {
        // Detach from the previous emulator before subscribing to the new
        // one to avoid leaking handler references.
        if (oldEm is not null)
        {
            oldEm.ScreenUpdated -= OnScreenUpdated;
            oldEm.ScreenResized -= OnScreenResized;
        }
        if (newEm is not null)
        {
            newEm.ScreenUpdated += OnScreenUpdated;
            newEm.ScreenResized += OnScreenResized;
        }
        InvalidateMeasure();
        InvalidateVisual();
    }

    // ScreenUpdated may fire on any thread; invalidation must happen on the
    // UI thread. Also clears the post-Enter pending overlay — the server
    // just sent output (the most common case being the echo of the line
    // we just submitted), so the cell grid behind the overlay is now
    // accurate and the overlay can stop drawing.
    private void OnScreenUpdated()
    {
        if (_pendingFlushText is not null)
        {
            _pendingFlushText = null;
        }
        Dispatcher.UIThread.Post(InvalidateVisual);
    }

    // ScreenResized only fires on Emulator.Resize. Re-measure so the
    // canvas grows / shrinks to match the new cell grid.
    private void OnScreenResized() => Dispatcher.UIThread.Post(() =>
    {
        InvalidateMeasure();
        InvalidateVisual();
    });

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RecalculateMetrics();
        // Cursor blink: toggle on/off twice a second.
        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _blinkTimer.Tick += (_, _) => { _cursorBlinkOn = !_cursorBlinkOn; InvalidateVisual(); };
        _blinkTimer.Start();
        // Repaint the buffer overlay whenever the user types / backspaces
        // / flushes. Stored as a field so we can unsubscribe on detach
        // without leaking the strong handler reference into the buffer.
        if (InputBuffer is { } buf)
        {
            _onBufferChanged = () => Dispatcher.UIThread.Post(InvalidateVisual);
            buf.Changed += _onBufferChanged;
        }
        Focus();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _blinkTimer?.Stop();
        _blinkTimer = null;
        if (_onBufferChanged is not null && InputBuffer is { } buf)
        {
            buf.Changed -= _onBufferChanged;
            _onBufferChanged = null;
        }
    }

    // Measure the width and height of the chosen font's "M" glyph and snap the
    // result to whole pixels. Used as the per-cell box size.
    private void RecalculateMetrics()
    {
        _typeface = new Typeface(FontFamily);
        var probe = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typeface, FontSize, Brushes.White);
        // Snap to integer pixels so adjacent cell BG fills meet exactly and
        // glyph advances align with cell grid. Without this, sub-pixel
        // residue shows up as 1px gaps/overlaps across cell boundaries.
        _cellW = Math.Max(1, Math.Round(probe.WidthIncludingTrailingWhitespace));
        _cellH = Math.Max(1, Math.Round(probe.Height));
        InvalidateMeasure();
        InvalidateVisual();
    }

    // Tell layout the control wants exactly cols × rows × cell pixels.
    protected override Size MeasureOverride(Size availableSize)
    {
        var em = Emulator;
        if (em is null) return new Size(_cellW * 80, _cellH * 25);
        return new Size(_cellW * em.Screen.Cols, _cellH * em.Screen.Rows);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var em = Emulator;
        var bounds = new Rect(Bounds.Size);
        // Clear background. (Done explicitly even though Background="Black"
        // is the window default — inside the ScrollViewer we may extend.)
        context.FillRectangle(Brushes.Black, bounds);
        if (em is null) return;

        var screen = em.Screen;

        // Resolve the local-line-edit overlay (buffered, not-yet-sent text)
        // up front — its length decides whether the whole screen needs to
        // scroll to keep the caret in view.
        //
        // Three render modes, in priority order:
        //   1. Live buffer non-empty → draw at CURRENT cursor.
        //   2. Live buffer empty but pending-flush captured → draw the
        //      just-Enter'd text at its CAPTURED cursor, so the visual
        //      stays seamless until the server's echo arrives.
        //   3. Neither → no overlay; caret defaults to current cursor.
        int caretCol = screen.CursorX;
        int caretRow = screen.CursorY;
        string? overlayText = null;
        int overlayStartCol = screen.CursorX;
        int overlayStartRow = screen.CursorY;
        // Character-mode (full-screen forms) suppresses the overlay
        // entirely — the server renders its own form + echo, so the
        // caret simply tracks the server cursor. Both overlay branches
        // gate on it so neither the live buffer nor a pending flush paints.
        bool charMode = InputBuffer is { CharacterMode: true };
        if (!charMode && InputBuffer is { Length: > 0 } buffer)
        {
            overlayText = buffer.Text;
        }
        else if (!charMode && _pendingFlushText is not null)
        {
            overlayText = _pendingFlushText;
            overlayStartCol = _pendingFlushCol;
            overlayStartRow = _pendingFlushRow;
        }

        // MajorMUD keeps its prompt on the bottom row, so a buffer long
        // enough to wrap spills past the last row. Rather than truncate the
        // tail (hiding what the user types) or float just the input upward
        // (it slides over static content — jarring), scroll the WHOLE screen
        // up by the overflow, exactly as character-mode server echo does.
        // The input then simply wraps onto the next line while the screen
        // scrolls to reveal it.
        int rowShift = 0;
        if (overlayText is not null)
        {
            int endCol = overlayStartCol;
            int endRow = overlayStartRow;
            foreach (char ch in overlayText)
            {
                if (endCol >= screen.Cols) { endCol = 0; endRow++; }
                endCol++;
            }
            rowShift = System.Math.Max(0, endRow - (screen.Rows - 1));
        }

        using (context.PushTransform(Matrix.CreateTranslation(0, -rowShift * _cellH)))
        {
            // Draw row by row, batching consecutive same-attribute cells into
            // a single "run" to reduce draw calls and keep BG fills contiguous.
            for (int y = 0; y < screen.Rows; y++)
            {
                int x = 0;
                while (x < screen.Cols)
                {
                    var startAttr = screen[x, y].Attr;
                    int runStart = x;
                    int runEnd = x;
                    while (runEnd < screen.Cols && screen[runEnd, y].Attr == startAttr)
                        runEnd++;

                    DrawRun(context, screen, runStart, runEnd, y, startAttr);
                    x = runEnd;
                }
            }

            // Paint the buffered overlay at the cursor, wrapping onto the
            // next line at the right edge. The cell grid behind it is
            // unchanged — when the user hits Enter and the server echoes the
            // line back, the echo writes real cells over the overlay area.
            // The shared scroll transform above keeps a bottom-row caret and
            // its wrapped tail on-screen.
            if (overlayText is not null)
            {
                int col = overlayStartCol;
                int row = overlayStartRow;
                foreach (char ch in overlayText)
                {
                    if (col >= screen.Cols)
                    {
                        // Wrap to the next line so a long buffer keeps
                        // rendering instead of clipping at the right edge.
                        col = 0;
                        row++;
                    }
                    double px = col * _cellW;
                    double py = row * _cellH;
                    // Black BG fill first so any server-painted cells
                    // underneath don't bleed through, then the glyph in the
                    // prompt foreground so the overlay reads inline.
                    context.FillRectangle(Brushes.Black,
                        new Rect(px, py, _cellW, _cellH));
                    var glyph = new FormattedText(
                        ch.ToString(),
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        _typeface,
                        FontSize,
                        Brushes.LightGray);
                    context.DrawText(glyph, new Point(px, py));
                    col++;
                }
                // Caret tracks the END of the LIVE buffer overlay (mode 1).
                // For pending overlay (mode 2) the caret stays at the
                // current cursor — the buffer is empty so the next typed
                // char would land there, NOT at the end of the pending
                // ghost text.
                if (InputBuffer is { Length: > 0 })
                {
                    caretCol = col;
                    caretRow = row;
                }
            }

            // Cursor caret — a thin horizontal bar at the bottom of its cell,
            // shown only when the screen says it's visible AND the blink is
            // "on". Position is the END of the buffer overlay (if any) so the
            // caret sits where the next typed char will land. The row test is
            // against the post-scroll position so a caret pushed to the
            // bottom line still draws.
            if (screen.CursorVisible && _cursorBlinkOn && caretRow - rowShift < screen.Rows)
            {
                var cx = caretCol * _cellW;
                var cy = caretRow * _cellH;
                // Buffer-full hint: when the local buffer is at the wire
                // cap (254 chars) the caret colour shifts so the user can
                // see further keystrokes are being dropped on the floor.
                IBrush caretBrush = InputBuffer is { IsFull: true } ? Brushes.OrangeRed : Brushes.LightGray;
                context.FillRectangle(caretBrush,
                    new Rect(cx, cy + _cellH * 0.85, _cellW, _cellH * 0.15));
            }
        }
    }

    // Render one horizontal run of same-attribute cells.
    private void DrawRun(DrawingContext context, TerminalScreen screen, int x0, int x1, int y, CellAttributes attr)
    {
        bool reverse = (attr.Flags & CellFlags.Reverse) != 0;
        bool bold = (attr.Flags & CellFlags.Bold) != 0;

        // Reverse video: just swap which color goes to fg vs bg.
        var fgColor = reverse ? attr.Background : attr.Foreground;
        var bgColor = reverse ? attr.Foreground : attr.Background;

        uint fgArgb = reverse
            ? AnsiPalette.ResolveBackground(fgColor)
            : AnsiPalette.ResolveForeground(fgColor, bold);
        uint bgArgb = reverse
            ? AnsiPalette.ResolveForeground(bgColor, bold)
            : AnsiPalette.ResolveBackground(bgColor);

        var fg = ToBrush(fgArgb);
        var bg = ToBrush(bgArgb);

        double left = x0 * _cellW;
        double top = y * _cellH;
        double width = (x1 - x0) * _cellW;
        // Single fill for the whole run's background.
        context.FillRectangle(bg, new Rect(left, top, width, _cellH));

        // SGR 8 — concealed: fill bg only; skip glyphs.
        if ((attr.Flags & CellFlags.Concealed) != 0) return;

        // Draw each cell individually at its exact pixel-aligned position.
        // Drawing a run as one FormattedText lets the font's advance widths
        // drift the glyph row away from the cell grid by fractions of a pixel,
        // which manifests as the visible "color bleed" between cells.
        var typeface = bold ? new Typeface(FontFamily, FontStyle.Normal, FontWeight.Bold) : _typeface;
        for (int i = x0; i < x1; i++)
        {
            char ch = screen[i, y].Char;
            if (ch == ' ') continue;
            var ft = new FormattedText(ch.ToString(), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, FontSize, fg);
            context.DrawText(ft, new Point(x0 == i ? left : i * _cellW, top));
        }

        // Underline — draw a 1px line along the bottom of the run.
        if ((attr.Flags & CellFlags.Underline) != 0)
            context.FillRectangle(fg, new Rect(left, top + _cellH - 1, width, 1));
    }

    private static IBrush ToBrush(uint argb)
    {
        var (r, g, b) = AnsiPalette.ToRgb(argb);
        return new ImmutableSolidColorBrush(Color.FromRgb(r, g, b));
    }

    // ----- Input ---------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Macro first — if the chord matches a user-defined keybind,
        // fire it via the dispatcher and consume the event so neither
        // MapKey nor OnTextInput see the keystroke. The dispatcher
        // returns false when no macro matches OR no sender is bound
        // yet (pre-telnet-connection), letting us fall through to the
        // regular terminal path.
        if (FujinTerm.Services.AppServices.Current.MacroDispatcher
                .TryHandleKey(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }

        // Local-line-edit intercept. Enter flushes the buffer + CR;
        // Backspace pops the last buffered char (and consumes the
        // event regardless so we never send 0x08 to the wire when in
        // line mode — per user: backspace just erases the buffer). Up /
        // Down recall previously-sent commands into the buffer. The
        // remaining special keys (Left/Right, F-keys, Ctrl+letter, Tab,
        // Escape) pass straight through via MapKey because they're
        // meaningful to the server immediately (login prompts, menu
        // navigation) and aren't part of any "line" the user is
        // composing. In character-mode (full-screen forms) the buffer is
        // bypassed entirely — Enter/Backspace/arrows fall through to
        // MapKey so the server's form reads each keystroke as it lands.
        if (InputBuffer is { CharacterMode: false } buf)
        {
            if (e.Key == Key.Enter)
            {
                // Capture what we're flushing + where it's drawn so the
                // pending-overlay path can keep it visible until the
                // server's echo arrives. Skip the capture when the
                // buffer is empty (lone-CR Enter has no visual to
                // preserve) or when the emulator isn't ready yet.
                if (buf.Length > 0 && Emulator is { } em)
                {
                    _pendingFlushText = buf.Text;
                    _pendingFlushRow  = em.Screen.CursorY;
                    _pendingFlushCol  = em.Screen.CursorX;
                }
                // Record the typed line for Up/Down recall before the
                // flush clears the buffer; blank lines are dropped by
                // CommandHistory.Record itself.
                FujinTerm.Services.AppServices.Current.CommandHistory.Record(buf.Text);
                HistoryNav.Reset();
                UserInput?.Invoke(buf.FlushBytes());
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Back)
            {
                buf.Backspace();
                e.Handled = true;
                return;
            }
            // Up / Down recall a previously-sent command into the buffer.
            // History navigation is reserved for line-mode; full-screen
            // forms (CharacterMode) skip this whole block, so their raw
            // CSI arrows still flow through MapKey below. Consumed even
            // with empty history so the arrow never leaks to the wire
            // mid-compose.
            if (e.Key == Key.Up || e.Key == Key.Down)
            {
                string? recalled = e.Key == Key.Up
                    ? HistoryNav.Previous(buf.Text)
                    : HistoryNav.Next();
                if (recalled is not null)
                {
                    buf.Set(recalled);
                    InvalidateVisual();
                }
                e.Handled = true;
                return;
            }
        }

        // Map special keys to escape sequences first; printable text is
        // delivered through OnTextInput instead.
        var bytes = MapKey(e);
        if (bytes is not null)
        {
            UserInput?.Invoke(bytes);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        // Line-mode: route the typed chars into the local buffer
        // instead of straight to the wire. The render overlay paints
        // the buffer at the cursor on the next invalidation. Capped
        // silently at LocalInputBuffer.MaxLength (254 — MUD wire-level
        // line cap); chars past the cap are dropped. In character-mode
        // (full-screen forms) we fall through to the straight-to-wire
        // path so the server echoes each char itself.
        if (InputBuffer is { CharacterMode: false } buf)
        {
            buf.Append(e.Text);
            // Typing a fresh char abandons history browsing — the next Up
            // starts again from the newest entry.
            HistoryNav.Reset();
            e.Handled = true;
            return;
        }
        // Char-mode path (no buffer bound, OR LocalInputBuffer suspended
        // for a full-screen form). BBSes expect Latin-1 / 8-bit bytes,
        // not UTF-8. Encoding here keeps accented characters legible to
        // older servers.
        var bytes = System.Text.Encoding.Latin1.GetBytes(e.Text);
        UserInput?.Invoke(bytes);
        e.Handled = true;
    }

    // Translate non-text key presses into the byte sequence a real terminal
    // would emit. Returns null for keys we don't handle; OnTextInput will pick
    // up regular characters.
    private static byte[]? MapKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: return new byte[] { 0x0D };
            case Key.Back: return new byte[] { 0x08 };
            case Key.Delete: return new byte[] { 0x7F };
            case Key.Escape: return new byte[] { 0x1B };
            case Key.Tab: return new byte[] { 0x09 };
            // Arrow / navigation keys — standard CSI sequences.
            case Key.Up: return new byte[] { 0x1B, (byte)'[', (byte)'A' };
            case Key.Down: return new byte[] { 0x1B, (byte)'[', (byte)'B' };
            case Key.Right: return new byte[] { 0x1B, (byte)'[', (byte)'C' };
            case Key.Left: return new byte[] { 0x1B, (byte)'[', (byte)'D' };
            case Key.Home: return new byte[] { 0x1B, (byte)'[', (byte)'H' };
            case Key.End: return new byte[] { 0x1B, (byte)'[', (byte)'F' };
            case Key.PageUp: return new byte[] { 0x1B, (byte)'[', (byte)'5', (byte)'~' };
            case Key.PageDown: return new byte[] { 0x1B, (byte)'[', (byte)'6', (byte)'~' };
            // F1–F4 use the older "SS3" form expected by most BBS software.
            case Key.F1: return new byte[] { 0x1B, (byte)'O', (byte)'P' };
            case Key.F2: return new byte[] { 0x1B, (byte)'O', (byte)'Q' };
            case Key.F3: return new byte[] { 0x1B, (byte)'O', (byte)'R' };
            case Key.F4: return new byte[] { 0x1B, (byte)'O', (byte)'S' };
        }

        // Ctrl+A..Z → control bytes 0x01..0x1A, the classic terminal
        // "control character" mapping.
        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            if (e.Key >= Key.A && e.Key <= Key.Z)
                return new byte[] { (byte)((int)e.Key - (int)Key.A + 1) };
        }

        return null;
    }
}
