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

    public static readonly StyledProperty<IReadOnlyList<RoomKey>?> WalkPathProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyList<RoomKey>?>(nameof(WalkPath));

    /// <summary>
    /// Preview polyline for a queued (but not yet running) walk. Drawn
    /// in red beneath any live <see cref="WalkPath"/> so an active walk
    /// overlays its target without flicker. NavigationViewModel sets
    /// this when a search-result selection arms a destination.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<RoomKey>?> PreviewPathProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyList<RoomKey>?>(nameof(PreviewPath));

    public static readonly StyledProperty<IReadOnlyList<RoomKey>?> LoopPathProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyList<RoomKey>?>(nameof(LoopPath));

    /// <summary>
    /// Live BFS-expanded preview of the loop the user is currently
    /// building in the Navigation window's loop-builder strip. Drawn
    /// underneath any active <see cref="LoopPath"/> / <see cref="WalkPath"/>
    /// so an active automation always overlays the build preview when
    /// both share a segment. Pen is dashed cyan to distinguish from the
    /// solid red preview and the blue active-loop pens.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<RoomKey>?> LoopBuilderPathProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyList<RoomKey>?>(nameof(LoopBuilderPath));

    public static readonly StyledProperty<IReadOnlySet<RoomKey>?> AvoidedRoomsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlySet<RoomKey>?>(nameof(AvoidedRooms));

    public static readonly StyledProperty<IReadOnlyDictionary<RoomKey, int>?> LoopSequenceNumbersProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyDictionary<RoomKey, int>?>(nameof(LoopSequenceNumbers));

    public static readonly StyledProperty<IReadOnlySet<RoomKey>?> AutoLairRoomsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlySet<RoomKey>?>(nameof(AutoLairRooms));

    /// <summary>
    /// Set of rooms with a CMD-driven teleport command (TBInfo Action
    /// chain contains a <c>teleport &lt;r&gt; &lt;m&gt;</c> directive).
    /// Rendered with diagonal cross-hatch lines over the cell fill so
    /// the user can see at a glance which rooms hide a non-exit
    /// movement option (e.g. 1/1182 "use chime" → 1/65).
    /// </summary>
    public static readonly StyledProperty<IReadOnlySet<RoomKey>?> TeleportRoomsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlySet<RoomKey>?>(nameof(TeleportRooms));

    public static readonly StyledProperty<bool> WalkPathIsAutoLairProperty =
        AvaloniaProperty.Register<MapControl, bool>(nameof(WalkPathIsAutoLair));

    public static readonly StyledProperty<RoomKey?> SelectedRoomKeyProperty =
        AvaloniaProperty.Register<MapControl, RoomKey?>(nameof(SelectedRoomKey));

    /// <summary>Walk-to destination — blue-filled with a ring so it's
    /// immediately recognisable as the goal, mirroring the
    /// "you are here" treatment.</summary>
    public static readonly StyledProperty<RoomKey?> DestinationRoomKeyProperty =
        AvaloniaProperty.Register<MapControl, RoomKey?>(nameof(DestinationRoomKey));

    public static readonly StyledProperty<FujinTerm.Models.Profile.KeyChord> UpStepChordProperty =
        AvaloniaProperty.Register<MapControl, FujinTerm.Models.Profile.KeyChord>(nameof(UpStepChord),
            new FujinTerm.Models.Profile.KeyChord(Key.PageUp));

    public static readonly StyledProperty<FujinTerm.Models.Profile.KeyChord> DownStepChordProperty =
        AvaloniaProperty.Register<MapControl, FujinTerm.Models.Profile.KeyChord>(nameof(DownStepChord),
            new FujinTerm.Models.Profile.KeyChord(Key.PageDown));

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

    public IReadOnlyList<RoomKey>? WalkPath
    {
        get => GetValue(WalkPathProperty);
        set => SetValue(WalkPathProperty, value);
    }

    public IReadOnlyList<RoomKey>? PreviewPath
    {
        get => GetValue(PreviewPathProperty);
        set => SetValue(PreviewPathProperty, value);
    }

    public IReadOnlyList<RoomKey>? LoopPath
    {
        get => GetValue(LoopPathProperty);
        set => SetValue(LoopPathProperty, value);
    }

    public IReadOnlyList<RoomKey>? LoopBuilderPath
    {
        get => GetValue(LoopBuilderPathProperty);
        set => SetValue(LoopBuilderPathProperty, value);
    }

    public IReadOnlySet<RoomKey>? AvoidedRooms
    {
        get => GetValue(AvoidedRoomsProperty);
        set => SetValue(AvoidedRoomsProperty, value);
    }

    public IReadOnlyDictionary<RoomKey, int>? LoopSequenceNumbers
    {
        get => GetValue(LoopSequenceNumbersProperty);
        set => SetValue(LoopSequenceNumbersProperty, value);
    }

    public IReadOnlySet<RoomKey>? AutoLairRooms
    {
        get => GetValue(AutoLairRoomsProperty);
        set => SetValue(AutoLairRoomsProperty, value);
    }

    public IReadOnlySet<RoomKey>? TeleportRooms
    {
        get => GetValue(TeleportRoomsProperty);
        set => SetValue(TeleportRoomsProperty, value);
    }

    public bool WalkPathIsAutoLair
    {
        get => GetValue(WalkPathIsAutoLairProperty);
        set => SetValue(WalkPathIsAutoLairProperty, value);
    }

    /// <summary>
    /// Cursor for the keyboard map-crawler. Null = no selection (the
    /// current room is implicitly active when the user first presses
    /// a navigation key). Drawn as a cyan ring around the cell so it
    /// reads distinctly from the amber current-room highlight.
    /// </summary>
    public RoomKey? SelectedRoomKey
    {
        get => GetValue(SelectedRoomKeyProperty);
        set => SetValue(SelectedRoomKeyProperty, value);
    }

    public RoomKey? DestinationRoomKey
    {
        get => GetValue(DestinationRoomKeyProperty);
        set => SetValue(DestinationRoomKeyProperty, value);
    }

    /// <summary>
    /// Fired when the user steps the crawler up or down — the layout
    /// host is expected to rebuild from the new room (which lives on a
    /// different floor and therefore isn't in the current layout).
    /// </summary>
    public event Action<RoomKey>? FloorChangeRequested;

    /// <summary>
    /// Key chord that steps the crawler one floor up. Bound from
    /// the user's macro configured to send <c>u</c> to the game so
    /// the same chord drives both in-game movement and the map
    /// crawler. Defaults to <c>PageUp</c> when the macro isn't bound.
    /// </summary>
    public FujinTerm.Models.Profile.KeyChord UpStepChord
    {
        get => GetValue(UpStepChordProperty);
        set => SetValue(UpStepChordProperty, value);
    }

    public FujinTerm.Models.Profile.KeyChord DownStepChord
    {
        get => GetValue(DownStepChordProperty);
        set => SetValue(DownStepChordProperty, value);
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

    // Hover-tooltip tracking.
    private RoomKey? _hoverRoom;
    private Point _hoverPos;
    private readonly Avalonia.Threading.DispatcherTimer _hoverTimer;
    private const int HoverDelayMs = 250;

    // Auto-follow suppression — after any explicit pan-drag or
    // crawler step, the player-room auto-centre is paused for this
    // many seconds so the user can browse without the view yanking
    // back to live position. The selection-driven centre (crawler
    // step / Home / search jump) is always honoured.
    private DateTime _autoFollowSuppressedUntil = DateTime.MinValue;
    private const int AutoFollowSuppressionSeconds = 10;

    private void SuppressAutoFollow()
        => _autoFollowSuppressedUntil = DateTime.UtcNow.AddSeconds(AutoFollowSuppressionSeconds);

    private bool IsAutoFollowSuppressed
        => DateTime.UtcNow < _autoFollowSuppressedUntil;

    /// <summary>
    /// Fires once the pointer has dwelled over a room cell for
    /// <see cref="HoverDelayMs"/>, AND any time the hovered room
    /// changes. Carries the room key + screen-local pointer position
    /// so the host can position a popup. Null payload means
    /// "no room is being hovered" — host should dismiss the popup.
    /// </summary>
    public event Action<RoomKey?, Point>? RoomHovered;
    private double _panStartX;
    private double _panStartY;
    private const double DragThresholdPixels = 4.0;

    // ----- brushes (cached) -----------------------------------------

    private static readonly IBrush Bg            = new SolidColorBrush(Color.Parse("#0E0E0E"));
    private static readonly IBrush TileBg        = new SolidColorBrush(Color.Parse("#1E1E1E"));
    private static readonly IBrush RoomFill      = new SolidColorBrush(Color.Parse("#9B9B9B"));
    private static readonly IBrush CurrentFill   = new SolidColorBrush(Color.Parse("#F8B500"));
    private static readonly IBrush LairFill      = new SolidColorBrush(Color.Parse("#8E4F7B"));
    private static readonly IBrush ShopFill      = new SolidColorBrush(Color.Parse("#4A7791"));
    private static readonly IBrush SpellFill     = new SolidColorBrush(Color.Parse("#6428A0"));
    // Vertical-exit indicators (MudProxy convention): green = up only,
    // yellow = down only, orange = both. Applied as the room-node fill
    // when no higher-priority highlight (current / auto-lair / lair /
    // shop / spell) takes the cell.
    private static readonly IBrush UpFill        = new SolidColorBrush(Color.Parse("#00C800"));
    private static readonly IBrush DownFill      = new SolidColorBrush(Color.Parse("#DCDC00"));
    private static readonly IBrush UpDownFill    = new SolidColorBrush(Color.Parse("#FFB432"));

    private static readonly IPen   TileBorderPen = new Pen(new SolidColorBrush(Color.Parse("#2A2A2A")), 1.0);
    private static readonly IPen   ExitPen       = new Pen(new SolidColorBrush(Color.Parse("#C0C0C0")), 2.0);
    private static readonly IPen   TrapPen       = new Pen(new SolidColorBrush(Color.Parse("#DC3C3C")), 2.0);
    private static readonly IPen   RoomBorderPen = new Pen(new SolidColorBrush(Color.Parse("#D0D0D0")), 1.0);
    private static readonly IPen   CurrentPen    = new Pen(new SolidColorBrush(Color.Parse("#FFD24D")), 2.0);
    private static readonly IPen   LairBorderPen  = new Pen(new SolidColorBrush(Color.Parse("#B36F9C")), 1.5);
    private static readonly IPen   ShopBorderPen  = new Pen(new SolidColorBrush(Color.Parse("#6A9CB6")), 1.5);
    private static readonly IPen   SpellBorderPen = new Pen(new SolidColorBrush(Color.Parse("#9C70CC")), 1.5);
    private static readonly IPen   UpBorderPen     = new Pen(new SolidColorBrush(Color.Parse("#00A000")), 1.5);
    private static readonly IPen   DownBorderPen   = new Pen(new SolidColorBrush(Color.Parse("#B4B400")), 1.5);
    private static readonly IPen   UpDownBorderPen = new Pen(new SolidColorBrush(Color.Parse("#FFD250")), 1.5);
    private static readonly IPen   SelectionPen   = new Pen(new SolidColorBrush(Color.Parse("#00DDDD")), 2.0);
    // "You are here" overlay for the player's current room — a
    // saturated amber dot drawn over whatever the room-node fill is.
    private static readonly IBrush PlayerDotFill  = new SolidColorBrush(Color.Parse("#FFE03A"));

    // Walk-to destination — solid blue fill with a matching thick ring,
    // visually mirroring the "you are here" amber treatment so the
    // user can spot the goal at a glance.
    // Destination marker — same shape as the player marker (cell fill +
    // node border + thick cell-perimeter ring + centre dot) but in
    // deep royal blue. Chosen darker than the shop blue (#4A7791) so
    // a shop sitting next to the queued destination still reads as a
    // separate room class at a glance.
    private static readonly IBrush DestinationFill    = new SolidColorBrush(Color.Parse("#1A4FB0"));
    private static readonly IPen   DestinationRing    = new Pen(new SolidColorBrush(Color.Parse("#3D6FCA")), 2.0);
    private static readonly IPen   DestinationOuterPen = new Pen(new SolidColorBrush(Color.Parse("#3D6FCA")), 2.5);
    private static readonly IBrush DestinationDotFill = new SolidColorBrush(Color.Parse("#9FC4FF"));
    private static readonly IPen   DestinationDotPen  = new Pen(new SolidColorBrush(Color.Parse("#0A1E40")), 1.5);
    private static readonly IPen   PlayerDotPen   = new Pen(new SolidColorBrush(Color.Parse("#3A1F00")), 1.5);
    private static readonly IPen   PlayerOuterPen = new Pen(new SolidColorBrush(Color.Parse("#FFD24D")), 2.5);
    private static readonly IPen   WalkPathPen    = new Pen(new SolidColorBrush(Color.Parse("#1E64DC")), 3.0)
    {
        LineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };
    private static readonly IPen   LoopPathPen    = new Pen(new SolidColorBrush(Color.Parse("#4C82E6")), 3.0)
    {
        LineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };
    /// <summary>
    /// Solid red "where Run would walk" preview line for queued walk-to
    /// destinations. Drawn beneath the active walk so a live walk
    /// overlays it. Hue distinct from the trap red (<see cref="TrapPen"/>
    /// #DC3C3C) so the user reads them as different signals. Dashed-
    /// red is reserved for future loop previews (per UX direction).
    /// </summary>
    private static readonly IPen   PreviewPathPen = new Pen(new SolidColorBrush(Color.Parse("#E66C5A")), 3.0)
    {
        LineCap  = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };

    /// <summary>
    /// Dashed cyan polyline showing the user's in-progress loop-builder
    /// gap-filled path. Distinct from solid red PreviewPath (queued
    /// walk-to) and solid blue Loop/Walk paths (running automations).
    /// </summary>
    private static readonly IPen   LoopBuilderPen = new Pen(new SolidColorBrush(Color.Parse("#FF50E6FF")), 2.5)
    {
        LineCap     = PenLineCap.Round,
        LineJoin    = PenLineJoin.Round,
        DashStyle   = new DashStyle(new double[] { 3.0, 2.5 }, 0),
    };

    // Cross-hatch overlay for teleport-CMD rooms. Fully-opaque bright
    // cyan with a 1.5 px stroke so the pattern reads at default zoom
    // without disappearing into the cell fill — the prior #B0FFFFFF
    // at 1.0 px was nearly invisible on lair pink and shop blue.
    private static readonly IPen TeleportHashPen
        = new Pen(new SolidColorBrush(Color.Parse("#FF50E6FF")), 1.5);
    private static readonly IPen   AvoidXPen      = new Pen(new SolidColorBrush(Color.Parse("#FF6464")), 2.0)
    {
        LineCap = PenLineCap.Round,
    };
    private static readonly IBrush SeqNumberFill  = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush AutoLairFill   = new SolidColorBrush(Color.Parse("#DC821E"));
    private static readonly IPen   AutoLairBorder = new Pen(new SolidColorBrush(Color.Parse("#FFA500")), 2.0)
    {
        DashStyle = new DashStyle(new double[] { 3, 2 }, 0),
    };
    private static readonly IPen   AutoLairWalkPen = new Pen(new SolidColorBrush(Color.Parse("#DC821E")), 3.0)
    {
        LineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };

    // ----- lifecycle -------------------------------------------------

    public MapControl()
    {
        Focusable = true;
        ClipToBounds = true;
        _hoverTimer = new(TimeSpan.FromMilliseconds(HoverDelayMs),
            Avalonia.Threading.DispatcherPriority.Background,
            (_, _) =>
            {
                _hoverTimer!.Stop();
                if (_hoverRoom is { } k) RoomHovered?.Invoke(k, _hoverPos);
            });
        _hoverTimer.Stop();
        AffectsRender<MapControl>(LayoutProperty, CurrentRoomKeyProperty, DestinationRoomKeyProperty, GraphProperty,
            HighlightLairsProperty, HighlightShopsProperty, HighlightSpellsProperty,
            WalkPathProperty, LoopPathProperty, LoopBuilderPathProperty, AvoidedRoomsProperty,
            LoopSequenceNumbersProperty, AutoLairRoomsProperty, WalkPathIsAutoLairProperty,
            SelectedRoomKeyProperty, PreviewPathProperty, TeleportRoomsProperty);

        // Auto-centre on the player's current room every time it
        // changes (MudProxy's CenterOnRoom rule) — but only when the
        // user isn't actively browsing. Drag-pan and crawler steps
        // both arm a 10-second suppression window during which
        // CurrentRoomKey updates are visually ignored.
        CurrentRoomKeyProperty.Changed.AddClassHandler<MapControl>((c, a) =>
        {
            if (c.IsAutoFollowSuppressed) return;
            if (a.NewValue is RoomKey k) c.CenterOnRoom(k);
        });
        // Selection moves do NOT re-centre on click — clicking a
        // square the user can already see shouldn't yank the view.
        // Keyboard crawler stepping centres explicitly from
        // TryStepSelection / TryStepFloor since those can step off
        // the visible window.
        // When the layout itself rebuilds (new floor / new origin),
        // re-centre on whichever room the host considers active —
        // selection takes precedence so floor-stepping lands on the
        // new room. Layout rebuilds are typically the result of an
        // explicit user gesture (PageUp/Down, search jump) so we
        // ignore the browse-suppression window here.
        LayoutProperty.Changed.AddClassHandler<MapControl>((c, _) =>
        {
            RoomKey? focus = c.SelectedRoomKey ?? c.CurrentRoomKey;
            if (focus is { } k) c.CenterOnRoom(k);
        });
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
        Focus();                                              // grab keyboard focus for the map crawler
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
            // Mirror the crawler outline onto the right-clicked room
            // so the user can see which square the context menu is
            // attached to (the menu can move off-screen on small maps).
            SelectedRoomKey = hit;
            RoomRightClicked?.Invoke(hit, point.Position);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point now = e.GetPosition(this);

        if (_leftPressed)
        {
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

                // Arm the auto-follow suppression window — the user
                // is actively browsing; don't yank back to live
                // player position for the next 10 s.
                SuppressAutoFollow();

                // Hide any open tooltip while dragging — the room
                // under the cursor changes constantly during a pan.
                if (_hoverRoom is not null)
                {
                    _hoverRoom = null;
                    RoomHovered?.Invoke(null, now);
                }
                _hoverTimer.Stop();
                return;
            }
        }

        // Hover hit-testing — fires when the pointer settles over a
        // new room cell. Movement within the same cell keeps the
        // tooltip in place (no flicker).
        _hoverPos = now;
        TryHitTestRoom(now, out RoomKey hit);
        bool overRoom = hit.Map > 0;
        if (!overRoom)
        {
            if (_hoverRoom is not null)
            {
                _hoverRoom = null;
                RoomHovered?.Invoke(null, now);
            }
            _hoverTimer.Stop();
            return;
        }
        if (_hoverRoom is { } prev && prev.Equals(hit)) return;
        _hoverRoom = hit;
        // Dismiss the current tooltip immediately; reopen after the
        // dwell delay.
        RoomHovered?.Invoke(null, now);
        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverTimer.Stop();
        if (_hoverRoom is not null)
        {
            _hoverRoom = null;
            RoomHovered?.Invoke(null, _hoverPos);
        }
    }

    // ----- map crawler (keyboard navigation) -------------------------

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Graph is null || Layout is null) return;

        // Floor change — U / D step the crawler onto a different
        // floor. Matched against the user's configured up/down macros
        // (via the UpStepChord / DownStepChord bindings) so the same
        // chord that walks the character up/down in-game drives the
        // map crawler when the map has focus.
        if (ChordMatches(e, UpStepChord))   { TryStepFloor(Direction.U); e.Handled = true; return; }
        if (ChordMatches(e, DownStepChord)) { TryStepFloor(Direction.D); e.Handled = true; return; }

        // Home re-centres on the live current room and clears any
        // active auto-follow suppression so live movement starts
        // following the player again. Centres explicitly — the
        // selection-change handler no longer pans on its own (clicks
        // shouldn't yank the view).
        if (e.Key == Key.Home)
        {
            _autoFollowSuppressedUntil = DateTime.MinValue;
            if (CurrentRoomKey is { } cur)
            {
                SelectedRoomKey = cur;
                CenterOnRoom(cur);
            }
            e.Handled = true;
            return;
        }

        Direction? dir = e.Key switch
        {
            Key.NumPad8 or Key.Up    => Direction.N,
            Key.NumPad2 or Key.Down  => Direction.S,
            Key.NumPad6 or Key.Right => Direction.E,
            Key.NumPad4 or Key.Left  => Direction.W,
            Key.NumPad7              => Direction.NW,
            Key.NumPad9              => Direction.NE,
            Key.NumPad1              => Direction.SW,
            Key.NumPad3              => Direction.SE,
            _                        => null,
        };
        if (dir is { } d)
        {
            TryStepSelection(d);
            e.Handled = true;
        }
    }

    private static bool ChordMatches(Avalonia.Input.KeyEventArgs e, FujinTerm.Models.Profile.KeyChord chord)
    {
        if (chord.IsEmpty || chord.Key != e.Key) return false;
        bool ctrl  = (e.KeyModifiers & KeyModifiers.Control) != 0;
        bool shift = (e.KeyModifiers & KeyModifiers.Shift)   != 0;
        bool alt   = (e.KeyModifiers & KeyModifiers.Alt)     != 0;
        return chord.Ctrl == ctrl && chord.Shift == shift && chord.Alt == alt;
    }

    private RoomKey CrawlOrigin() =>
        SelectedRoomKey ?? CurrentRoomKey ?? Layout!.Origin;

    private void TryStepSelection(Direction dir)
    {
        if (Layout is null || Graph is null) return;
        RoomKey here = CrawlOrigin();
        if (Graph.GetRoom(here) is not { } room) return;
        if (!room.Exits.TryGetValue(dir, out RoomExit exit)) return;

        // Destination IS the room across the exit. Three cases:
        //   1. Placed in the current layout → move the selection AND
        //      centre on the new cell (the user just navigated there;
        //      the click-doesn't-centre rule doesn't apply to keys).
        //   2. Not in the layout but still in the active graph →
        //      treat as an out-of-floor / non-Euclidean step and ask
        //      the host to rebuild the layout from the new origin
        //      (matches the U/D PageUp/PageDown path).
        //   3. Not in the graph at all → no-op.
        SuppressAutoFollow();
        if (Layout.Positions.ContainsKey(exit.Target))
        {
            SelectedRoomKey = exit.Target;
            CenterOnRoom(exit.Target);
            return;
        }
        if (Graph.GetRoom(exit.Target) is not null)
        {
            FloorChangeRequested?.Invoke(exit.Target);
        }
    }

    private void TryStepFloor(Direction dir)
    {
        if (Layout is null || Graph is null) return;
        RoomKey here = CrawlOrigin();
        if (Graph.GetRoom(here) is not { } room) return;
        if (!room.Exits.TryGetValue(dir, out RoomExit exit)) return;
        SuppressAutoFollow();
        FloorChangeRequested?.Invoke(exit.Target);
    }

    // EnsureSelectionVisible removed — SelectedRoomKeyProperty's
    // ChangeHandler in the ctor now calls CenterOnRoom on every move
    // (MudProxy CenterOnRoom rule), so the explicit margin check is
    // no longer needed.

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
            // Move the crawler selection to the clicked room. The
            // SelectedRoomKeyProperty change handler centres the
            // view; arming auto-follow suppression keeps the click
            // sticky for the next 10 s instead of bouncing back to
            // live player position on the next in-game move.
            SuppressAutoFollow();
            SelectedRoomKey = hit;

            // Notify the host (NavigationViewModel → loop builder
            // when LoopMode is active).
            RoomLeftClicked?.Invoke(hit, releasePos);
        }
        e.Handled = true;
    }

    /// <summary>
    /// Centre the view on the room at <paramref name="key"/>. Modelled
    /// on MudProxy's <c>MapRenderer.CenterOnRoom</c> — pan = -coord *
    /// zoom puts the cell's world point exactly at screen centre.
    /// No-op when the room isn't in the active layout.
    /// </summary>
    public void CenterOnRoom(RoomKey key)
    {
        if (Layout is null) return;
        if (!Layout.Positions.TryGetValue(key, out (int X, int Y) coord)) return;
        _panX = -coord.X * TileWorldSize * _zoom;
        _panY = -coord.Y * TileWorldSize * _zoom;
        InvalidateVisual();
    }

    /// <summary>Re-centre on the player's current room (Home key / explicit recenter).</summary>
    public void FitToCurrent()
    {
        if (Layout is null) return;
        RoomKey origin = CurrentRoomKey ?? Layout.Origin;
        CenterOnRoom(origin);
    }

    /// <summary>
    /// Explicit "show me where I am right now" — clears the browse-
    /// suppression window (so live moves resume centring), moves the
    /// crawler selection to the live current room, and centres on it.
    /// Same body as the Home key handler; exposed publicly so the
    /// right-click context menu "Center on Player" item can fire it
    /// from the VM via the window code-behind.
    /// </summary>
    public void RecenterOnPlayer()
    {
        _autoFollowSuppressedUntil = DateTime.MinValue;
        if (CurrentRoomKey is { } cur)
        {
            SelectedRoomKey = cur;
            CenterOnRoom(cur);
        }
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

        // Pass 1: cell backgrounds + borders.
        foreach (KeyValuePair<(int X, int Y), RoomKey> kvp in Layout.CoordToRoom)
        {
            Rect cell = ComputeCellRect(kvp.Key, tilePixels, cx, cy);
            if (!cell.Intersects(viewport)) continue;
            context.FillRectangle(TileBg, cell);
            context.DrawRectangle(null, TileBorderPen, cell);
        }

        // Pass 2: exit lines, deduplicated. Full continuous line
        // when both endpoints are placed (no overlap seam, no
        // bump); a single half-stub when the destination is
        // dangling.
        DrawAllExitLines(context, tilePixels, cx, cy, viewport);

        // Pass 3: room nodes + per-cell overlays.
        foreach (KeyValuePair<(int X, int Y), RoomKey> kvp in Layout.CoordToRoom)
        {
            Rect cell = ComputeCellRect(kvp.Key, tilePixels, cx, cy);
            if (!cell.Intersects(viewport)) continue;

            DrawRoomNode(context, cell, kvp.Value);

            if (AvoidedRooms is not null && AvoidedRooms.Contains(kvp.Value))
                DrawAvoidX(context, cell);

            if (LoopSequenceNumbers is not null
                && LoopSequenceNumbers.TryGetValue(kvp.Value, out int seq)
                && tilePixels >= 16)
                DrawSequenceNumber(context, cell, seq);

            // Crawler selection ring — drawn inside the cell with a
            // small inset so it sits between the cell border and the
            // room node, distinct from the amber current-room ring.
            if (SelectedRoomKey is { } sel && sel.Equals(kvp.Value))
            {
                Rect ring = cell.Deflate(1);
                context.DrawRectangle(null, SelectionPen, ring);
            }
        }

        // Pass 4: top-of-stack polylines. Builder preview goes first
        // (lowest layer) so any active automation overlays it; preview
        // (queued walk) goes next; running loop / walk on top so the
        // user's primary signal is the active automation.
        DrawPathPolyline(context, LoopBuilderPath, LoopBuilderPen, tilePixels, cx, cy);
        DrawPathPolyline(context, PreviewPath, PreviewPathPen, tilePixels, cx, cy);
        DrawPathPolyline(context, LoopPath, LoopPathPen, tilePixels, cx, cy);
        IPen walkPen = WalkPathIsAutoLair ? AutoLairWalkPen : WalkPathPen;
        DrawPathPolyline(context, WalkPath, walkPen, tilePixels, cx, cy);
    }

    private static Rect ComputeCellRect((int X, int Y) coord, double tilePixels, double cx, double cy)
    {
        double centerX = cx + coord.X * tilePixels;
        double centerY = cy + coord.Y * tilePixels;
        return new Rect(
            centerX - tilePixels / 2,
            centerY - tilePixels / 2,
            tilePixels,
            tilePixels);
    }

    /// <summary>
    /// Walks <see cref="RoomLayout.EdgesFromCoord"/> once and draws
    /// each (source, target) pair exactly once. When both endpoints
    /// are placed cells, draws a single line between the two cell
    /// centres — no overlap seam, no thickness bump. When the target
    /// is dangling (an asymmetric / Euclidean-clashing exit), draws a
    /// stub from the source cell's centre to its edge.
    /// </summary>
    private void DrawAllExitLines(DrawingContext ctx, double tilePixels, double cx, double cy, Rect viewport)
    {
        if (Layout is null) return;

        var drawn = new HashSet<((int X, int Y) A, (int X, int Y) B)>();

        foreach (KeyValuePair<(int X, int Y), IReadOnlySet<Direction>> entry in Layout.EdgesFromCoord)
        {
            (int X, int Y) source = entry.Key;
            double srcX = cx + source.X * tilePixels;
            double srcY = cy + source.Y * tilePixels;
            Point srcPt = new(srcX, srcY);

            foreach (Direction dir in entry.Value)
            {
                if (!TryPlanarOffset(dir, out int dx, out int dy)) continue;
                (int X, int Y) target = (source.X + dx, source.Y + dy);

                ((int X, int Y) A, (int X, int Y) B) pair = SortPair(source, target);
                if (!drawn.Add(pair)) continue;

                bool isTrap = IsTrapEdge(source, dir)
                           || IsTrapEdge(target, Opposite(dir));
                IPen pen = isTrap ? TrapPen : ExitPen;

                if (Layout.CoordToRoom.ContainsKey(target))
                {
                    // Both endpoints placed — single continuous line.
                    double tgtX = cx + target.X * tilePixels;
                    double tgtY = cy + target.Y * tilePixels;
                    Point tgtPt = new(tgtX, tgtY);
                    ctx.DrawLine(pen, srcPt, tgtPt);
                }
                else
                {
                    // Dangling — stub from source cell centre to edge.
                    Rect cell = ComputeCellRect(source, tilePixels, cx, cy);
                    DrawStub(ctx, pen, cell, srcX, srcY, dir);
                }
            }
        }
    }

    private bool IsTrapEdge((int X, int Y) coord, Direction dir)
    {
        if (Layout?.TrapEdgesFromCoord is null) return false;
        return Layout.TrapEdgesFromCoord.TryGetValue(coord, out IReadOnlySet<Direction>? set)
            && set.Contains(dir);
    }

    private static ((int X, int Y) A, (int X, int Y) B) SortPair((int X, int Y) a, (int X, int Y) b)
        => (a.X < b.X || (a.X == b.X && a.Y < b.Y)) ? (a, b) : (b, a);

    private static bool TryPlanarOffset(Direction dir, out int dx, out int dy)
    {
        switch (dir)
        {
            case Direction.N:  dx =  0; dy = -1; return true;
            case Direction.S:  dx =  0; dy =  1; return true;
            case Direction.E:  dx =  1; dy =  0; return true;
            case Direction.W:  dx = -1; dy =  0; return true;
            case Direction.NE: dx =  1; dy = -1; return true;
            case Direction.NW: dx = -1; dy = -1; return true;
            case Direction.SE: dx =  1; dy =  1; return true;
            case Direction.SW: dx = -1; dy =  1; return true;
            default:           dx = dy = 0;     return false;
        }
    }

    private static Direction Opposite(Direction dir) => dir switch
    {
        Direction.N  => Direction.S,
        Direction.S  => Direction.N,
        Direction.E  => Direction.W,
        Direction.W  => Direction.E,
        Direction.NE => Direction.SW,
        Direction.SW => Direction.NE,
        Direction.NW => Direction.SE,
        Direction.SE => Direction.NW,
        _            => dir,
    };

    private void DrawPathPolyline(DrawingContext ctx, IReadOnlyList<RoomKey>? path, IPen pen,
        double tilePixels, double cx, double cy)
    {
        if (path is null || path.Count < 2 || Layout is null) return;

        Point? prev = null;
        foreach (RoomKey key in path)
        {
            if (!Layout.Positions.TryGetValue(key, out (int X, int Y) coord))
            {
                prev = null;                                  // gap — skip until next placed room
                continue;
            }
            Point here = new(cx + coord.X * tilePixels, cy + coord.Y * tilePixels);
            if (prev is { } p) ctx.DrawLine(pen, p, here);
            prev = here;
        }
    }

    private static void DrawAvoidX(DrawingContext ctx, Rect cell)
    {
        double inset = cell.Width * 0.25;
        Point topLeft     = new(cell.X + inset, cell.Y + inset);
        Point topRight    = new(cell.Right - inset, cell.Y + inset);
        Point bottomLeft  = new(cell.X + inset, cell.Bottom - inset);
        Point bottomRight = new(cell.Right - inset, cell.Bottom - inset);
        ctx.DrawLine(AvoidXPen, topLeft, bottomRight);
        ctx.DrawLine(AvoidXPen, topRight, bottomLeft);
    }

    private void DrawSequenceNumber(DrawingContext ctx, Rect cell, int seq)
    {
        Typeface tf = new("Inter", FontStyle.Normal, FontWeight.Bold);
        double size = Math.Clamp(cell.Width * 0.32, 8, 16);
        FormattedText ft = new(seq.ToString(), System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, tf, size, SeqNumberFill);
        Point p = new(
            cell.X + (cell.Width  - ft.Width)  / 2,
            cell.Y + (cell.Height - ft.Height) / 2);
        ctx.DrawText(ft, p);
    }

    /// <summary>
    /// Draws a half-line from the cell centre to one edge. Only used
    /// by the dangling-exit branch in <see cref="DrawAllExitLines"/>
    /// — full lines between two placed cells are drawn end-to-end
    /// without overlap. No StubOverlap here: there's no adjacent
    /// stub to meet, so the segment ends flush at the cell edge.
    /// </summary>
    private static void DrawStub(DrawingContext ctx, IPen pen, Rect cell, double mx, double my, Direction dir)
    {
        switch (dir)
        {
            case Direction.N:  ctx.DrawLine(pen, new Point(mx, my), new Point(mx, cell.Top)); break;
            case Direction.S:  ctx.DrawLine(pen, new Point(mx, my), new Point(mx, cell.Bottom)); break;
            case Direction.E:  ctx.DrawLine(pen, new Point(mx, my), new Point(cell.Right, my)); break;
            case Direction.W:  ctx.DrawLine(pen, new Point(mx, my), new Point(cell.Left,  my)); break;
            case Direction.NE: ctx.DrawLine(pen, new Point(mx, my), new Point(cell.Right, cell.Top)); break;
            case Direction.NW: ctx.DrawLine(pen, new Point(mx, my), new Point(cell.Left,  cell.Top)); break;
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
        bool isDestination = !isCurrent && DestinationRoomKey is { } dest && dest.Equals(key);
        bool isAutoLair = AutoLairRooms is not null && AutoLairRooms.Contains(key);
        Room? room = Graph?.GetRoom(key);

        IBrush fill;
        IPen pen;
        if (isCurrent)
        {
            fill = CurrentFill;
            pen = CurrentPen;
        }
        else if (isDestination)
        {
            fill = DestinationFill;
            pen = DestinationRing;
        }
        else if (isAutoLair)
        {
            fill = AutoLairFill;
            pen = AutoLairBorder;
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
        else if (Layout?.VerticalHints is { } vhints && vhints.TryGetValue(key, out VerticalHint hint))
        {
            (fill, pen) = hint switch
            {
                VerticalHint.Both => ((IBrush)UpDownFill, (IPen)UpDownBorderPen),
                VerticalHint.Up   => (UpFill,             UpBorderPen),
                VerticalHint.Down => (DownFill,           DownBorderPen),
                _                 => ((IBrush)RoomFill,   (IPen)RoomBorderPen),
            };
        }
        else
        {
            fill = RoomFill;
            pen = RoomBorderPen;
        }

        ctx.FillRectangle(fill, node);
        ctx.DrawRectangle(null, pen, node);

        // Teleport-CMD overlay — diagonal cross-hatch so rooms with a
        // keyword-triggered teleport (use chime → 1/65 etc.) read at
        // a glance even when their cell fill is claimed by another
        // class. Drawn under the U/D badge so the corner triangle
        // stays the brightest signal.
        if (TeleportRooms is { } tr && tr.Contains(key))
        {
            DrawTeleportHash(ctx, node);
        }

        // Vertical-exit corner badges — always drawn when the room has
        // a U/D hint, regardless of the cell's primary fill class. Lets
        // the user see "this room goes up/down" even when the fill is
        // claimed by Lair / Shop / Spell / Auto-Lair.
        if (Layout?.VerticalHints is { } vh
            && vh.TryGetValue(key, out VerticalHint vhint)
            && vhint != VerticalHint.None)
        {
            DrawVerticalCornerBadge(ctx, node, vhint);
        }

        if (isCurrent || isDestination)
        {
            // Thick perimeter ring + centre dot — same shape for both
            // markers so the destination reads as "the other end of the
            // pair" rather than a different room class.
            Rect ring = cell.Deflate(2);
            IPen  outerPen = isCurrent ? PlayerOuterPen   : DestinationOuterPen;
            IBrush dotFill  = isCurrent ? PlayerDotFill    : DestinationDotFill;
            IPen   dotPen   = isCurrent ? PlayerDotPen     : DestinationDotPen;
            ctx.DrawRectangle(null, outerPen, ring);

            double dotSize = Math.Max(cell.Width * 0.22, 4.0);
            double dx = cell.X + (cell.Width  - dotSize) / 2;
            double dy = cell.Y + (cell.Height - dotSize) / 2;
            Rect dot = new(dx, dy, dotSize, dotSize);
            ctx.DrawGeometry(dotFill, dotPen, new EllipseGeometry(dot));
        }
    }

    /// <summary>
    /// Draws small filled triangles in the right corners of the node
    /// to indicate U/D exits:
    /// <list type="bullet">
    /// <item>top-right green triangle when the room has an Up exit;</item>
    /// <item>bottom-right yellow triangle when it has a Down exit;</item>
    /// <item>both triangles when both exits are present (Up+Down rooms
    /// get the green corner on top of the existing UpDownFill or the
    /// classification fill — orange/green/yellow stay distinct).</item>
    /// </list>
    /// Triangle size scales with the node so it stays glanceable on
    /// small cells without crowding the centre dot of the player /
    /// destination marker.
    /// </summary>
    private static void DrawVerticalCornerBadge(DrawingContext ctx, Rect node, VerticalHint hint)
    {
        double size = Math.Max(node.Width * 0.50, 7.0);

        if (hint is VerticalHint.Up or VerticalHint.Both)
        {
            StreamGeometry geo = new();
            using (StreamGeometryContext g = geo.Open())
            {
                g.BeginFigure(new Point(node.Right - size, node.Top), isFilled: true);
                g.LineTo(new Point(node.Right, node.Top));
                g.LineTo(new Point(node.Right, node.Top + size));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(UpFill, null, geo);
        }

        if (hint is VerticalHint.Down or VerticalHint.Both)
        {
            StreamGeometry geo = new();
            using (StreamGeometryContext g = geo.Open())
            {
                g.BeginFigure(new Point(node.Right - size, node.Bottom), isFilled: true);
                g.LineTo(new Point(node.Right, node.Bottom));
                g.LineTo(new Point(node.Right, node.Bottom - size));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(DownFill, null, geo);
        }
    }

    /// <summary>
    /// Draws diagonal cross-hatch lines across the cell node to mark a
    /// room with a CMD-driven teleport command. Clipped to the node so
    /// the lines don't bleed onto neighbouring connectors. Spacing
    /// scales with the cell so the pattern stays readable when zoomed
    /// in/out.
    /// </summary>
    private static void DrawTeleportHash(DrawingContext ctx, Rect node)
    {
        double spacing = Math.Max(node.Width * 0.30, 4.0);
        using (ctx.PushClip(node))
        {
            // \\\\ direction
            for (double offset = -node.Height; offset < node.Width; offset += spacing)
            {
                ctx.DrawLine(TeleportHashPen,
                    new Point(node.Left + offset,              node.Top),
                    new Point(node.Left + offset + node.Height, node.Bottom));
            }
            // //// direction
            for (double offset = 0; offset < node.Width + node.Height; offset += spacing)
            {
                ctx.DrawLine(TeleportHashPen,
                    new Point(node.Left + offset,              node.Top),
                    new Point(node.Left + offset - node.Height, node.Bottom));
            }
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
