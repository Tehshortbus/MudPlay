using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using FujinTerm.Game.Map;

namespace FujinTerm.Controls;

/// <summary>
/// BFS-planar room map rendering for the Phase 7 Navigation window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rendering style</b> — modeled on MudProxy's <c>MapRenderer</c>:
/// per layout coord we draw a slightly darker "tile" rectangle, then
/// short exit stubs from the tile centre to each tile edge (one stub
/// per direction the source room has, sourced from
/// <see cref="RoomLayout.EdgesFromCoord"/>), then a smaller room-node
/// rectangle centred in the tile. Adjacent tiles' stubs meet at the
/// shared edge to form a continuous visual line; exits to rooms that
/// fell off-grid still render as a stub from the source side, which
/// matches MudProxy.
/// </para>
/// <para>
/// <b>Input</b> — plain left-button drag pans the view; a mouse-wheel
/// notch zooms about the cursor. A left-button release without
/// movement is treated as a click on the underlying room (used by
/// the loop-builder mode); right-click opens the host's context menu.
/// </para>
/// </remarks>
public sealed class MapControl : Control
{
    public static readonly StyledProperty<RoomLayout?> LayoutProperty =
        AvaloniaProperty.Register<MapControl, RoomLayout?>(nameof(Layout));

    public static readonly StyledProperty<RoomKey?> CurrentRoomKeyProperty =
        AvaloniaProperty.Register<MapControl, RoomKey?>(nameof(CurrentRoomKey));

    public static readonly StyledProperty<RoomGraphManager?> GraphProperty =
        AvaloniaProperty.Register<MapControl, RoomGraphManager?>(nameof(Graph));

    public static readonly StyledProperty<bool> HighlightLairsProperty =
        AvaloniaProperty.Register<MapControl, bool>(nameof(HighlightLairs), defaultValue: true);

    public static readonly StyledProperty<bool> HighlightShopsProperty =
        AvaloniaProperty.Register<MapControl, bool>(nameof(HighlightShops), defaultValue: true);

    public static readonly StyledProperty<bool> HighlightSpellsProperty =
        AvaloniaProperty.Register<MapControl, bool>(nameof(HighlightSpells), defaultValue: true);

    public RoomLayout? Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public RoomKey? CurrentRoomKey
    {
        get => GetValue(CurrentRoomKeyProperty);
        set => SetValue(CurrentRoomKeyProperty, value);
    }

    public RoomGraphManager? Graph
    {
        get => GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
    }

    public bool HighlightLairs
    {
        get => GetValue(HighlightLairsProperty);
        set => SetValue(HighlightLairsProperty, value);
    }

    public bool HighlightShops
    {
        get => GetValue(HighlightShopsProperty);
        set => SetValue(HighlightShopsProperty, value);
    }

    public bool HighlightSpells
    {
        get => GetValue(HighlightSpellsProperty);
        set => SetValue(HighlightSpellsProperty, value);
    }

    // ----- view-state ------------------------------------------------

    /// <summary>World tile size in layout units. Multiplied by <see cref="_zoom"/> to get screen pixels.</summary>
    private const double TileWorldSize = 24.0;

    private double _zoom = 1.2;
    private double _panX;
    private double _panY;

    // Left-button drag/click disambiguation.
    private bool _leftPressed;
    private bool _isDragging;
    private Point _pressPos;
    private double _panStartX;
    private double _panStartY;
    private const double DragThresholdPixels = 4.0;

    // ----- brushes (cached) -----------------------------------------

    private static readonly IBrush Bg            = new SolidColorBrush(Color.Parse("#0E0E0E"));
    private static readonly IBrush TileBg        = new SolidColorBrush(Color.Parse("#1E1E1E"));
    private static readonly IBrush RoomFill      = new SolidColorBrush(Color.Parse("#9B9B9B"));
    private static readonly IBrush CurrentFill   = new SolidColorBrush(Color.Parse("#F8B500"));
    private static readonly IBrush LairFill      = new SolidColorBrush(Color.Parse("#A05F8C"));
    private static readonly IBrush ShopFill      = new SolidColorBrush(Color.Parse("#5F8DA8"));
    private static readonly IBrush SpellFill     = new SolidColorBrush(Color.Parse("#6428A0"));

    private static readonly IPen   TileBorderPen = new Pen(new SolidColorBrush(Color.Parse("#2A2A2A")), 1.0);
    private static readonly IPen   ExitPen       = new Pen(new SolidColorBrush(Color.Parse("#C0C0C0")), 2.0);
    private static readonly IPen   TrapPen       = new Pen(new SolidColorBrush(Color.Parse("#DC3C3C")), 2.0);
    private static readonly IPen   RoomBorderPen = new Pen(new SolidColorBrush(Color.Parse("#D0D0D0")), 1.0);
    private static readonly IPen   CurrentPen    = new Pen(new SolidColorBrush(Color.Parse("#FFD24D")), 2.0);
    private static readonly IPen   LairBorderPen  = new Pen(new SolidColorBrush(Color.Parse("#C77FAC")), 1.5);
    private static readonly IPen   ShopBorderPen  = new Pen(new SolidColorBrush(Color.Parse("#7FB0CC")), 1.5);
    private static readonly IPen   SpellBorderPen = new Pen(new SolidColorBrush(Color.Parse("#9C70CC")), 1.5);

    // ----- lifecycle -------------------------------------------------

    public MapControl()
    {
        Focusable = true;
        ClipToBounds = true;
        AffectsRender<MapControl>(LayoutProperty, CurrentRoomKeyProperty, GraphProperty,
            HighlightLairsProperty, HighlightShopsProperty, HighlightSpellsProperty);
    }

    public event Action<RoomKey, Point>? RoomRightClicked;
    public event Action<RoomKey, Point>? RoomLeftClicked;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // Zoom about the cursor: keep the world point under the
        // cursor fixed in screen space across the zoom transition.
        Point cursor = e.GetPosition(this);
        double zoomBefore = _zoom;
        double factor = e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1;
        double zoomAfter = Math.Clamp(zoomBefore * factor, 0.4, 4.0);
        if (Math.Abs(zoomAfter - zoomBefore) < 1e-6) return;

        // Reverse-project the cursor into world space at the old zoom,
        // re-project at the new zoom, and adjust pan so the cursor's
        // world point lands at the same screen pixel.
        double cxOld = (cursor.X - Bounds.Width  / 2 - _panX) / (TileWorldSize * zoomBefore);
        double cyOld = (cursor.Y - Bounds.Height / 2 - _panY) / (TileWorldSize * zoomBefore);
        _zoom = zoomAfter;
        _panX = cursor.X - Bounds.Width  / 2 - cxOld * TileWorldSize * _zoom;
        _panY = cursor.Y - Bounds.Height / 2 - cyOld * TileWorldSize * _zoom;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        PointerPoint point = e.GetCurrentPoint(this);

        if (point.Properties.IsLeftButtonPressed)
        {
            _leftPressed = true;
            _isDragging = false;
            _pressPos = point.Position;
            _panStartX = _panX;
            _panStartY = _panY;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsRightButtonPressed
            && TryHitTestRoom(point.Position, out RoomKey hit))
        {
            RoomRightClicked?.Invoke(hit, point.Position);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_leftPressed) return;

        Point now = e.GetPosition(this);
        double dx = now.X - _pressPos.X;
        double dy = now.Y - _pressPos.Y;

        if (!_isDragging
            && dx * dx + dy * dy >= DragThresholdPixels * DragThresholdPixels)
        {
            _isDragging = true;
        }

        if (_isDragging)
        {
            _panX = _panStartX + dx;
            _panY = _panStartY + dy;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_leftPressed) return;

        bool wasDragging = _isDragging;
        Point releasePos = e.GetPosition(this);
        _leftPressed = false;
        _isDragging = false;
        e.Pointer.Capture(null);

        if (!wasDragging && TryHitTestRoom(releasePos, out RoomKey hit))
        {
            RoomLeftClicked?.Invoke(hit, releasePos);
        }
        e.Handled = true;
    }

    /// <summary>Re-centre the view on the current room.</summary>
    public void FitToCurrent()
    {
        if (Layout is null) return;
        RoomKey origin = CurrentRoomKey ?? Layout.Origin;
        if (!Layout.Positions.TryGetValue(origin, out (int X, int Y) coord))
        {
            _panX = 0;
            _panY = 0;
        }
        else
        {
            _panX = -coord.X * TileWorldSize * _zoom;
            _panY = -coord.Y * TileWorldSize * _zoom;
        }
        InvalidateVisual();
    }

    // ----- render ----------------------------------------------------

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Bg, new Rect(Bounds.Size));

        if (Layout is null || Layout.CoordToRoom.Count == 0)
        {
            DrawCenteredMessage(context, "No room data loaded. Import game data first.");
            return;
        }

        double tilePixels = TileWorldSize * _zoom;
        if (tilePixels < 4) return;

        double cx = Bounds.Width  / 2 + _panX;
        double cy = Bounds.Height / 2 + _panY;
        Rect viewport = new(Bounds.Size);

        foreach (KeyValuePair<(int X, int Y), RoomKey> kvp in Layout.CoordToRoom)
        {
            (int gx, int gy) = kvp.Key;
            double centerX = cx + gx * tilePixels;
            double centerY = cy + gy * tilePixels;
            Rect cell = new(
                centerX - tilePixels / 2,
                centerY - tilePixels / 2,
                tilePixels,
                tilePixels);

            // Cull off-screen cells.
            if (!cell.Intersects(viewport)) continue;

            // 1. Cell background + faint grid border.
            context.FillRectangle(TileBg, cell);
            context.DrawRectangle(null, TileBorderPen, cell);

            // 2. Exit stubs — one per direction in EdgesFromCoord[here].
            DrawExitStubs(context, cell, kvp.Key);

            // 3. Room node (smaller centered rectangle).
            DrawRoomNode(context, cell, kvp.Value);
        }
    }

    private void DrawExitStubs(DrawingContext ctx, Rect cell, (int X, int Y) coord)
    {
        if (Layout is null) return;

        HashSet<Direction>? trapDirs = null;
        if (Layout.TrapEdgesFromCoord.TryGetValue(coord, out IReadOnlySet<Direction>? trapSet))
            trapDirs = new HashSet<Direction>(trapSet);

        if (!Layout.EdgesFromCoord.TryGetValue(coord, out IReadOnlySet<Direction>? dirs))
            return;

        double midX = cell.X + cell.Width  / 2;
        double midY = cell.Y + cell.Height / 2;

        foreach (Direction dir in dirs)
        {
            bool isTrap = trapDirs is not null && trapDirs.Contains(dir);
            IPen pen = isTrap ? TrapPen : ExitPen;
            DrawStub(ctx, pen, cell, midX, midY, dir);
        }
    }

    private static void DrawStub(DrawingContext ctx, IPen pen, Rect cell, double mx, double my, Direction dir)
    {
        switch (dir)
        {
            case Direction.N:  ctx.DrawLine(pen, new Point(mx, my), new Point(mx, cell.Top));    break;
            case Direction.S:  ctx.DrawLine(pen, new Point(mx, my), new Point(mx, cell.Bottom)); break;
            case Direction.E:  ctx.DrawLine(pen, new Point(mx, my), new Point(cell.Right, my)); break;
            case Direction.W:  ctx.DrawLine(pen, new Point(mx, my), new Point(cell.Left,  my)); break;
            case Direction.NE: ctx.DrawLine(pen, new Point(mx, my), new Point(cell.Right, cell.Top));    break;
            case Direction.NW: ctx.DrawLine(pen, new Point(mx, my), new Point(cell.Left,  cell.Top));    break;
            case Direction.SE: ctx.DrawLine(pen, new Point(mx, my), new Point(cell.Right, cell.Bottom)); break;
            case Direction.SW: ctx.DrawLine(pen, new Point(mx, my), new Point(cell.Left,  cell.Bottom)); break;
            // U / D — not planar, not rendered as stubs.
        }
    }

    private void DrawRoomNode(DrawingContext ctx, Rect cell, RoomKey key)
    {
        double nodeSize = Math.Max(cell.Width * 0.45, 3.0);
        double nx = cell.X + (cell.Width  - nodeSize) / 2;
        double ny = cell.Y + (cell.Height - nodeSize) / 2;
        Rect node = new(nx, ny, nodeSize, nodeSize);

        bool isCurrent = CurrentRoomKey is { } current && current.Equals(key);
        Room? room = Graph?.GetRoom(key);

        IBrush fill;
        IPen pen;
        if (isCurrent)
        {
            fill = CurrentFill;
            pen = CurrentPen;
        }
        else if (HighlightLairs && room is { HasLair: true })
        {
            fill = LairFill;
            pen = LairBorderPen;
        }
        else if (HighlightShops && room is { Shop: > 0 })
        {
            fill = ShopFill;
            pen = ShopBorderPen;
        }
        else if (HighlightSpells && room is { Spell: > 0 })
        {
            fill = SpellFill;
            pen = SpellBorderPen;
        }
        else
        {
            fill = RoomFill;
            pen = RoomBorderPen;
        }

        ctx.FillRectangle(fill, node);
        ctx.DrawRectangle(null, pen, node);

        if (isCurrent)
        {
            // Outer yellow ring on the cell so the current room reads
            // even when the user has zoomed out past the node size.
            Rect ring = cell.Deflate(2);
            ctx.DrawRectangle(null, CurrentPen, ring);
        }
    }

    private void DrawCenteredMessage(DrawingContext ctx, string text)
    {
        Typeface tf = new("Inter");
        FormattedText ft = new(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, tf, 12,
            new SolidColorBrush(Color.Parse("#888")));
        Point p = new(
            (Bounds.Width  - ft.Width)  / 2,
            (Bounds.Height - ft.Height) / 2);
        ctx.DrawText(ft, p);
    }

    private bool TryHitTestRoom(Point position, out RoomKey hit)
    {
        hit = default;
        if (Layout is null) return false;

        double tilePixels = TileWorldSize * _zoom;
        double half = tilePixels / 2;
        double cx = Bounds.Width  / 2 + _panX;
        double cy = Bounds.Height / 2 + _panY;

        // Inverse-project the screen point into grid coords.
        int gx = (int)Math.Round((position.X - cx) / tilePixels);
        int gy = (int)Math.Round((position.Y - cy) / tilePixels);
        double centerX = cx + gx * tilePixels;
        double centerY = cy + gy * tilePixels;
        if (Math.Abs(position.X - centerX) <= half
            && Math.Abs(position.Y - centerY) <= half
            && Layout.CoordToRoom.TryGetValue((gx, gy), out hit))
        {
            return true;
        }
        return false;
    }
}
