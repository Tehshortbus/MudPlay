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
/// Consumes a <see cref="RoomLayout"/> produced by
/// <see cref="BfsMapper.BuildLayout"/> and draws each placed room as
/// a filled rectangle with edges between adjacent rooms.
/// </summary>
/// <remarks>
/// <para>
/// PR 7.11 ships the basic renderer: room cells + planar edges +
/// current-room highlight + trap-exit colouring + lair-room marker.
/// Pan with middle-button drag (also Shift+left-button for trackpad
/// users); zoom with the mouse wheel. The off-grid lane and the
/// vertical-glyph rendering (U/D) land in a follow-up.
/// </para>
/// <para>
/// Inspired by MudProxy's <c>MapViewDialog.cs</c> render loop in
/// shape only — we use Avalonia's <c>DrawingContext</c> APIs natively
/// and avoid MudProxy's per-tile palette caching since our grid is
/// far smaller than its scrollback-driven canvas.
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

    // ----- view-state -----------------------------------------------

    private const double CellSize = 22.0;
    private const double CellSpacing = 16.0;
    private const double CellSide = CellSize;
    private const double Stride = CellSize + CellSpacing;

    private double _zoom = 1.2;
    private double _panX;
    private double _panY;

    private bool _isPanning;
    private Point _panStart;
    private double _panStartX;
    private double _panStartY;

    // ----- brushes (cached) -----------------------------------------

    private static readonly IBrush Bg          = new SolidColorBrush(Color.Parse("#0E0E0E"));
    private static readonly IBrush RoomFill    = new SolidColorBrush(Color.Parse("#3A3A3A"));
    private static readonly IBrush RoomBorder  = new SolidColorBrush(Color.Parse("#555"));
    private static readonly IBrush CurrentFill = new SolidColorBrush(Color.Parse("#F8B500"));
    private static readonly IBrush LairFill    = new SolidColorBrush(Color.Parse("#A85FA8"));
    private static readonly IBrush ShopFill    = new SolidColorBrush(Color.Parse("#5F8DA8"));
    private static readonly IBrush EdgePlain   = new SolidColorBrush(Color.Parse("#3A3A3A"));
    private static readonly IBrush EdgeDoor    = new SolidColorBrush(Color.Parse("#5F8DA8"));
    private static readonly IBrush EdgeTrap    = new SolidColorBrush(Color.Parse("#F25C54"));

    private static readonly IPen EdgePlainPen = new Pen(EdgePlain, 1.5);
    private static readonly IPen EdgeDoorPen  = new Pen(EdgeDoor,  1.5);
    private static readonly IPen EdgeTrapPen  = new Pen(EdgeTrap,  2.0);
    private static readonly IPen CurrentPen   = new Pen(new SolidColorBrush(Color.Parse("#F8B500")), 2.0);

    // ----- lifecycle ------------------------------------------------

    public MapControl()
    {
        Focusable = true;
        ClipToBounds = true;
        AffectsRender<MapControl>(LayoutProperty, CurrentRoomKeyProperty, GraphProperty);
    }

    /// <summary>
    /// Fired when the user right-clicks a placed room cell. Carries
    /// the hit room key and the pointer position (in control-local
    /// coordinates) so the host can open a context menu at the click.
    /// </summary>
    public event Action<RoomKey, Point>? RoomRightClicked;

    /// <summary>Fired on a left-click of a placed room cell (used by the loop-builder mode in PR 7.15).</summary>
    public event Action<RoomKey, Point>? RoomLeftClicked;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double factor = e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1;
        _zoom = Math.Clamp(_zoom * factor, 0.4, 4.0);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        PointerPoint point = e.GetCurrentPoint(this);
        bool middleDown = point.Properties.IsMiddleButtonPressed;
        bool shiftLeftDown = point.Properties.IsLeftButtonPressed
            && (e.KeyModifiers & KeyModifiers.Shift) != 0;
        if (middleDown || shiftLeftDown)
        {
            _isPanning = true;
            _panStart = point.Position;
            _panStartX = _panX;
            _panStartY = _panY;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // Hit-test the room cells. Right-click opens the context menu
        // via the host's RoomRightClicked handler; left-click is the
        // generic select / add-to-loop signal.
        if (TryHitTestRoom(point.Position, out RoomKey hit))
        {
            if (point.Properties.IsRightButtonPressed)
                RoomRightClicked?.Invoke(hit, point.Position);
            else if (point.Properties.IsLeftButtonPressed)
                RoomLeftClicked?.Invoke(hit, point.Position);
            e.Handled = true;
        }
    }

    private bool TryHitTestRoom(Point position, out RoomKey hit)
    {
        hit = default;
        if (Layout is null) return false;

        double cell = CellSide * _zoom;
        double stride = Stride * _zoom;
        double cx = Bounds.Width  / 2 + _panX;
        double cy = Bounds.Height / 2 + _panY;
        double half = cell / 2;

        foreach (KeyValuePair<RoomKey, (int X, int Y)> kvp in Layout.Positions)
        {
            (int gx, int gy) = kvp.Value;
            double x = cx + gx * stride - half;
            double y = cy + gy * stride - half;
            if (position.X >= x && position.X <= x + cell
                && position.Y >= y && position.Y <= y + cell)
            {
                hit = kvp.Key;
                return true;
            }
        }
        return false;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isPanning) return;
        Point now = e.GetPosition(this);
        _panX = _panStartX + (now.X - _panStart.X);
        _panY = _panStartY + (now.Y - _panStart.Y);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    /// <summary>Re-centre the view on the current room (called by the toolbar's Fit button when wired).</summary>
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
            // Place the origin at the centre of the control.
            _panX = Bounds.Width  / 2 - coord.X * Stride * _zoom;
            _panY = Bounds.Height / 2 - coord.Y * Stride * _zoom;
        }
        InvalidateVisual();
    }

    // ----- render ---------------------------------------------------

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Bg, new Rect(Bounds.Size));

        if (Layout is null || Layout.Positions.Count == 0) return;

        double cell = CellSide * _zoom;
        double stride = Stride * _zoom;
        double cx = Bounds.Width  / 2 + _panX;
        double cy = Bounds.Height / 2 + _panY;

        // Edges first so room cells overlap them cleanly.
        DrawEdges(context, cell, stride, cx, cy);

        // Room cells.
        foreach (KeyValuePair<RoomKey, (int X, int Y)> kvp in Layout.Positions)
        {
            (int gx, int gy) = kvp.Value;
            double x = cx + gx * stride - cell / 2;
            double y = cy + gy * stride - cell / 2;
            Rect rect = new(x, y, cell, cell);

            bool isCurrent = CurrentRoomKey is { } current && current.Equals(kvp.Key);
            Room? room = Graph?.GetRoom(kvp.Key);
            IBrush fill = isCurrent ? CurrentFill
                        : room is { HasLair: true } ? LairFill
                        : room is { Shop: > 0 } ? ShopFill
                        : RoomFill;

            context.FillRectangle(fill, rect);
            context.DrawRectangle(null, isCurrent ? CurrentPen
                : new Pen(RoomBorder, 1.0), rect);
        }
    }

    private void DrawEdges(DrawingContext context, double cell, double stride, double cx, double cy)
    {
        if (Graph is null) return;

        // Draw each (room → exit) line once. The Layout positions
        // tell us where the room sits; the Graph tells us which
        // neighbours exist and what hint each exit carries.
        foreach (KeyValuePair<RoomKey, (int X, int Y)> kvp in Layout!.Positions)
        {
            Room? room = Graph.GetRoom(kvp.Key);
            if (room is null) continue;
            (int x, int y) = kvp.Value;
            Point from = new(cx + x * stride, cy + y * stride);

            foreach (KeyValuePair<Direction, RoomExit> exit in room.Exits)
            {
                if (!IsPlanar(exit.Key)) continue;
                if (!Layout.Positions.TryGetValue(exit.Value.Target, out (int X, int Y) neighbor)) continue;

                Point to = new(cx + neighbor.X * stride, cy + neighbor.Y * stride);
                IPen pen = exit.Value.Hint switch
                {
                    RoomExitHint.Trap => EdgeTrapPen,
                    RoomExitHint.Door => EdgeDoorPen,
                    _                  => EdgePlainPen,
                };
                context.DrawLine(pen, from, to);
            }
        }
    }

    private static bool IsPlanar(Direction d) =>
        d != Direction.U && d != Direction.D;
}
