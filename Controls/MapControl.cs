using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using FujinTerm.Game.Map;

namespace FujinTerm.Controls;

// BFS-planar room map rendering for the Navigation window.
//
// Rendering style — per layout coord we draw a slightly darker "tile"
// rectangle, then short exit stubs from the tile centre to each tile edge (one
// stub per direction the source room has, sourced from
// RoomLayout.EdgesFromCoord), then a smaller room-node rectangle centred in
// the tile. Adjacent tiles' stubs meet at the shared edge to form a
// continuous visual line; exits to rooms that fell off-grid still render as a
// stub from the source side.
//
// Input — plain left-button drag pans the view; a mouse-wheel notch zooms
// about the cursor. A left-button release without movement is treated as a
// click on the underlying room (used by the loop-builder mode); right-click
// opens the host's context menu.
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

    // Preview polyline for a queued (but not yet running) walk. Drawn in red
    // beneath any live WalkPath so an active walk overlays its target without
    // flicker. NavigationViewModel sets this when a search-result selection
    // arms a destination.
    public static readonly StyledProperty<IReadOnlyList<RoomKey>?> PreviewPathProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyList<RoomKey>?>(nameof(PreviewPath));

    public static readonly StyledProperty<IReadOnlyList<RoomKey>?> LoopPathProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyList<RoomKey>?>(nameof(LoopPath));

    // Live BFS-expanded preview of the loop the user is currently building in
    // the Navigation window's loop-builder strip. Drawn underneath any active
    // LoopPath / WalkPath so an active automation always overlays the build
    // preview when both share a segment. Pen is dashed cyan to distinguish from
    // the solid red preview and the blue active-loop pens.
    public static readonly StyledProperty<IReadOnlyList<RoomKey>?> LoopBuilderPathProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyList<RoomKey>?>(nameof(LoopBuilderPath));

    // Per-click ordered waypoints surfaced as numbered red circles on the map
    // while the user is in loop-build mode. Separate from LoopBuilderPath
    // because the path is the BFS-filled flat room sequence (every
    // intermediate hop), whereas this list is only the rooms the user actually
    // clicked.
    public static readonly StyledProperty<IReadOnlyList<RoomKey>?> LoopBuilderWaypointsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyList<RoomKey>?>(nameof(LoopBuilderWaypoints));

    // Loop preview drawn during the walker-driven approach phase of a loop run.
    // Distinct from LoopBuilderPath only by semantic (drawn with the same red
    // pen). Visible alongside the active WalkPath so the user sees both the
    // immediate walk to the start waypoint and the bigger-picture cycle that's
    // about to begin.
    public static readonly StyledProperty<IReadOnlyList<RoomKey>?> LoopApproachPreviewPathProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyList<RoomKey>?>(nameof(LoopApproachPreviewPath));

    public static readonly StyledProperty<IReadOnlySet<RoomKey>?> AvoidedRoomsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlySet<RoomKey>?>(nameof(AvoidedRooms));

    // Rooms the user has marked as stash drops. Rendered with a gold outline so
    // the user can spot them at a glance. Game.Cash.StashRoomManager reads the
    // same set from Models.Profile.CharacterProfile.StashRooms and dispatches
    // hide N <coin> on entry.
    public static readonly StyledProperty<IReadOnlySet<RoomKey>?> StashRoomsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlySet<RoomKey>?>(nameof(StashRooms));

    public static readonly StyledProperty<IReadOnlyDictionary<RoomKey, int>?> LoopSequenceNumbersProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyDictionary<RoomKey, int>?>(nameof(LoopSequenceNumbers));

    public static readonly StyledProperty<IReadOnlySet<RoomKey>?> AutoLairRoomsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlySet<RoomKey>?>(nameof(AutoLairRooms));

    // Ordered list of Auto-Lair markers — when non-null + non-empty, each room
    // gets a numbered amber circle overlay matching the CURRENT NAV row index.
    // Same pattern as LoopBuilderWaypoints for loops; the order is supplied by
    // ViewModels.Navigation.NavigationViewModel so the map and the CURRENT NAV
    // list always agree.
    public static readonly StyledProperty<IReadOnlyList<RoomKey>?> AutoLairWaypointsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyList<RoomKey>?>(nameof(AutoLairWaypoints));

    // Stable full-leg path drawn in orange during an Auto-Lair run —
    // current → wait-room → lair. Holds steady across the Approaching →
    // Waiting → Entering transitions so the line doesn't redraw / disappear as
    // the walker steps through it, fixing the per-step flicker the user-driven
    // WalkPath had. When set, the regular WalkPath is suppressed.
    public static readonly StyledProperty<IReadOnlyList<RoomKey>?> AutoLairApproachPathProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyList<RoomKey>?>(nameof(AutoLairApproachPath));

    // Set of rooms with a CMD-driven teleport command (TBInfo Action chain
    // contains a teleport <r> <m> directive). Rendered with diagonal
    // cross-hatch lines over the cell fill so the user can see at a glance
    // which rooms hide a non-exit movement option (e.g. 1/1182 "use chime" →
    // 1/65).
    public static readonly StyledProperty<IReadOnlySet<RoomKey>?> TeleportRoomsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlySet<RoomKey>?>(nameof(TeleportRooms));

    // Rooms holding an un-recovered deathpile (a DeathRecord whose Status isn't
    // Recovered). Each gets a skull glyph so the player can spot where a death is
    // waiting to be recovered. Supplied by NavigationViewModel from
    // DeathRecoveryManager.Records; a record flipping to Recovered drops its room
    // from the set and the skull clears on the next render.
    public static readonly StyledProperty<IReadOnlySet<RoomKey>?> DeathRoomsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlySet<RoomKey>?>(nameof(DeathRooms));

    public static readonly StyledProperty<bool> WalkPathIsAutoLairProperty =
        AvaloniaProperty.Register<MapControl, bool>(nameof(WalkPathIsAutoLair));

    public static readonly StyledProperty<RoomKey?> SelectedRoomKeyProperty =
        AvaloniaProperty.Register<MapControl, RoomKey?>(nameof(SelectedRoomKey));

    // Walk-to destination — blue-filled with a ring so it's immediately
    // recognisable as the goal, mirroring the "you are here" treatment.
    public static readonly StyledProperty<RoomKey?> DestinationRoomKeyProperty =
        AvaloniaProperty.Register<MapControl, RoomKey?>(nameof(DestinationRoomKey));

    public static readonly StyledProperty<FujinTerm.Models.Profile.KeyChord> UpStepChordProperty =
        AvaloniaProperty.Register<MapControl, FujinTerm.Models.Profile.KeyChord>(nameof(UpStepChord),
            new FujinTerm.Models.Profile.KeyChord(Key.PageUp));

    public static readonly StyledProperty<FujinTerm.Models.Profile.KeyChord> DownStepChordProperty =
        AvaloniaProperty.Register<MapControl, FujinTerm.Models.Profile.KeyChord>(nameof(DownStepChord),
            new FujinTerm.Models.Profile.KeyChord(Key.PageDown));

    public static readonly StyledProperty<IReadOnlyDictionary<Direction, FujinTerm.Models.Profile.KeyChord>?> CompassChordsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyDictionary<Direction, FujinTerm.Models.Profile.KeyChord>?>(nameof(CompassChords));

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

    public IReadOnlyList<RoomKey>? LoopBuilderWaypoints
    {
        get => GetValue(LoopBuilderWaypointsProperty);
        set => SetValue(LoopBuilderWaypointsProperty, value);
    }

    public IReadOnlyList<RoomKey>? LoopApproachPreviewPath
    {
        get => GetValue(LoopApproachPreviewPathProperty);
        set => SetValue(LoopApproachPreviewPathProperty, value);
    }

    public IReadOnlySet<RoomKey>? AvoidedRooms
    {
        get => GetValue(AvoidedRoomsProperty);
        set => SetValue(AvoidedRoomsProperty, value);
    }

    public IReadOnlySet<RoomKey>? StashRooms
    {
        get => GetValue(StashRoomsProperty);
        set => SetValue(StashRoomsProperty, value);
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

    public IReadOnlyList<RoomKey>? AutoLairWaypoints
    {
        get => GetValue(AutoLairWaypointsProperty);
        set => SetValue(AutoLairWaypointsProperty, value);
    }

    public IReadOnlyList<RoomKey>? AutoLairApproachPath
    {
        get => GetValue(AutoLairApproachPathProperty);
        set => SetValue(AutoLairApproachPathProperty, value);
    }

    public IReadOnlySet<RoomKey>? TeleportRooms
    {
        get => GetValue(TeleportRoomsProperty);
        set => SetValue(TeleportRoomsProperty, value);
    }

    public IReadOnlySet<RoomKey>? DeathRooms
    {
        get => GetValue(DeathRoomsProperty);
        set => SetValue(DeathRoomsProperty, value);
    }

    public bool WalkPathIsAutoLair
    {
        get => GetValue(WalkPathIsAutoLairProperty);
        set => SetValue(WalkPathIsAutoLairProperty, value);
    }

    // Cursor for the keyboard map-crawler. Null = no selection (the current
    // room is implicitly active when the user first presses a navigation key).
    // Drawn as a cyan ring around the cell so it reads distinctly from the
    // amber current-room highlight.
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

    // Fired when the user steps the crawler up or down — the layout host is
    // expected to rebuild from the new room (which lives on a different floor
    // and therefore isn't in the current layout).
    public event Action<RoomKey>? FloorChangeRequested;

    // Key chord that steps the crawler one floor up. Bound from the user's
    // macro configured to send u to the game so the same chord drives both
    // in-game movement and the map crawler. Defaults to PageUp when the macro
    // isn't bound.
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

    // Per-direction crawler chords derived from the user's N/S/E/W + diagonal
    // movement macros (Settings → Macros). When a direction has a macro bound,
    // the macro's key steps the crawler that way so the same key that sends the
    // direction in-game also drives the map. Directions absent here fall
    // through to the hardcoded numpad / arrow defaults in OnKeyDown, so the
    // crawler is never left with no binding.
    public IReadOnlyDictionary<Direction, FujinTerm.Models.Profile.KeyChord>? CompassChords
    {
        get => GetValue(CompassChordsProperty);
        set => SetValue(CompassChordsProperty, value);
    }

    // ----- view-state ------------------------------------------------

    // World tile size in layout units. Multiplied by _zoom to get screen pixels.
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

    // Auto-follow suppression — after any explicit pan-drag, zoom, or
    // crawler step, the player-room auto-centre is paused for this
    // many seconds so the user can browse (including while the party
    // follower / leader keeps moving) without the view yanking back to
    // live position. An explicit re-root of the layout onto a room the
    // user isn't standing on (floor-crawl / search jump) is still
    // honoured immediately — only the movement-driven recentre waits.
    private DateTime _autoFollowSuppressedUntil = DateTime.MinValue;
    private const int AutoFollowSuppressionSeconds = 15;

    private void SuppressAutoFollow()
        => _autoFollowSuppressedUntil = DateTime.UtcNow.AddSeconds(AutoFollowSuppressionSeconds);

    private bool IsAutoFollowSuppressed
        => DateTime.UtcNow < _autoFollowSuppressedUntil;

    // Fires once the pointer has dwelled over a room cell for HoverDelayMs, AND
    // any time the hovered room changes. Carries the room key + screen-local
    // pointer position so the host can position a popup. Null payload means
    // "no room is being hovered" — host should dismiss the popup.
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
    // Vertical-exit indicators: green = up only,
    // yellow = down only, orange = both. Applied as the room-node fill
    // when no higher-priority highlight (current / auto-lair / lair /
    // shop / spell) takes the cell.
    private static readonly IBrush UpFill        = new SolidColorBrush(Color.Parse("#00C800"));
    private static readonly IBrush DownFill      = new SolidColorBrush(Color.Parse("#DCDC00"));
    private static readonly IBrush UpDownFill    = new SolidColorBrush(Color.Parse("#FFB432"));

    private static readonly IPen   TileBorderPen = new Pen(new SolidColorBrush(Color.Parse("#2A2A2A")), 1.0);
    private static readonly IPen   ExitPen       = new Pen(new SolidColorBrush(Color.Parse("#C0C0C0")), 2.0);
    private static readonly IPen   TrapPen       = new Pen(new SolidColorBrush(Color.Parse("#DC3C3C")), 2.0);
    // Dark magenta for exits that require one or more in-room actions
    // before traversal (RoomExitHint.MultiActionHidden) — e.g. map
    // 9 / room 1032's east exit on the v1.11p data set, which needs
    // a lever pull elsewhere before the walker can step E. Distinct
    // from trap red (more dangerous, takes precedence at render time).
    private static readonly IPen   ActionPen     = new Pen(new SolidColorBrush(Color.Parse("#8B008B")), 2.0);
    // Dark cyan for plain hidden exits revealed via `sea <dir>`
    // (RoomExitHint.SearchableHidden) — e.g. map 9 / room 1031 on
    // v1.11p. Distinct from action-magenta (no prerequisite action,
    // just a search) and from trap-red.
    private static readonly IPen   HiddenPen     = new Pen(new SolidColorBrush(Color.Parse("#008B8B")), 2.0);
    // "Gap-bridge" pens — used when two rooms are connected by an exit
    // but the layout couldn't place them on grid-adjacent cells (the map
    // data has a non-tiling diagonal / off-by-one reciprocal, common in
    // hand-built forest + sewer maps). Instead of a stub pointing into
    // empty space we draw a direct dashed line between the two room
    // centres so the connection is visible. Dashed + thinner (and the
    // plain variant dimmer) so the user can still tell a true
    // grid-adjacent connection from a forced bridge; trap / action /
    // hidden keep their semantic hue for recognition.
    private static readonly DashStyle BridgeDash = new(new double[] { 2, 2 }, 0);
    private static readonly IPen   ExitBridgePen   = new Pen(new SolidColorBrush(Color.Parse("#8A8A8A")), 1.5) { DashStyle = BridgeDash, LineCap = PenLineCap.Round };
    private static readonly IPen   TrapBridgePen   = new Pen(new SolidColorBrush(Color.Parse("#DC3C3C")), 1.5) { DashStyle = BridgeDash, LineCap = PenLineCap.Round };
    private static readonly IPen   ActionBridgePen = new Pen(new SolidColorBrush(Color.Parse("#8B008B")), 1.5) { DashStyle = BridgeDash, LineCap = PenLineCap.Round };
    private static readonly IPen   HiddenBridgePen = new Pen(new SolidColorBrush(Color.Parse("#008B8B")), 1.5) { DashStyle = BridgeDash, LineCap = PenLineCap.Round };
    // Max grid distance (Chebyshev) a gap-bridge line spans; beyond this the
    // rooms are too far apart to bridge cleanly and we fall back to a dangling
    // stub rather than shoot a line across the map.
    private const int BridgeMaxCells = 4;
    private static readonly IPen   RoomBorderPen = new Pen(new SolidColorBrush(Color.Parse("#D0D0D0")), 1.0);
    private static readonly IPen   CurrentPen    = new Pen(new SolidColorBrush(Color.Parse("#FFD24D")), 2.0);
    private static readonly IPen   LairBorderPen  = new Pen(new SolidColorBrush(Color.Parse("#B36F9C")), 1.5);
    private static readonly IPen   ShopBorderPen  = new Pen(new SolidColorBrush(Color.Parse("#6A9CB6")), 1.5);
    private static readonly IPen   SpellBorderPen = new Pen(new SolidColorBrush(Color.Parse("#9C70CC")), 1.5);
    // Perpendicular "wall" bar drawn across a cast-on-walk exit's connector, in
    // the same purple as the spell-room highlight so the two read as kin.
    // Thicker than the grey exit line it crosses so it stands out as a barrier
    // between the two rooms — "walking here fires a spell on you."
    private static readonly IPen   SpellWallPen   = new Pen(new SolidColorBrush(Color.Parse("#9C70CC")), 4.5)
    {
        LineCap = PenLineCap.Round,
    };
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
    // Active loop line — green to match the engine colour schema:
    // walk-to blue, loop green, auto-lair orange (AccentGreenColor in
    // the palette). The colour is reused by the Navigation rail's
    // section headers + the Loops + Auto-Lairs list rows so the user
    // gets one consistent "this is a loop" signal everywhere.
    private static readonly IPen   LoopPathPen    = new Pen(new SolidColorBrush(Color.Parse("#7AB870")), 3.0)
    {
        LineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };
    // Solid red "where Run would walk" preview line for queued walk-to
    // destinations. Drawn beneath the active walk so a live walk overlays it.
    // Hue distinct from the trap red (TrapPen #DC3C3C) so the user reads them
    // as different signals. Dashed-red is reserved for loop previews.
    private static readonly IPen   PreviewPathPen = new Pen(new SolidColorBrush(Color.Parse("#E66C5A")), 3.0)
    {
        LineCap  = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };

    // Solid red polyline for the user's in-progress loop-builder gap-filled
    // path. Shares hue with PreviewPathPen (queued walk-to) because both are
    // "preview" overlays. Executing engines have their own per-engine colours
    // (walk-to WalkPathPen blue, loop LoopPathPen green, auto-lair
    // AutoLairWalkPen orange), so red unambiguously means "planned, not yet
    // driving." Numbered waypoint markers (drawn separately) disambiguate
    // build-preview from a single-leg walk-to preview when both could
    // theoretically be visible.
    private static readonly IPen   LoopBuilderPen = new Pen(new SolidColorBrush(Color.Parse("#E66C5A")), 3.0)
    {
        LineCap     = PenLineCap.Round,
        LineJoin    = PenLineJoin.Round,
    };

    // Fill brush for the per-waypoint numbered circle markers.
    private static readonly IBrush LoopBuilderWaypointFill =
        new SolidColorBrush(Color.Parse("#E66C5A"));
    private static readonly IPen   LoopBuilderWaypointRing =
        new Pen(new SolidColorBrush(Color.Parse("#FFFFFFFF")), 1.5);
    private static readonly IBrush LoopBuilderWaypointTextBrush =
        new SolidColorBrush(Color.Parse("#FFFFFFFF"));

    // Auto-Lair numbered overlay — amber to match the section theme.
    private static readonly IBrush AutoLairWaypointFill =
        new SolidColorBrush(Color.Parse("#DC821E"));
    private static readonly IPen   AutoLairWaypointRing =
        new Pen(new SolidColorBrush(Color.Parse("#FFFFFFFF")), 1.5);
    private static readonly IBrush AutoLairWaypointTextBrush =
        new SolidColorBrush(Color.Parse("#FFFFFFFF"));

    // Cross-hatch overlay for teleport-CMD rooms. Fully-opaque bright
    // cyan with a 1.5 px stroke so the pattern reads at default zoom
    // without disappearing into the cell fill — the prior #B0FFFFFF
    // at 1.0 px was nearly invisible on lair pink and shop blue.
    private static readonly IPen TeleportHashPen
        = new Pen(new SolidColorBrush(Color.Parse("#FF50E6FF")), 1.5);
    // Brighter punch-red so the avoid markers read at a glance on
    // dark + medium-fill room glyphs alike — the prior #FF6464 sat
    // too close to the cell muted-red trap line and washed out on
    // the darker palettes. Stroke bumped to 2.5 px so the cross
    // strokes don't get lost on aliased small cells.
    private static readonly IPen   AvoidXPen      = new Pen(new SolidColorBrush(Color.Parse("#FF2020")), 2.5)
    {
        LineCap = PenLineCap.Round,
    };

    // Golden X for stash rooms — matches the shape of the avoid-X so the two
    // map markers read as a pair ("flagged room") with colour carrying the
    // action: red = avoid, gold = stash. Distinct from the amber current-room
    // ring + the white selection ring so the user can scan for stashes at a
    // glance.
    private static readonly IPen   StashXPen      = new Pen(new SolidColorBrush(Color.Parse("#FFFFD24E")), 2.5)
    {
        LineCap = PenLineCap.Round,
    };
    // Death-marker skull — bone-white silhouette with dark hollows, drawn on
    // rooms that still hold an un-recovered deathpile. The dark eye / nose / tooth
    // features carry the contrast so the glyph reads on both light and dark room
    // fills; the thin rim keeps the bone shape legible over the amber current-room
    // fill. Vector primitives (no emoji glyph) so it renders on any font.
    private static readonly IBrush SkullBoneFill  = new SolidColorBrush(Color.Parse("#ECE7DA"));
    private static readonly IBrush SkullSocketFill = new SolidColorBrush(Color.Parse("#141414"));
    private static readonly IPen   SkullRimPen     = new Pen(new SolidColorBrush(Color.Parse("#2E281F")), 1.0);
    private static readonly IPen   SkullToothPen    = new Pen(new SolidColorBrush(Color.Parse("#2E281F")), 1.0)
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
            WalkPathProperty, LoopPathProperty, LoopBuilderPathProperty, LoopBuilderWaypointsProperty,
            AutoLairWaypointsProperty, AutoLairApproachPathProperty,
            LoopApproachPreviewPathProperty, AvoidedRoomsProperty, StashRoomsProperty, LoopSequenceNumbersProperty,
            AutoLairRoomsProperty, WalkPathIsAutoLairProperty, SelectedRoomKeyProperty,
            PreviewPathProperty, TeleportRoomsProperty, DeathRoomsProperty);

        // Auto-centre on the player's current room every time it
        // changes — but only when the
        // user isn't actively browsing. Drag-pan, zoom, and crawler
        // steps all arm a 15-second suppression window during which
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
        // When the layout itself rebuilds, re-centre on its origin —
        // the room it was built around. Two kinds of rebuild reach
        // here, distinguished by whether that origin is our live room:
        //   - Movement rebuild (walked onto a new floor, reconnect,
        //     party-follow drag / leader loop): origin == CurrentRoomKey.
        //     Re-centre on it, but honour the browse-suppression window
        //     so a party member's move doesn't yank the view while the
        //     user is actively looking around. This is also what fixes
        //     the old bug where crossing a U/D centred on a stale
        //     destination selection instead of where we actually landed.
        //   - Explicit re-root (PageUp/Down floor-crawl, search jump):
        //     origin is a room the user isn't standing on. Honour it
        //     immediately — the user just asked to go there.
        LayoutProperty.Changed.AddClassHandler<MapControl>((c, _) =>
        {
            if (c.Layout is not { } layout) return;
            RoomKey origin = layout.Origin;
            if (c.CurrentRoomKey is { } cur && cur.Equals(origin))
            {
                if (c.IsAutoFollowSuppressed) return;
                c.CenterOnRoom(origin);
                return;
            }
            c.CenterOnRoom(origin);
        });
    }

    // Raised on right-click. The key is the hit room, or null when the click
    // landed on empty map space — so the context-menu target is cleared
    // instead of left pointing at a stale (off-screen) room.
    public event Action<RoomKey?, Point>? RoomRightClicked;
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
        // Zooming is active browsing — arm the suppression window so a
        // live move doesn't yank the view out from under the user.
        SuppressAutoFollow();
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

        if (point.Properties.IsRightButtonPressed)
        {
            if (TryHitTestRoom(point.Position, out RoomKey hit))
            {
                // Mirror the crawler outline onto the right-clicked room
                // so the user can see which square the context menu is
                // attached to (the menu can move off-screen on small maps).
                SelectedRoomKey = hit;
                RoomRightClicked?.Invoke(hit, point.Position);
            }
            else
            {
                // Empty-space right-click: clear the context target so the
                // menu doesn't keep showing the previous room's entries
                // (teleports, etc.) after the map has shifted under it.
                RoomRightClicked?.Invoke(null, point.Position);
            }
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
                // player position for the next 15 s.
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
        // following the player again. Re-roots the layout first when the
        // player is off the browsed floor (see RecenterOnPlayer).
        if (e.Key == Key.Home)
        {
            RecenterOnPlayer();
            e.Handled = true;
            return;
        }

        // Macro-derived compass chords take precedence: the same key the
        // user mapped to send a direction in-game steps the crawler that
        // way. The hardcoded numpad / arrow switch below stays as the
        // always-available fallback (and covers directions with no macro).
        if (CompassChords is { } chords)
        {
            foreach ((Direction cd, FujinTerm.Models.Profile.KeyChord chord) in chords)
            {
                if (!ChordMatches(e, chord)) continue;
                TryStepSelection(cd);
                e.Handled = true;
                return;
            }
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

    // Crawler stepping centres explicitly from TryStepSelection /
    // TryStepFloor (a keyboard step can walk the selection off the
    // visible window); a plain selection change does not pan on its own.

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
            // Move the crawler selection to the clicked room and keep
            // the click sticky for the next 15 s by arming auto-follow
            // suppression, instead of bouncing back to live player
            // position on the next in-game move.
            SuppressAutoFollow();
            SelectedRoomKey = hit;

            // Notify the host (NavigationViewModel → loop builder
            // when LoopMode is active).
            RoomLeftClicked?.Invoke(hit, releasePos);
        }
        e.Handled = true;
    }

    // Centre the view on the room at key. pan = -coord * zoom puts the cell's
    // world point exactly at screen centre. No-op when the room isn't in the
    // active layout.
    public void CenterOnRoom(RoomKey key)
    {
        if (Layout is null) return;
        if (!Layout.Positions.TryGetValue(key, out (int X, int Y) coord)) return;
        _panX = -coord.X * TileWorldSize * _zoom;
        _panY = -coord.Y * TileWorldSize * _zoom;
        InvalidateVisual();
    }

    // Re-centre on the player's current room (Home key / explicit recenter).
    public void FitToCurrent()
    {
        if (Layout is null) return;
        RoomKey origin = CurrentRoomKey ?? Layout.Origin;
        CenterOnRoom(origin);
    }

    // Explicit "show me where I am right now" — clears the browse-suppression
    // window (so live moves resume centring), moves the crawler selection to
    // the live current room, and centres on it. Drives both the Home key and
    // the right-click "Center on Player" menu (fired from the VM via the
    // window code-behind).
    public void RecenterOnPlayer()
    {
        _autoFollowSuppressedUntil = DateTime.MinValue;
        if (CurrentRoomKey is not { } cur) return;
        // On the displayed layout → just pan. Off it (the user floor-crawled
        // away to browse, so movement-driven re-rooting was left suppressed)
        // → ask the host to re-root the layout on the live room; a plain
        // CenterOnRoom would no-op on a key the browsed layout doesn't hold.
        if (Layout is { } layout && layout.Positions.ContainsKey(cur))
        {
            SelectedRoomKey = cur;
            CenterOnRoom(cur);
        }
        else
        {
            FloorChangeRequested?.Invoke(cur);
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

            if (StashRooms is not null && StashRooms.Contains(kvp.Value))
                DrawStashX(context, cell);

            if (DeathRooms is not null && DeathRooms.Contains(kvp.Value))
                DrawSkull(context, cell);

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

        // Pass 4: top-of-stack polylines. Loop-builder preview and the
        // loop-approach preview both draw in red with the same pen
        // (they're semantically distinct but visually identical:
        // "this is a planned cycle"). Walk preview red on top of those.
        // Running loop / walk in blue on top of everything so the
        // user's primary signal is the active automation.
        DrawPathPolyline(context, LoopBuilderPath,        LoopBuilderPen, tilePixels, cx, cy);
        DrawPathPolyline(context, LoopApproachPreviewPath, LoopBuilderPen, tilePixels, cx, cy);
        DrawPathPolyline(context, PreviewPath,            PreviewPathPen, tilePixels, cx, cy);
        DrawPathPolyline(context, LoopPath,               LoopPathPen,    tilePixels, cx, cy);
        // When AutoLair is driving the walker, the dedicated approach
        // path renders the FULL leg in orange and stays stable across
        // walker sub-step shrinkage — suppress the per-step WalkPath
        // so the two layers don't fight. WalkPathIsAutoLair stays as
        // a fallback for environments that haven't wired the new
        // property.
        if (AutoLairApproachPath is { Count: > 1 })
        {
            DrawPathPolyline(context, AutoLairApproachPath, AutoLairWalkPen, tilePixels, cx, cy);
        }
        else
        {
            IPen walkPen = WalkPathIsAutoLair ? AutoLairWalkPen : WalkPathPen;
            DrawPathPolyline(context, WalkPath, walkPen, tilePixels, cx, cy);
        }

        // Pass 5: numbered builder waypoint markers — drawn last so
        // they sit on top of every polyline and every room node fill.
        DrawLoopBuilderWaypoints(context, tilePixels, cx, cy);
        DrawAutoLairWaypoints(context, tilePixels, cx, cy);
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

    // Walks RoomLayout.EdgesFromCoord once and draws each connection exactly
    // once. Three cases per exit, resolved against the exit's REAL target room
    // (not just the grid-adjacent cell):
    //   - Grid-adjacent — target sits on the expected neighbouring cell: a
    //     clean continuous line between the two centres (no overlap seam, no
    //     thickness bump).
    //   - Gap-bridge — target is placed but NOT adjacent (the map data
    //     wouldn't tile flat): a dashed direct line between the two room
    //     centres, up to BridgeMaxCells apart, so the connection is visible
    //     instead of vanishing into a stub-to-nowhere.
    //   - Stub — target genuinely unplaced (dropped collision / blacklisted)
    //     or beyond the bridge distance: a short stub from the source centre
    //     to its cell edge.
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

            // Resolve the room sitting at the source cell once — its exit
            // table tells us where each exit ACTUALLY lands, which may
            // differ from the grid-adjacent cell when the layout couldn't
            // place the pair flat.
            bool haveSrcKey = Layout.CoordToRoom.TryGetValue(source, out RoomKey srcKey);
            Room? sourceRoom = Graph is not null && haveSrcKey ? Graph.GetRoom(srcKey) : null;

            foreach (Direction dir in entry.Value)
            {
                if (!TryPlanarOffset(dir, out int dx, out int dy)) continue;
                (int X, int Y) expected = (source.X + dx, source.Y + dy);

                // Where does this exit's real target room sit? Prefer the
                // graph-resolved target position; fall back to "whatever
                // occupies the expected adjacent cell" when the graph
                // isn't wired (keeps the old behaviour as a safety net).
                bool targetPlaced;
                (int X, int Y) actual;
                bool oneWay = false;
                if (sourceRoom is not null
                    && sourceRoom.Exits.TryGetValue(dir, out RoomExit exit))
                {
                    // Graph resolved the real target. If it isn't placed
                    // (dropped collision / blacklisted), this is a genuine
                    // dangling stub — don't fall back to the adjacent-cell
                    // heuristic, which could connect to the wrong room.
                    targetPlaced = Layout.Positions.TryGetValue(exit.Target, out (int X, int Y) ac);
                    actual = targetPlaced ? ac : expected;

                    // One-way when the destination carries no exit back to
                    // us. Class-hall entrances off the Crypt Shadowed Hall
                    // are the canonical case: the hall room exits into a
                    // class hall whose start room can't return, so a plain
                    // line would imply a round trip that doesn't exist. Also
                    // computed when the target is UNPLACED (a cast pocket mouth
                    // stubs to the cell edge) so its stub still carries the
                    // directional arrowhead.
                    if (haveSrcKey && Graph?.GetRoom(exit.Target) is { } destRoom)
                    {
                        oneWay = !HasExitTo(destRoom, srcKey);
                    }
                }
                else
                {
                    // No graph wired — legacy heuristic: trust whatever
                    // occupies the expected adjacent cell.
                    targetPlaced = Layout.CoordToRoom.ContainsKey(expected);
                    actual = expected;
                }

                // Dedupe on the real endpoints so the reciprocal exit
                // (drawn from the other room's side) doesn't double-draw.
                (int X, int Y) bCoord = targetPlaced ? actual : expected;
                ((int X, int Y) A, (int X, int Y) B) pair = SortPair(source, bCoord);
                if (!drawn.Add(pair)) continue;

                // Classify against the source side AND the real target
                // side (its reciprocal exit carries the same hint).
                // Priority at render time: trap > action > hidden >
                // plain. Trap-red is critical safety info ("don't
                // walk here unless disarmed"); action-magenta is
                // routing info ("needs a lever pulled first");
                // hidden-cyan is reveal info ("needs sea <dir>").
                bool isTrap = IsTrapEdge(source, dir)
                           || IsTrapEdge(bCoord, Opposite(dir));
                bool isAction = !isTrap
                    && (IsActionRequiredEdge(source, dir)
                     || IsActionRequiredEdge(bCoord, Opposite(dir)));
                bool isHidden = !isTrap && !isAction
                    && (IsHiddenEdge(source, dir)
                     || IsHiddenEdge(bCoord, Opposite(dir)));

                // A cast-on-walk exit gets a perpendicular spell-wall bar across
                // its connector regardless of the trap/action/hidden line colour
                // — the two convey different things (why the edge is special vs.
                // that a spell fires when you cross it).
                bool isSpell = IsSpellEdge(source, dir)
                            || IsSpellEdge(bCoord, Opposite(dir));

                switch (ClassifyConnection(targetPlaced, source, expected, actual, BridgeMaxCells))
                {
                    case ConnectionKind.Adjacent:
                    {
                        // Grid-adjacent — clean continuous connector.
                        IPen pen = isTrap ? TrapPen : isAction ? ActionPen : isHidden ? HiddenPen : ExitPen;
                        Point tgtPt = new(cx + actual.X * tilePixels, cy + actual.Y * tilePixels);
                        ctx.DrawLine(pen, srcPt, tgtPt);
                        if (oneWay) DrawOneWayArrow(ctx, pen, srcPt, tgtPt, tilePixels);
                        if (isSpell) DrawSpellWall(ctx, SpellWallPen, Midpoint(srcPt, tgtPt), dir, tilePixels);
                        break;
                    }
                    case ConnectionKind.Bridge:
                    {
                        // Connected but not grid-adjacent — dashed direct
                        // line between the two room centres, angled along
                        // the real connection instead of a stub into space.
                        IPen pen = isTrap ? TrapBridgePen : isAction ? ActionBridgePen
                                 : isHidden ? HiddenBridgePen : ExitBridgePen;
                        Point tgtPt = new(cx + actual.X * tilePixels, cy + actual.Y * tilePixels);
                        ctx.DrawLine(pen, srcPt, tgtPt);
                        if (oneWay) DrawOneWayArrow(ctx, pen, srcPt, tgtPt, tilePixels);
                        if (isSpell) DrawSpellWall(ctx, SpellWallPen, Midpoint(srcPt, tgtPt), dir, tilePixels);
                        break;
                    }
                    default:
                    {
                        // Target genuinely unplaced (dropped / blacklisted, a
                        // one-way cast pocket mouth, or too far to bridge) — stub
                        // to the cell edge. The spell-wall bar sits ON the cell
                        // divider (the stub's edge point), exactly where it lands
                        // between two placed rooms, rather than halfway down the
                        // stub. A one-way exit keeps its directional arrowhead —
                        // slightly enlarged and tipped at the divider — so a
                        // cut-off pocket still reads as "out this way, no return".
                        IPen pen = isTrap ? TrapPen : isAction ? ActionPen : isHidden ? HiddenPen : ExitPen;
                        Rect cell = ComputeCellRect(source, tilePixels, cx, cy);
                        DrawStub(ctx, pen, cell, srcX, srcY, dir);
                        if (StubEndpoint(cell, srcX, srcY, dir) is { } endPt)
                        {
                            if (oneWay) DrawOneWayArrow(ctx, pen, srcPt, endPt, tilePixels, scale: 1.3, tipBack: 0.0);
                            if (isSpell) DrawSpellWall(ctx, SpellWallPen, endPt, dir, tilePixels);
                        }
                        break;
                    }
                }
            }
        }
    }

    // How a single exit connection should be rendered.
    internal enum ConnectionKind
    {
        // Target on the expected neighbouring cell — clean line.
        Adjacent,
        // Target placed but not adjacent — dashed gap-bridge line.
        Bridge,
        // Target unplaced or too far — half-stub to the cell edge.
        Stub,
    }

    // Decide how to render an exit from source given where its target actually
    // landed. Pure geometry so the adjacent / bridge / stub rule is
    // unit-testable without a DrawingContext. A target on the expected cell
    // draws clean; one placed elsewhere within bridgeMaxCells (Chebyshev)
    // bridges; an unplaced or far target stubs.
    internal static ConnectionKind ClassifyConnection(
        bool targetPlaced,
        (int X, int Y) source,
        (int X, int Y) expected,
        (int X, int Y) actual,
        int bridgeMaxCells)
    {
        if (!targetPlaced) return ConnectionKind.Stub;
        if (actual == expected) return ConnectionKind.Adjacent;
        int chebyshev = Math.Max(Math.Abs(actual.X - source.X), Math.Abs(actual.Y - source.Y));
        return chebyshev <= bridgeMaxCells ? ConnectionKind.Bridge : ConnectionKind.Stub;
    }

    private bool IsTrapEdge((int X, int Y) coord, Direction dir)
    {
        if (Layout?.TrapEdgesFromCoord is null) return false;
        return Layout.TrapEdgesFromCoord.TryGetValue(coord, out IReadOnlySet<Direction>? set)
            && set.Contains(dir);
    }

    // True when the exit at coord heading dir fires a spell when walked
    // ("(Cast: ...)"). Drives the perpendicular spell-wall glyph. Same
    // pre-computed-set lookup as IsTrapEdge.
    private bool IsSpellEdge((int X, int Y) coord, Direction dir)
    {
        if (Layout?.SpellEdgesFromCoord is null) return false;
        return Layout.SpellEdgesFromCoord.TryGetValue(coord, out IReadOnlySet<Direction>? set)
            && set.Contains(dir);
    }

    // True when the exit at coord heading dir is a
    // RoomExitHint.MultiActionHidden — i.e. one or more in-room actions
    // (lever, switch, button …) must execute before the walker can traverse.
    // Queried at render time rather than pre-computed because action-required
    // edges are sparse and the visible-viewport edge count is bounded; doing a
    // dictionary hop per edge is fine and avoids dragging another pre-computed
    // set through RoomLayout.
    private bool IsActionRequiredEdge((int X, int Y) coord, Direction dir)
    {
        if (Graph is null || Layout is null) return false;
        if (!Layout.CoordToRoom.TryGetValue(coord, out RoomKey key)) return false;
        if (Graph.GetRoom(key) is not { } room) return false;
        if (!room.Exits.TryGetValue(dir, out RoomExit exit)) return false;
        return exit.Hint == RoomExitHint.MultiActionHidden;
    }

    // True when the exit at coord heading dir is a
    // RoomExitHint.SearchableHidden — present but masked from "Obvious exits:"
    // until the player runs `sea <dir>`. Same lookup pattern as
    // IsActionRequiredEdge.
    private bool IsHiddenEdge((int X, int Y) coord, Direction dir)
    {
        if (Graph is null || Layout is null) return false;
        if (!Layout.CoordToRoom.TryGetValue(coord, out RoomKey key)) return false;
        if (Graph.GetRoom(key) is not { } room) return false;
        if (!room.Exits.TryGetValue(dir, out RoomExit exit)) return false;
        return exit.Hint == RoomExitHint.SearchableHidden;
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
        RoomKey? prevKey = null;
        foreach (RoomKey key in path)
        {
            if (!Layout.Positions.TryGetValue(key, out (int X, int Y) coord))
            {
                prev = null;                                  // gap — skip until next placed room
                prevKey = null;
                continue;
            }
            Point here = new(cx + coord.X * tilePixels, cy + coord.Y * tilePixels);
            // Never draw a line across a teleport hop: the two rooms sit on
            // opposite sides of a portal, not on adjacent tiles. The route
            // into the teleport room and the route out of its destination each
            // draw on their own side, matching the "map to the teleport, then
            // resume from the far side" rule. Usually the destination isn't
            // even placed in this layout (the hop is one-way), so the gap is
            // implicit — this also covers a two-way shortcut that lands both
            // endpoints on the same plane.
            if (prev is { } p && !(prevKey is { } pk && IsTeleportHop(pk, key)))
                ctx.DrawLine(pen, p, here);
            prev = here;
            prevKey = key;
        }
    }

    private bool IsTeleportHop(RoomKey from, RoomKey to)
        => Graph?.GetRoom(from) is { } room
           && room.Exits.TryGetValue(Direction.Teleport, out RoomExit tele)
           && tele.Target.Equals(to);

    private static void DrawAvoidX(DrawingContext ctx, Rect cell)
        => DrawCellX(ctx, cell, AvoidXPen);

    // Golden cross-strokes for stash rooms. Same geometry as the avoid X —
    // the two markers share a shape so the user recognises them as "flagged
    // rooms" and the colour carries the action (red = avoid, gold = stash).
    private static void DrawStashX(DrawingContext ctx, Rect cell)
        => DrawCellX(ctx, cell, StashXPen);

    private static void DrawCellX(DrawingContext ctx, Rect cell, IPen pen)
    {
        double inset = cell.Width * 0.25;
        Point topLeft     = new(cell.X + inset, cell.Y + inset);
        Point topRight    = new(cell.Right - inset, cell.Y + inset);
        Point bottomLeft  = new(cell.X + inset, cell.Bottom - inset);
        Point bottomRight = new(cell.Right - inset, cell.Bottom - inset);
        ctx.DrawLine(pen, topLeft, bottomRight);
        ctx.DrawLine(pen, topRight, bottomLeft);
    }

    // A small bone-white skull marking a room that still holds an un-recovered
    // deathpile. Built from primitives — a rounded jaw block under a cranium
    // ellipse (the shared bone fill merges them into one silhouette), with dark
    // eye sockets, a nose triangle, and two tooth strokes — so it renders crisply
    // at any zoom without depending on an emoji glyph the map font may not carry.
    private static void DrawSkull(DrawingContext ctx, Rect cell)
    {
        double span = Math.Min(cell.Width, cell.Height);
        double mx = cell.X + cell.Width  / 2.0;
        double my = cell.Y + cell.Height / 2.0;
        double r  = span * 0.22;                     // cranium radius

        // Jaw first (drawn under the cranium) — a rounded block whose top tucks
        // behind the cranium so the two read as a single skull outline. The
        // cranium sits on the cell centre so the full glyph (cranium up to the
        // teeth) is vertically balanced in the square rather than riding high.
        double jawW = r * 1.25;
        double jawH = r * 0.95;
        Point head  = new(mx, my);
        Rect jaw = new(mx - jawW / 2.0, head.Y, jawW, jawH);
        ctx.DrawRectangle(SkullBoneFill, SkullRimPen, jaw, r * 0.35, r * 0.35);

        // Cranium.
        ctx.DrawEllipse(SkullBoneFill, SkullRimPen, head, r, r);

        // Eye sockets — dark hollows set into the cranium.
        double eyeR  = r * 0.34;
        double eyeDX = r * 0.44;
        double eyeY  = head.Y - r * 0.02;
        ctx.DrawEllipse(SkullSocketFill, null, new Point(mx - eyeDX, eyeY), eyeR, eyeR);
        ctx.DrawEllipse(SkullSocketFill, null, new Point(mx + eyeDX, eyeY), eyeR, eyeR);

        // Nose — a small dark downward triangle between and below the eyes.
        StreamGeometry nose = new();
        using (StreamGeometryContext g = nose.Open())
        {
            double top = head.Y + r * 0.35;
            g.BeginFigure(new Point(mx - r * 0.14, top), isFilled: true);
            g.LineTo(new Point(mx + r * 0.14, top));
            g.LineTo(new Point(mx, top + r * 0.34));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(SkullSocketFill, null, nose);

        // Two tooth strokes across the jaw so the mouth reads at a glance.
        double toothTop = head.Y + r * 0.62;
        double toothBot = toothTop + r * 0.4;
        ctx.DrawLine(SkullToothPen, new Point(mx - r * 0.2, toothTop), new Point(mx - r * 0.2, toothBot));
        ctx.DrawLine(SkullToothPen, new Point(mx + r * 0.2, toothTop), new Point(mx + r * 0.2, toothBot));
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

    // Draw a small numbered red circle on every loop-builder waypoint in click
    // order (1, 2, 3, ...). The marker overlays the cell so the user sees the
    // order at a glance even with the red polyline looping through the area.
    // Skipped when the waypoint isn't on the current layout (different floor /
    // disconnected island).
    private void DrawLoopBuilderWaypoints(DrawingContext ctx, double tilePixels, double cx, double cy)
    {
        if (LoopBuilderWaypoints is not { Count: > 0 } waypoints) return;
        if (Layout is null) return;

        double radius = Math.Clamp(tilePixels * 0.32, 6.0, 14.0);
        Typeface tf = new("Inter", FontStyle.Normal, FontWeight.Bold);
        double textSize = Math.Clamp(tilePixels * 0.28, 8.0, 12.0);

        for (int i = 0; i < waypoints.Count; i++)
        {
            RoomKey key = waypoints[i];
            if (!Layout.Positions.TryGetValue(key, out var coord)) continue;
            Rect cell = ComputeCellRect(coord, tilePixels, cx, cy);
            // Centre of the cell — the marker doubles as a clear
            // "this is your waypoint" indicator that's easy to spot
            // at any zoom level.
            Point centre = new(
                cell.X + cell.Width  / 2.0,
                cell.Y + cell.Height / 2.0);
            ctx.DrawEllipse(LoopBuilderWaypointFill, LoopBuilderWaypointRing,
                centre, radius, radius);

            string label = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            FormattedText ft = new(label, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, tf, textSize, LoopBuilderWaypointTextBrush);
            ctx.DrawText(ft, new Point(
                centre.X - ft.Width  / 2,
                centre.Y - ft.Height / 2));
        }
    }

    // Render numbered amber circles on every marked Auto-Lair room. Index
    // matches the CURRENT NAV row order supplied by
    // NavigationViewModel.AutoLairMarkedKeys so the map and the rail are
    // always in sync. Mirrors DrawLoopBuilderWaypoints with the amber theme.
    private void DrawAutoLairWaypoints(DrawingContext ctx, double tilePixels, double cx, double cy)
    {
        if (AutoLairWaypoints is not { Count: > 0 } waypoints) return;
        if (Layout is null) return;

        double radius = Math.Clamp(tilePixels * 0.32, 6.0, 14.0);
        Typeface tf = new("Inter", FontStyle.Normal, FontWeight.Bold);
        double textSize = Math.Clamp(tilePixels * 0.28, 8.0, 12.0);

        for (int i = 0; i < waypoints.Count; i++)
        {
            RoomKey key = waypoints[i];
            if (!Layout.Positions.TryGetValue(key, out var coord)) continue;
            Rect cell = ComputeCellRect(coord, tilePixels, cx, cy);
            Point centre = new(
                cell.X + cell.Width  / 2.0,
                cell.Y + cell.Height / 2.0);
            ctx.DrawEllipse(AutoLairWaypointFill, AutoLairWaypointRing,
                centre, radius, radius);

            string label = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            FormattedText ft = new(label, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, tf, textSize, AutoLairWaypointTextBrush);
            ctx.DrawText(ft, new Point(
                centre.X - ft.Width  / 2,
                centre.Y - ft.Height / 2));
        }
    }

    // Draws a half-line from the cell centre to one edge. Only used by the
    // dangling-exit branch in DrawAllExitLines — full lines between two placed
    // cells are drawn end-to-end without overlap. No StubOverlap here: there's
    // no adjacent stub to meet, so the segment ends flush at the cell edge.
    private static void DrawStub(DrawingContext ctx, IPen pen, Rect cell, double mx, double my, Direction dir)
    {
        if (StubEndpoint(cell, mx, my, dir) is { } end)
            ctx.DrawLine(pen, new Point(mx, my), end);
    }

    // Where a stub connector from the cell centre (mx, my) meets the cell edge
    // for a planar direction. Null for U / D (not rendered as stubs). Shared by
    // DrawStub and the spell-wall placement so both agree on the stub geometry.
    private static Point? StubEndpoint(Rect cell, double mx, double my, Direction dir) => dir switch
    {
        Direction.N  => new Point(mx, cell.Top),
        Direction.S  => new Point(mx, cell.Bottom),
        Direction.E  => new Point(cell.Right, my),
        Direction.W  => new Point(cell.Left,  my),
        Direction.NE => new Point(cell.Right, cell.Top),
        Direction.NW => new Point(cell.Left,  cell.Top),
        Direction.SE => new Point(cell.Right, cell.Bottom),
        Direction.SW => new Point(cell.Left,  cell.Bottom),
        _            => null,
    };

    private static Point Midpoint(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    // A short bar drawn perpendicular to an exit's direction, centred on the
    // connector between the two rooms, in the spell colour. Marks a
    // "(Cast: ...)" exit — crossing this connector fires a spell on the player —
    // so it reads as a wall standing in the doorway between the rooms.
    private static void DrawSpellWall(DrawingContext ctx, IPen pen, Point mid, Direction dir, double tilePixels)
    {
        if (!TryPlanarOffset(dir, out int dx, out int dy)) return;
        double len = Math.Sqrt((double)dx * dx + (double)dy * dy);
        if (len < 1e-3) return;
        // Rotate the direction 90° to get the wall's own axis, then normalise.
        double px = -dy / len;
        double py =  dx / len;
        // Span the bar the full height of a room-node square (DrawRoomNode's
        // 0.45·tile) so it reads as a wall sized to the rooms it stands between.
        double half = tilePixels * 0.45 / 2;
        Point a = new(mid.X - px * half, mid.Y - py * half);
        Point b = new(mid.X + px * half, mid.Y + py * half);
        ctx.DrawLine(pen, a, b);
    }

    // True when room has any exit whose target is key — i.e. the connection
    // is traversable in the reverse direction, so it isn't one-way.
    private static bool HasExitTo(Room room, RoomKey key)
    {
        foreach (RoomExit e in room.Exits.Values)
        {
            if (e.Target.Equals(key)) return true;
        }
        return false;
    }

    // A filled arrowhead near the target end of a one-way connector, pointing
    // from → to. Drawn in the connector's own colour (via pen.Brush) so trap /
    // action / hidden edges keep their meaning while gaining direction. The tip
    // sits short of the target centre so it clears the destination node fill —
    // except a stub connector (target unplaced) passes tipBack: 0 to seat the tip
    // right on the cell divider, since there's no node there to clear. scale
    // enlarges the head for the stub case so a cut-off pocket's arrow reads
    // clearly on the shorter half-tile connector.
    private static void DrawOneWayArrow(DrawingContext ctx, IPen pen, Point from, Point to, double tilePixels, double scale = 1.0, double? tipBack = null)
    {
        if (pen.Brush is null) return;

        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3) return;
        dx /= len;
        dy /= len;

        double head = Math.Clamp(tilePixels * 0.20, 4.0, 10.0) * scale;
        double back = tipBack ?? Math.Max(tilePixels * 0.30, head + 2.0);
        Point tip = new(to.X - dx * back, to.Y - dy * back);
        double px = -dy, py = dx;                    // perpendicular unit vector
        Point b1 = new(tip.X - dx * head + px * head * 0.6, tip.Y - dy * head + py * head * 0.6);
        Point b2 = new(tip.X - dx * head - px * head * 0.6, tip.Y - dy * head - py * head * 0.6);

        StreamGeometry geo = new();
        using (StreamGeometryContext g = geo.Open())
        {
            g.BeginFigure(tip, isFilled: true);
            g.LineTo(b1);
            g.LineTo(b2);
            g.EndFigure(true);
        }
        ctx.DrawGeometry(pen.Brush, null, geo);
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

    // Draws small filled triangles in the right corners of the node to
    // indicate U/D exits:
    //   - top-right green triangle when the room has an Up exit;
    //   - bottom-right yellow triangle when it has a Down exit;
    //   - both triangles when both exits are present (Up+Down rooms get the
    //     green corner on top of the existing UpDownFill or the classification
    //     fill — orange/green/yellow stay distinct).
    // Triangle size scales with the node so it stays glanceable on small cells
    // without crowding the centre dot of the player / destination marker.
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

    // Draws diagonal cross-hatch lines across the cell node to mark a room
    // with a CMD-driven teleport command. Clipped to the node so the lines
    // don't bleed onto neighbouring connectors. Spacing scales with the cell
    // so the pattern stays readable when zoomed in/out.
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
