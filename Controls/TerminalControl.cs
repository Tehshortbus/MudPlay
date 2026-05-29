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

/// <summary>
/// Custom Avalonia control that draws the terminal grid and forwards
/// keyboard input back out to the view-model.
///
/// Rendering pipeline:
///   1. Compute per-cell pixel size from the chosen monospace font.
///   2. For each row, walk left-to-right grouping consecutive cells that
///      share the same attributes into "runs" (single fill + per-glyph draw).
///   3. After all cells are drawn, paint the cursor caret if visible.
///
/// Input pipeline:
///   • OnTextInput catches normal printable text and posts the bytes
///     (Latin-1 encoded) to <see cref="UserInput"/>.
///   • OnKeyDown maps non-text keys (arrows, Enter, Ctrl+letter, F1–F4) to
///     the matching ANSI/VT escape sequences and emits those.
/// </summary>
public sealed class TerminalControl : Control
{
    /// <summary>The emulator whose screen we render. Bound from XAML.</summary>
    public static readonly StyledProperty<TerminalEmulator?> EmulatorProperty =
        AvaloniaProperty.Register<TerminalControl, TerminalEmulator?>(nameof(Emulator));

    /// <summary>Bitmap-style monospace font; defaults to embedded MX437.</summary>
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

    /// <summary>Raised on the UI thread with bytes to send to the host.</summary>
    public event Action<byte[]>? UserInput;

    private Typeface _typeface;
    private double _cellW = 8;
    private double _cellH = 16;
    private bool _cursorBlinkOn = true;
    private DispatcherTimer? _blinkTimer;

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
    // UI thread.
    private void OnScreenUpdated() => Dispatcher.UIThread.Post(InvalidateVisual);

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
        Focus();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _blinkTimer?.Stop();
        _blinkTimer = null;
    }

    /// <summary>
    /// Measure the width and height of the chosen font's "M" glyph and
    /// snap the result to whole pixels. Used as the per-cell box size.
    /// </summary>
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

    /// <summary>Tell layout the control wants exactly cols × rows × cell pixels.</summary>
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

        // Draw row by row, batching consecutive same-attribute cells into
        // a single "run" to reduce draw calls and keep BG fills contiguous.
        var screen = em.Screen;
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

        // Cursor caret — a thin horizontal bar at the bottom of its cell,
        // shown only when the screen says it's visible AND the blink is "on".
        if (screen.CursorVisible && _cursorBlinkOn)
        {
            var cx = screen.CursorX * _cellW;
            var cy = screen.CursorY * _cellH;
            context.FillRectangle(Brushes.LightGray,
                new Rect(cx, cy + _cellH * 0.85, _cellW, _cellH * 0.15));
        }
    }

    /// <summary>Render one horizontal run of same-attribute cells.</summary>
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
        // BBSes expect Latin-1 / 8-bit bytes, not UTF-8. Encoding here keeps
        // accented characters legible to older servers.
        var bytes = System.Text.Encoding.Latin1.GetBytes(e.Text);
        UserInput?.Invoke(bytes);
        e.Handled = true;
    }

    /// <summary>
    /// Translate non-text key presses into the byte sequence a real terminal
    /// would emit. Returns null for keys we don't handle; OnTextInput will
    /// pick up regular characters.
    /// </summary>
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
