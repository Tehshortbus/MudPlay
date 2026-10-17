using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Models.Settings;

namespace MudPlay.Controls;

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

    // The floors reached through up/down exits from Layout, already placed on its
    // grid where they don't cover it (OtherLevels.Build). Drawn dimmed under the
    // current floor; their rooms hover, click and route like the current floor's.
    public static readonly StyledProperty<RoomLayout?> OtherLevelsProperty =
        AvaloniaProperty.Register<MapControl, RoomLayout?>(nameof(OtherLevels));

    public static readonly StyledProperty<RoomKey?> CurrentRoomKeyProperty =
        AvaloniaProperty.Register<MapControl, RoomKey?>(nameof(CurrentRoomKey));

    public static readonly StyledProperty<RoomGraphManager?> GraphProperty =
        AvaloniaProperty.Register<MapControl, RoomGraphManager?>(nameof(Graph));

    public static readonly StyledProperty<LairDisplayMode> LairModeProperty =
        AvaloniaProperty.Register<MapControl, LairDisplayMode>(nameof(LairMode), defaultValue: LairDisplayMode.Uniform);

    // Per-room lair respawn times (seconds), consulted only in the Heat display
    // mode to pick a hot/cold fill. Rooms absent from this map (unresolved
    // timer) fall back to the flat lair colour.
    public static readonly StyledProperty<IReadOnlyDictionary<RoomKey, int>?> LairRespawnSecondsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyDictionary<RoomKey, int>?>(nameof(LairRespawnSeconds));

    // Whole-set longest lair respawn (seconds), the black endpoint of the Heat
    // tail. Kept stable across layout swaps so a lair's shade doesn't shift
    // with what's on screen.
    public static readonly StyledProperty<int> LairMaxRespawnSecondsProperty =
        AvaloniaProperty.Register<MapControl, int>(nameof(LairMaxRespawnSeconds));

    // Per-room max monster count, consulted only in the Count display mode to label
    // each lair with its "(Max N)". Rooms absent from this map draw no number.
    public static readonly StyledProperty<IReadOnlyDictionary<RoomKey, int>?> LairMonsterCountsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyDictionary<RoomKey, int>?>(nameof(LairMonsterCounts));

    public static readonly StyledProperty<bool> HighlightShopsProperty =
        AvaloniaProperty.Register<MapControl, bool>(nameof(HighlightShops), defaultValue: true);

    // Room-spell overlay mode. Mono = flat purple on every spell room (the original
    // behaviour); ByName = colour each spell room by which spell it carries (the
    // spell number hashed into a categorical palette); ByTeleport = colour it by
    // whether the spell teleports (SpellTeleportClasses); Off = no spell fill.
    public static readonly StyledProperty<SpellDisplayMode> SpellModeProperty =
        AvaloniaProperty.Register<MapControl, SpellDisplayMode>(nameof(SpellMode), defaultValue: SpellDisplayMode.Mono);

    // Each placed room spell's teleport class by spell number, read only in the
    // ByTeleport mode. A spell absent from it draws as one whose class is unknown.
    public static readonly StyledProperty<IReadOnlyDictionary<int, RoomSpellTeleport>?> SpellTeleportClassesProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyDictionary<int, RoomSpellTeleport>?>(nameof(SpellTeleportClasses));

    public static readonly StyledProperty<IReadOnlyList<RoomKey>?> WalkPathProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyList<RoomKey>?>(nameof(WalkPath));

    // Preview polyline for a queued (but not yet running) walk, in the preview
    // nav pen. Drawn beneath any live WalkPath so an active walk overlays its target without
    // flicker. NavigationViewModel sets this when a search-result selection
    // arms a destination.
    public static readonly StyledProperty<IReadOnlyList<RoomKey>?> PreviewPathProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyList<RoomKey>?>(nameof(PreviewPath));

    // The party leader's route we're following, rebuilt from their @path reply.
    // NavigationViewModel only sets it while our own engine is idle. Drawn in the
    // follow pen (cyan by default) — never our own walk / loop colours — so it can't be
    // mistaken for a route we're driving.
    public static readonly StyledProperty<IReadOnlyList<RoomKey>?> LeaderRoutePathProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyList<RoomKey>?>(nameof(LeaderRoutePath));

    // User-configured colour + thickness for each nav polyline (Settings → General,
    // Global tier). Null = every line draws its factory pen (NavLineDefaults). Bound
    // from NavigationViewModel, which keeps it live off the Global settings. Building
    // the pens from this is cached (see EnsureNavPens) so Render stays allocation-free.
    public static readonly StyledProperty<NavLineStyles?> NavLineStylesProperty =
        AvaloniaProperty.Register<MapControl, NavLineStyles?>(nameof(NavLineStyles));

    public static readonly StyledProperty<IReadOnlyList<RoomKey>?> LoopPathProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyList<RoomKey>?>(nameof(LoopPath));

    // The .mp import review's map comparison (Services.MapComparisonOverlay): the
    // recording's moves followed literally on our map, and the loop as converted.
    // Drawn over every other route in two fixed colours the review window's legend
    // names, the recorded one wide and translucent underneath so the two show as one
    // line where they agree and split apart where they don't.
    public static readonly StyledProperty<IReadOnlyList<RoomKey>?> ComparisonRecordedPathProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyList<RoomKey>?>(nameof(ComparisonRecordedPath));
    public static readonly StyledProperty<IReadOnlyList<RoomKey>?> ComparisonConvertedPathProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyList<RoomKey>?>(nameof(ComparisonConvertedPath));
    // Rooms where a recorded step couldn't be followed on our map — marked with a cross.
    public static readonly StyledProperty<IReadOnlyList<RoomKey>?> ComparisonStuckRoomsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyList<RoomKey>?>(nameof(ComparisonStuckRooms));

    public IReadOnlyList<RoomKey>? ComparisonRecordedPath
    {
        get => GetValue(ComparisonRecordedPathProperty);
        set => SetValue(ComparisonRecordedPathProperty, value);
    }

    public IReadOnlyList<RoomKey>? ComparisonConvertedPath
    {
        get => GetValue(ComparisonConvertedPathProperty);
        set => SetValue(ComparisonConvertedPathProperty, value);
    }

    public IReadOnlyList<RoomKey>? ComparisonStuckRooms
    {
        get => GetValue(ComparisonStuckRoomsProperty);
        set => SetValue(ComparisonStuckRoomsProperty, value);
    }

    // Kept in step with the legend in MpImportReviewWindow.axaml.
    private static readonly IPen ComparisonRecordedPen =
        new Pen(new SolidColorBrush(Color.Parse("#B3FF9800")), 7.0) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
    private static readonly IPen ComparisonConvertedPen =
        new Pen(new SolidColorBrush(Color.Parse("#00E5FF")), 2.5) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
    private static readonly IPen ComparisonStuckPen =
        new Pen(new SolidColorBrush(Color.Parse("#FF3B3B")), 2.5) { LineCap = PenLineCap.Round };

    // Live BFS-expanded preview of the loop being built in the Navigation
    // window's loop-builder strip. Drawn underneath any active LoopPath /
    // WalkPath so a running automation always overlays the build preview where
    // the two share a segment.
    public static readonly StyledProperty<IReadOnlyList<RoomKey>?> LoopBuilderPathProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyList<RoomKey>?>(nameof(LoopBuilderPath));

    // Per-click ordered waypoints surfaced as numbered red circles on the map
    // while the user is in loop-build mode. Separate from LoopBuilderPath
    // because the path is the BFS-filled flat room sequence (every
    // intermediate hop), whereas this list is only the rooms the user actually
    // clicked.
    public static readonly StyledProperty<IReadOnlyList<RoomKey>?> LoopBuilderWaypointsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyList<RoomKey>?>(nameof(LoopBuilderWaypoints));

    // Numbered green circles on each running-loop waypoint, in the same order as the
    // running CURRENT NAV rows, so the rail and the map line up while a loop runs.
    // Mirrors LoopBuilderWaypoints but green (running) rather than red (building).
    public static readonly StyledProperty<IReadOnlyList<RoomKey>?> LoopRunningWaypointsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyList<RoomKey>?>(nameof(LoopRunningWaypoints));

    // Loop preview drawn during the walker-driven approach phase of a loop run.
    // Distinct from LoopBuilderPath only by semantic (drawn with the same red
    // pen). Visible alongside the active WalkPath so the user sees both the
    // immediate walk to the start waypoint and the bigger-picture cycle that's
    // about to begin.
    public static readonly StyledProperty<IReadOnlyList<RoomKey>?> LoopApproachPreviewPathProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyList<RoomKey>?>(nameof(LoopApproachPreviewPath));

    public static readonly StyledProperty<IReadOnlySet<RoomKey>?> AvoidedRoomsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlySet<RoomKey>?>(nameof(AvoidedRooms));

    // Rooms holding a level gate — the doorway itself, which is still walkable
    // into. A map property, not a per-character one: it marks where the gates
    // are, not which refuse this character. Null/empty while the overlay is off.
    public static readonly StyledProperty<IReadOnlySet<RoomKey>?> LevelGatedRoomsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlySet<RoomKey>?>(nameof(LevelGatedRooms));

    // Rooms the user has marked as stash drops. Rendered with a gold outline so
    // the user can spot them at a glance. Game.Cash.StashRoomManager reads the
    // same set from Models.Profile.CharacterProfile.StashRooms and dispatches
    // hide N <coin> on entry.
    public static readonly StyledProperty<IReadOnlySet<RoomKey>?> StashRoomsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlySet<RoomKey>?>(nameof(StashRooms));

    // Rooms the user labeled for Roomba Mode (GH sorting). Rendered with a small
    // robot-head marker so a labeled destination reads at a glance.
    public static readonly StyledProperty<IReadOnlySet<RoomKey>?> GhRoomsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlySet<RoomKey>?>(nameof(GhRooms));

    // Roomba rooms the game refused a drop in during the current/last sweep. Drawn
    // as a ring around the robot so a glance at the map answers "which of my rooms
    // are out of space" — otherwise that only exists in the log.
    public static readonly StyledProperty<IReadOnlySet<RoomKey>?> GhFullRoomsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlySet<RoomKey>?>(nameof(GhFullRooms));

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
    // Recovered, recorded on the game data shown). Each gets a skull glyph so the
    // player can spot where a death is waiting to be recovered. Supplied by
    // NavigationViewModel from
    // DeathRecoveryManager.Records; a record flipping to Recovered drops its room
    // from the set and the skull clears on the next render.
    public static readonly StyledProperty<IReadOnlySet<RoomKey>?> DeathRoomsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlySet<RoomKey>?>(nameof(DeathRooms));

    // Rooms listed by any boss entry in the Bosses table — each gets a gold crown.
    // Rooms whose boss entry is flagged "stop before entering" ALSO appear in
    // StopBeforeBossRooms and get a red/black halt ring around the crown. Supplied
    // by NavigationViewModel from BossStore; refreshed on BossStore.Changed.
    public static readonly StyledProperty<IReadOnlySet<RoomKey>?> BossRoomsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlySet<RoomKey>?>(nameof(BossRooms));

    public static readonly StyledProperty<IReadOnlySet<RoomKey>?> StopBeforeBossRoomsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlySet<RoomKey>?>(nameof(StopBeforeBossRooms));

    // Rooms holding a trainer (game-data shops with ShopType == 8) — each gets an
    // up-chevron "level up here" icon. Supplied by NavigationViewModel from
    // TrainerCatalog; refreshed when the map re-lays out (game-data set swap).
    public static readonly StyledProperty<IReadOnlySet<RoomKey>?> TrainerRoomsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlySet<RoomKey>?>(nameof(TrainerRooms));

    public static readonly StyledProperty<bool> WalkPathIsAutoLairProperty =
        AvaloniaProperty.Register<MapControl, bool>(nameof(WalkPathIsAutoLair));

    public static readonly StyledProperty<RoomKey?> SelectedRoomKeyProperty =
        AvaloniaProperty.Register<MapControl, RoomKey?>(nameof(SelectedRoomKey));

    // Walk-to destination — blue-filled with a ring so it's immediately
    // recognisable as the goal, mirroring the "you are here" treatment.
    public static readonly StyledProperty<RoomKey?> DestinationRoomKeyProperty =
        AvaloniaProperty.Register<MapControl, RoomKey?>(nameof(DestinationRoomKey));

    // Rooms an @where reply just located — each flashed green for a few seconds,
    // then dropped independently by the VM as its own timer expires. A set so several
    // answered @where's light up at once (mirrors the other room-set overlays).
    public static readonly StyledProperty<IReadOnlySet<RoomKey>?> WhereTargetRoomsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlySet<RoomKey>?>(nameof(WhereTargetRooms));

    public static readonly StyledProperty<MudPlay.Models.Profile.KeyChord> UpStepChordProperty =
        AvaloniaProperty.Register<MapControl, MudPlay.Models.Profile.KeyChord>(nameof(UpStepChord),
            new MudPlay.Models.Profile.KeyChord(Key.PageUp));

    public static readonly StyledProperty<MudPlay.Models.Profile.KeyChord> DownStepChordProperty =
        AvaloniaProperty.Register<MapControl, MudPlay.Models.Profile.KeyChord>(nameof(DownStepChord),
            new MudPlay.Models.Profile.KeyChord(Key.PageDown));

    public static readonly StyledProperty<IReadOnlyDictionary<Direction, MudPlay.Models.Profile.KeyChord>?> CompassChordsProperty =
        AvaloniaProperty.Register<MapControl, IReadOnlyDictionary<Direction, MudPlay.Models.Profile.KeyChord>?>(nameof(CompassChords));

    public RoomLayout? Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public RoomLayout? OtherLevels
    {
        get => GetValue(OtherLevelsProperty);
        set => SetValue(OtherLevelsProperty, value);
    }

    // The layout the exit-line and room-node drawing reads: Layout, or OtherLevels
    // while the static layer draws the dimmed floors (the same drawing, a different
    // set of cells).
    private RoomLayout? _drawingOtherLevels;

    // Whether route lines run on through the other floors' rooms; off keeps them
    // to the floor shown (GlobalSettings.MapRouteLinesOnOtherFloors).
    public static readonly StyledProperty<bool> RouteLinesOnOtherLevelsProperty =
        AvaloniaProperty.Register<MapControl, bool>(nameof(RouteLinesOnOtherLevels), defaultValue: true);

    public bool RouteLinesOnOtherLevels
    {
        get => GetValue(RouteLinesOnOtherLevelsProperty);
        set => SetValue(RouteLinesOnOtherLevelsProperty, value);
    }
    private RoomLayout? DrawLayout => _drawingOtherLevels ?? Layout;

    // Where a room sits on the map: on the current floor, else on another floor
    // that shows.
    private bool TryGetCoord(RoomKey key, out (int X, int Y) coord)
    {
        if (Layout is { } layout && layout.Positions.TryGetValue(key, out coord)) return true;
        if (OtherLevels is { } other && other.Positions.TryGetValue(key, out coord)) return true;
        coord = default;
        return false;
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

    public LairDisplayMode LairMode
    {
        get => GetValue(LairModeProperty);
        set => SetValue(LairModeProperty, value);
    }

    public IReadOnlyDictionary<RoomKey, int>? LairRespawnSeconds
    {
        get => GetValue(LairRespawnSecondsProperty);
        set => SetValue(LairRespawnSecondsProperty, value);
    }

    public int LairMaxRespawnSeconds
    {
        get => GetValue(LairMaxRespawnSecondsProperty);
        set => SetValue(LairMaxRespawnSecondsProperty, value);
    }

    public IReadOnlyDictionary<RoomKey, int>? LairMonsterCounts
    {
        get => GetValue(LairMonsterCountsProperty);
        set => SetValue(LairMonsterCountsProperty, value);
    }

    public bool HighlightShops
    {
        get => GetValue(HighlightShopsProperty);
        set => SetValue(HighlightShopsProperty, value);
    }

    public SpellDisplayMode SpellMode
    {
        get => GetValue(SpellModeProperty);
        set => SetValue(SpellModeProperty, value);
    }

    public IReadOnlyDictionary<int, RoomSpellTeleport>? SpellTeleportClasses
    {
        get => GetValue(SpellTeleportClassesProperty);
        set => SetValue(SpellTeleportClassesProperty, value);
    }

    public IReadOnlyList<RoomKey>? WalkPath
    {
        get => GetValue(WalkPathProperty);
        set => SetValue(WalkPathProperty, value);
    }

    public IReadOnlyList<RoomKey>? LeaderRoutePath
    {
        get => GetValue(LeaderRoutePathProperty);
        set => SetValue(LeaderRoutePathProperty, value);
    }

    public IReadOnlyList<RoomKey>? PreviewPath
    {
        get => GetValue(PreviewPathProperty);
        set => SetValue(PreviewPathProperty, value);
    }

    public NavLineStyles? NavLineStyles
    {
        get => GetValue(NavLineStylesProperty);
        set => SetValue(NavLineStylesProperty, value);
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

    public IReadOnlyList<RoomKey>? LoopRunningWaypoints
    {
        get => GetValue(LoopRunningWaypointsProperty);
        set => SetValue(LoopRunningWaypointsProperty, value);
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

    public IReadOnlySet<RoomKey>? LevelGatedRooms
    {
        get => GetValue(LevelGatedRoomsProperty);
        set => SetValue(LevelGatedRoomsProperty, value);
    }

    public IReadOnlySet<RoomKey>? StashRooms
    {
        get => GetValue(StashRoomsProperty);
        set => SetValue(StashRoomsProperty, value);
    }

    public IReadOnlySet<RoomKey>? GhFullRooms
    {
        get => GetValue(GhFullRoomsProperty);
        set => SetValue(GhFullRoomsProperty, value);
    }

    public IReadOnlySet<RoomKey>? GhRooms
    {
        get => GetValue(GhRoomsProperty);
        set => SetValue(GhRoomsProperty, value);
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

    public IReadOnlySet<RoomKey>? BossRooms
    {
        get => GetValue(BossRoomsProperty);
        set => SetValue(BossRoomsProperty, value);
    }

    public IReadOnlySet<RoomKey>? StopBeforeBossRooms
    {
        get => GetValue(StopBeforeBossRoomsProperty);
        set => SetValue(StopBeforeBossRoomsProperty, value);
    }

    public IReadOnlySet<RoomKey>? TrainerRooms
    {
        get => GetValue(TrainerRoomsProperty);
        set => SetValue(TrainerRoomsProperty, value);
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

    public IReadOnlySet<RoomKey>? WhereTargetRooms
    {
        get => GetValue(WhereTargetRoomsProperty);
        set => SetValue(WhereTargetRoomsProperty, value);
    }

    // Fired when the user steps the crawler up or down — the layout host is
    // expected to rebuild from the new room (which lives on a different floor
    // and therefore isn't in the current layout).
    public event Action<RoomKey>? FloorChangeRequested;

    // Key chord that steps the crawler one floor up. Bound from the user's
    // macro configured to send u to the game so the same chord drives both
    // in-game movement and the map crawler. Defaults to PageUp when the macro
    // isn't bound.
    public MudPlay.Models.Profile.KeyChord UpStepChord
    {
        get => GetValue(UpStepChordProperty);
        set => SetValue(UpStepChordProperty, value);
    }

    public MudPlay.Models.Profile.KeyChord DownStepChord
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
    public IReadOnlyDictionary<Direction, MudPlay.Models.Profile.KeyChord>? CompassChords
    {
        get => GetValue(CompassChordsProperty);
        set => SetValue(CompassChordsProperty, value);
    }

    // ----- view-state ------------------------------------------------

    // World tile size in layout units. Multiplied by _zoom to get screen pixels.
    private const double TileWorldSize = 24.0;
    // Zoomed out to MinZoom a cell is under 4 px: rooms read as dots, enough to see
    // a whole area's shape and pick where to go.
    private const double MinZoom = 0.15;
    private const double MinTilePixels = 3.0;
    // Below this a room is too small to read a marker or an arrowhead on, and
    // drawing thousands of them is what made a zoomed-out redraw slow.
    private const double MinDetailTilePixels = 8.0;

    private double _zoom = 1.2;
    private double _panX;
    private double _panY;

    // Left-button drag/click disambiguation.
    private bool _leftPressed;
    private bool _isDragging;
    private Point _pressPos;

    // Waypoint-chip drag: a left press on a building waypoint's chip drags the chip
    // instead of panning. _chipDragFrom is the room pressed; _chipDragging turns on
    // past the drag threshold; _chipDropTarget is the room under the cursor.
    private RoomKey? _chipDragFrom;
    private bool _chipDragging;
    private RoomKey? _chipDropTarget;
    private Point _chipDragPoint;

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

    // How long the hold lasts. The user's Settings → Other value
    // (GlobalSettings.MapRecenterHoldSeconds), bound in by the Navigation window.
    public static readonly StyledProperty<int> AutoFollowHoldSecondsProperty =
        AvaloniaProperty.Register<MapControl, int>(nameof(AutoFollowHoldSeconds), 15);

    public int AutoFollowHoldSeconds
    {
        get => GetValue(AutoFollowHoldSecondsProperty);
        set => SetValue(AutoFollowHoldSecondsProperty, value);
    }

    private void SuppressAutoFollow()
        => _autoFollowSuppressedUntil = DateTime.UtcNow.AddSeconds(Math.Max(0, AutoFollowHoldSeconds));

    // True while the player-room auto-centre is paused because the user is
    // actively browsing (pan-drag, zoom, crawler step, or an explicit view
    // re-root). Time-boxed — lapses AutoFollowHoldSeconds after the last
    // browse gesture. The Navigation VM reads this to defer a movement-driven
    // layout rebuild while the user is looking elsewhere, so a stairs / U-D step
    // doesn't yank the map back mid-browse; once it lapses, the next step rebounds.
    public bool IsAutoFollowSuppressed
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
    // The room fills are readable outside the control so the by-teleport swatches
    // can be held to a minimum distance from every one of them.
    internal static readonly IBrush RoomFill     = new SolidColorBrush(Color.Parse("#9B9B9B"));
    // Darkened + shifted off pure yellow (lower green channel) so the current-room
    // fill doesn't blend into a down-exit room's #DCDC00 yellow where the two abut at
    // a shared corner; red kept high so "you are here" still reads as a punchy gold.
    internal static readonly IBrush CurrentFill  = new SolidColorBrush(Color.Parse("#E0A000"));
    internal static readonly IBrush LairFill     = new SolidColorBrush(Color.Parse("#8E4F7B"));
    internal static readonly IBrush ShopFill     = new SolidColorBrush(Color.Parse("#4A7791"));
    internal static readonly IBrush SpellFill    = new SolidColorBrush(Color.Parse("#6428A0"));
    // Vertical-exit indicators: green = up only,
    // yellow = down only, orange = both. Applied as the room-node fill
    // when no higher-priority highlight (current / auto-lair / lair /
    // shop / spell) takes the cell.
    internal static readonly IBrush UpFill       = new SolidColorBrush(Color.Parse("#00C800"));
    internal static readonly IBrush DownFill     = new SolidColorBrush(Color.Parse("#DCDC00"));
    internal static readonly IBrush UpDownFill   = new SolidColorBrush(Color.Parse("#FFB432"));
    // Dark rim for the U/D corner-badge triangles so the yellow down-badge (and green
    // up-badge) read against ANY cell fill — in particular the gold current-room fill,
    // where a rimless yellow triangle blends into the highlight. Same trick the skull /
    // trainer chevron use to stay legible over the current-room marker.
    private static readonly IPen   VerticalBadgeEdgePen = new Pen(new SolidColorBrush(Color.Parse("#141414")), 1.0)
    {
        LineJoin = PenLineJoin.Round,
    };

    private static readonly IPen   TileBorderPen = new Pen(new SolidColorBrush(Color.Parse("#2A2A2A")), 1.0);
    private static readonly IPen   ExitPen       = new Pen(new SolidColorBrush(Color.Parse("#C0C0C0")), 2.0);
    // Red for a trapped exit — same width as the walk-to / preview route lines (Nav line
    // default 3.0). Matching widths means a route running over a trap covers the solid
    // base exactly (no red edges poking out to frame it), while a standalone trap — base
    // and dashed overlay the same width — reads as a uniform solid red line.
    private static readonly IPen   TrapPen       = new Pen(new SolidColorBrush(Color.Parse("#DC3C3C")), 3.0);
    // Dark magenta for exits that need a command/action to cross rather than a
    // plain directional step — RoomExitHint.MultiActionHidden (an in-room lever /
    // ask-door acted on first, e.g. map 9 / room 1032's east exit on v1.11p) AND
    // RoomExitHint.Text (a named-command exit like `go path`, e.g. map 1 / room
    // 1403's NE, where the walker types the word instead of the compass step).
    // Distinct from trap red (more dangerous, takes precedence at render time).
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
    // Dashed red for the ON-TOP trap overlay: laid over a travel polyline, the dashes let
    // the route line show through, so the trapped exit and the route crossing it read at
    // once. Flat caps (not round) so the dash lengths are literal — round caps extend each
    // red dash into the gap, over-reddening it. The {3.25, 1.75} pattern is ~65% red /
    // 35% route showing through. A standalone trap still looks solid: the solid base line
    // under the room nodes fills the dash gaps; only a route-crossed trap (base masked by
    // the route) shows the dashes.
    private static readonly IPen   TrapOverlayPen  = new Pen(new SolidColorBrush(Color.Parse("#DC3C3C")), 3.0) { DashStyle = new DashStyle(new double[] { 3.25, 1.75 }, 0), LineCap = PenLineCap.Flat };
    private static readonly IPen   ActionBridgePen = new Pen(new SolidColorBrush(Color.Parse("#8B008B")), 1.5) { DashStyle = BridgeDash, LineCap = PenLineCap.Round };
    private static readonly IPen   HiddenBridgePen = new Pen(new SolidColorBrush(Color.Parse("#008B8B")), 1.5) { DashStyle = BridgeDash, LineCap = PenLineCap.Round };
    // Max grid distance (Chebyshev) a gap-bridge line spans; beyond this the
    // rooms are too far apart to bridge cleanly and we fall back to a dangling
    // stub rather than shoot a line across the map.
    private const int BridgeMaxCells = 4;
    private static readonly IPen   RoomBorderPen = new Pen(new SolidColorBrush(Color.Parse("#D0D0D0")), 1.0);
    private static readonly IPen   CurrentPen    = new Pen(new SolidColorBrush(Color.Parse("#FFD24D")), 2.0);
    private static readonly IPen   LairBorderPen  = new Pen(new SolidColorBrush(Color.Parse("#B36F9C")), 1.5);
    // Lair-heat colours (LairDisplayMode.Heat). MajorMUD lair respawns start at
    // 30s and step in 30s intervals, so a lair's colour is keyed to its 30s
    // bucket, not a continuous gradient: bucket 0 = 30s .. bucket 9 = 5min are
    // the fixed red->purple rainbow below (one distinct hue per step). Lairs
    // slower than 5min ("the tail") fade purple->black over (5min, whole-set
    // max], so the single longest lair in the game data lands on black; those
    // shades are built on demand and memoised in _tailHeat. Borders are
    // lightened tints so a near-black node stays visible on the dark map.
    private const int HeatBaseSeconds  = 30;
    private const int HeatStepSeconds  = 30;
    internal static readonly string[] HeatFixedHex =
    {
        "#E64A4A", // 0:30 red
        "#F07818", // 1:00 orange
        "#C8A000", // 1:30 amber-gold (darkened + shifted off pure yellow so a lair heat node isn't mistaken for a down-exit room, whose fill is #DCDC00)
        "#A6C82A", // 2:00 yellow-green
        "#43B84E", // 2:30 green
        "#22B58E", // 3:00 green / light-blue mix (teal)
        "#34B9DE", // 3:30 light blue
        "#3B7FE6", // 4:00 darker blue
        "#6B54DC", // 4:30 blue-purple
        "#A24BD6", // 5:00 purple
    };
    private static readonly (IBrush fill, IPen pen)[] HeatFixed = BuildHeatFixed();
    // Seconds at the last fixed stop (5min); the purple->black tail is anchored
    // here.
    private static readonly int HeatFixedMaxSeconds =
        HeatBaseSeconds + (HeatFixedHex.Length - 1) * HeatStepSeconds;
    // Memo for the >5min tail, keyed by (snapped-seconds, whole-set-max). Read
    // and written on the UI thread only (render path), so no lock needed.
    private static readonly Dictionary<(int snapped, int max), (IBrush fill, IPen pen)> _tailHeat = new();

    private static (IBrush, IPen)[] BuildHeatFixed()
    {
        var stops = new (IBrush, IPen)[HeatFixedHex.Length];
        for (int i = 0; i < HeatFixedHex.Length; i++)
        {
            Color c = Color.Parse(HeatFixedHex[i]);
            stops[i] = (new SolidColorBrush(c), new Pen(new SolidColorBrush(LightenToward(c, Colors.White, 0.35)), 1.5));
        }
        return stops;
    }

    // Categorical palette for the room-spell "by name" overlay (SpellDisplayMode.ByName).
    // A room's spell record number is hashed into these swatches so differing room
    // spells clustered together read as different colours — distinct, saturated hues
    // that stay legible on the dark map, borders lightened like the heat stops. There
    // are far more distinct room spells than slots, so rare spells may share a colour;
    // the hover tooltip's "Room Spell: <name>" line is the key that disambiguates.
    // Every swatch is deliberately CHROMATIC — no neutral grey — so a spell room never
    // blends into the normal room fill (RoomFill #9B9B9B); a near-grey swatch made
    // icy-mountain spell rooms vanish under the by-name filter. The chroma floor is
    // pinned by MapSpellPaletteTests.
    internal static readonly string[] SpellCategoryHex =
    {
        "#E6194B", "#3CB44B", "#4363D8", "#F58231", "#911EB4",
        "#42D4F4", "#F032E6", "#BFEF45", "#F58AB0", "#469990",
        "#C9A0FF", "#B87333", "#FFD21E", "#B03060", "#5AC8A8",
        "#9AA032", "#E8944A", "#4A6FE3", "#7C4DFF", "#2AA5C0",
    };
    private static readonly (IBrush fill, IPen pen)[] SpellCategory = BuildSpellSwatches(SpellCategoryHex);

    // The room-spell "by teleport" overlay (SpellDisplayMode.ByTeleport), indexed by
    // RoomSpellTeleport: green for a spell with no teleport, yellow for one that
    // teleports on a condition, red for one that teleports outright or on a roll.
    // Each sits off the pure hue a neighbouring cue already owns, since a spell room
    // can stand beside any of them: the up-exit room's #00C800 and the 2:30 lair's
    // #43B84E (so a forest green), the down-exit room's #DCDC00 and the current
    // room's gold (so a pale lemon), the 30-second lair's #E64A4A (so a crimson).
    // MapSpellPaletteTests pins the gaps. A spell whose class is unknown has no
    // swatch here: it keeps the flat spell purple, which claims nothing either way.
    internal static readonly string[] SpellTeleportHex = { "#218A45", "#F2DC5A", "#C81E3C" };
    private static readonly (IBrush fill, IPen pen)[] SpellTeleportSwatch = BuildSpellSwatches(SpellTeleportHex);

    private static (IBrush, IPen)[] BuildSpellSwatches(string[] hexes)
    {
        var stops = new (IBrush, IPen)[hexes.Length];
        for (int i = 0; i < hexes.Length; i++)
        {
            Color c = Color.Parse(hexes[i]);
            stops[i] = (new SolidColorBrush(c), new Pen(new SolidColorBrush(LightenToward(c, Colors.White, 0.35)), 1.5));
        }
        return stops;
    }

    // Stable, well-distributed colour for a room-spell record number — a
    // multiplicative (Knuth) hash so even adjacent spell numbers land on different
    // swatches rather than clustering.
    private static (IBrush fill, IPen pen) SpellColorFor(int spellNumber)
    {
        uint h = unchecked((uint)spellNumber * 2654435761u);
        return SpellCategory[(int)(h % (uint)SpellCategory.Length)];
    }

    private (IBrush fill, IPen pen) SpellTeleportColorFor(int spellNumber)
    {
        return SpellTeleportClasses is { } classes && classes.TryGetValue(spellNumber, out RoomSpellTeleport found)
            && found != RoomSpellTeleport.Unknown
            ? SpellTeleportSwatch[(int)found]
            : (SpellFill, SpellBorderPen);
    }

    private static Color LightenToward(Color a, Color b, double t)
    {
        byte Mix(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromArgb(255, Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B));
    }

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
    // Nav-line pens — built from the bound NavLineStyles (user colour + thickness),
    // falling back to NavLineDefaults for any unset line. Instance (not static) so
    // each map reflects the live Global settings; cached and rebuilt only when the
    // bound styles instance changes, so Render allocates no pens per frame. Default
    // colour rationale lives in NavLineDefaults — the engine schema is walk-to blue,
    // loop green, preview amber, loop-builder red, auto-lair orange, a followed
    // leader's route cyan, and the loop colour is echoed by the Navigation rail headers +
    // Loops/Auto-Lairs list rows.
    private NavLineStyles? _navPensBuiltFrom;
    private bool _navPensReady;
    private IPen _walkPathPen = null!;
    private IPen _loopPathPen = null!;
    private IPen _previewPathPen = null!;
    private IPen _loopBuilderPen = null!;
    private IPen _autoLairWalkPen = null!;
    private IPen _followRoutePen = null!;

    // Rebuild the six nav pens if the bound styles instance changed (or first draw).
    private void EnsureNavPens()
    {
        if (_navPensReady && ReferenceEquals(_navPensBuiltFrom, NavLineStyles)) return;
        _navPensBuiltFrom = NavLineStyles;
        _navPensReady = true;
        _walkPathPen     = BuildNavPen(NavLineKind.Goto);
        _loopPathPen     = BuildNavPen(NavLineKind.Loop);
        _previewPathPen  = BuildNavPen(NavLineKind.Preview);
        _loopBuilderPen  = BuildNavPen(NavLineKind.LoopBuilder);
        _autoLairWalkPen = BuildNavPen(NavLineKind.AutoLair);
        _followRoutePen  = BuildNavPen(NavLineKind.FollowRoute);
    }

    private IPen BuildNavPen(NavLineKind kind)
    {
        (string defHex, double defThick, _) = NavLineDefaults.For(kind);
        (string hex, double thickness) = NavLineStyles is { } s ? s.Resolve(kind) : (defHex, defThick);
        Color colour;
        try { colour = Color.Parse(hex); }
        catch { colour = Color.Parse(defHex); }   // hand-edited bad hex → fall back to default
        double t = Math.Clamp(thickness, NavLineDefaults.MinThickness, NavLineDefaults.MaxThickness);
        return new Pen(new SolidColorBrush(colour), t)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
    }

    // The room a dragged waypoint chip would land on.
    private static readonly IPen ChipDropPen =
        new Pen(new SolidColorBrush(Color.Parse("#E66C5A")), 2.5) { DashStyle = new DashStyle(new double[] { 3, 2 }, 0) };

    // Fill brush for the per-waypoint numbered circle markers.
    private static readonly IBrush LoopBuilderWaypointFill =
        new SolidColorBrush(Color.Parse("#E66C5A"));
    private static readonly IPen   LoopBuilderWaypointRing =
        new Pen(new SolidColorBrush(Color.Parse("#FFFFFFFF")), 1.5);
    // Running-loop numbered overlay — green to match the running loop line + rail
    // (NavLineKind.Loop / AccentGreenBrush).
    private static readonly IBrush LoopRunningWaypointFill =
        new SolidColorBrush(Color.Parse("#7AB870"));
    private static readonly IPen   LoopRunningWaypointRing =
        new Pen(new SolidColorBrush(Color.Parse("#FFFFFFFF")), 1.5);

    // Auto-Lair numbered overlay — amber to match the section theme.
    private static readonly IBrush AutoLairWaypointFill =
        new SolidColorBrush(Color.Parse("#DC821E"));
    private static readonly IPen   AutoLairWaypointRing =
        new Pen(new SolidColorBrush(Color.Parse("#FFFFFFFF")), 1.5);

    // Black numbers on every waypoint bubble — reads cleanly on all three fills (green /
    // red / amber) without the fussy white-on-fill outline.
    private static readonly IBrush NumberedWaypointTextBrush =
        new SolidColorBrush(Color.Parse("#FF000000"));

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
    // ring + the cyan selection ring so the user can scan for stashes at a
    // glance.
    private static readonly IPen   StashXPen      = new Pen(new SolidColorBrush(Color.Parse("#FFFFD24E")), 2.5)
    {
        LineCap = PenLineCap.Round,
    };
    // @where target flash — a translucent green fill under a bright green ring, so an
    // answered "where are you?" reads as a lit-up marked square the moment it lands.
    // Cleared when the VM's per-room highlight timer expires.
    private static readonly IBrush WhereTargetFill = new SolidColorBrush(Color.Parse("#8833DD66"));
    private static readonly IPen   WhereTargetPen  = new Pen(new SolidColorBrush(Color.Parse("#FF33DD66")), 2.5);

    // Level-gate marker — a filled amber wedge in the room node's top-left
    // corner, for a room you can walk into whose way onward is shut to anyone
    // outside the gate's level window. Deliberately NOT a coloured exit stub:
    // stubs mean traps, and
    // reusing that vocabulary for a level gate reads as danger rather than a
    // locked door. A corner badge is a property OF the room, which is what a
    // gate is here; it shares the U/D badges' dark rim and takes the free
    // top-left corner so position alone separates the three.
    private static readonly IBrush LevelGateFill = new SolidColorBrush(Color.Parse("#FFE0A020"));
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
    // Boss-room crown — a gold silhouette drawn from primitives (like the skull) so
    // it reads at any zoom without an emoji glyph. The thin darker rim keeps it
    // legible over the amber current-room fill. The stop-before halt ring is a red
    // ring backed by a thicker black edge so it stands out on any cell fill.
    private static readonly IBrush CrownFill      = new SolidColorBrush(Color.Parse("#F1C34A"));
    private static readonly IPen   CrownRimPen    = new Pen(new SolidColorBrush(Color.Parse("#7C5C14")), 1.0);
    private static readonly IPen   StopRingPen     = new Pen(new SolidColorBrush(Color.Parse("#E64545")), 2.0);
    private static readonly IPen   StopRingEdgePen = new Pen(new SolidColorBrush(Color.Parse("#141414")), 3.6);

    // Trainer-room "level up here" triple up-chevron — a bright level-up green over a
    // dark edge so the carets read on any cell fill. Distinct from crown gold, shop
    // cyan and lair magenta.
    private static readonly IPen   TrainerChevronPen = new Pen(new SolidColorBrush(Color.Parse("#3FD07A")), 2.4)
    {
        LineJoin = PenLineJoin.Round,
        LineCap  = PenLineCap.Round,
    };
    private static readonly IPen   TrainerChevronEdgePen = new Pen(new SolidColorBrush(Color.Parse("#0E3A22")), 4.2)
    {
        LineJoin = PenLineJoin.Round,
        LineCap  = PenLineCap.Round,
    };
    private static readonly IBrush SeqNumberFill  = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush AutoLairFill   = new SolidColorBrush(Color.Parse("#DC821E"));
    private static readonly IPen   AutoLairBorder = new Pen(new SolidColorBrush(Color.Parse("#FFA500")), 2.0)
    {
        DashStyle = new DashStyle(new double[] { 3, 2 }, 0),
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
        _staticLayer = new MapLayer(DrawStaticLayer)
        {
            CacheMode = new BitmapCache(),
            RenderTransformOrigin = RelativePoint.TopLeft,
        };
        _dynamicLayer = new MapLayer(DrawDynamicLayer);
        VisualChildren.Add(_staticLayer);
        VisualChildren.Add(_dynamicLayer);
        InvalidateStatic("first draw");
        AffectsRender<MapControl>(LayoutProperty, CurrentRoomKeyProperty, DestinationRoomKeyProperty, GraphProperty,
            LairModeProperty, LairRespawnSecondsProperty, LairMaxRespawnSecondsProperty, LairMonsterCountsProperty,
            HighlightShopsProperty, SpellModeProperty, SpellTeleportClassesProperty,
            WalkPathProperty, LoopPathProperty, LoopBuilderPathProperty, LoopBuilderWaypointsProperty,
            LoopRunningWaypointsProperty,
            AutoLairWaypointsProperty, AutoLairApproachPathProperty,
            LoopApproachPreviewPathProperty, AvoidedRoomsProperty, LevelGatedRoomsProperty, StashRoomsProperty, GhRoomsProperty, GhFullRoomsProperty, LoopSequenceNumbersProperty,
            AutoLairRoomsProperty, WalkPathIsAutoLairProperty, SelectedRoomKeyProperty,
            PreviewPathProperty, LeaderRoutePathProperty, TeleportRoomsProperty, DeathRoomsProperty,
            BossRoomsProperty, StopBeforeBossRoomsProperty, TrainerRoomsProperty,
            WhereTargetRoomsProperty, NavLineStylesProperty,
            ComparisonRecordedPathProperty, ComparisonConvertedPathProperty, ComparisonStuckRoomsProperty);

        // Auto-centre on the player's current room every time it
        // changes — but only when the
        // user isn't actively browsing. Drag-pan, zoom, and crawler
        // steps all arm the AutoFollowHoldSeconds suppression window during which
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
        // the room it was built around. Three kinds of rebuild reach
        // here, told apart by that origin:
        //   - Movement rebuild (walked onto a new floor, reconnect,
        //     party-follow drag / leader loop): origin == CurrentRoomKey.
        //     Re-centre on it, but honour the browse-suppression window
        //     so a party member's move doesn't yank the view while the
        //     user is actively looking around. This is also what fixes
        //     the old bug where crossing a U/D centred on a stale
        //     destination selection instead of where we actually landed.
        //   - Explicit re-root (PageUp/Down floor-crawl, search jump):
        //     origin is a room the user isn't standing on. Honour it
        //     immediately AND arm the browse window so the view holds there
        //     for the suppression interval, then rebounds to the player —
        //     mirroring a pan-drag. Without this a search jump re-rooted the
        //     layout off the player permanently and never rebounded.
        //   - A redraw of the same map (blacklist edit, graph reload): same origin.
        //     The view stays exactly where the user left it.
        LayoutProperty.Changed.AddClassHandler<MapControl>((c, e) =>
        {
            if (c.Layout is not { } layout) return;
            RoomKey origin = layout.Origin;
            if (e.OldValue is RoomLayout previous && previous.Origin.Equals(origin)) return;
            if (c.CurrentRoomKey is { } cur && cur.Equals(origin))
            {
                if (c.IsAutoFollowSuppressed) return;
                c.CenterOnRoom(origin);
                return;
            }
            // A null current room is initial-load / no-fix-yet, not a deliberate
            // browse-off-player — don't arm suppression there or startup would
            // freeze auto-follow for the first window.
            if (c.CurrentRoomKey is not null) c.SuppressAutoFollow();
            c.CenterOnRoom(origin);
        });
    }

    // Raised on right-click. The key is the hit room, or null when the click
    // landed on empty map space — so the context-menu target is cleared
    // instead of left pointing at a stale (off-screen) room.
    public event Action<RoomKey?, Point, KeyModifiers>? RoomRightClicked;
    // Carries the click's modifier keys: Alt+click removes a room from a loop being
    // built or unmarks an Auto-Lair room.
    public event Action<RoomKey, Point, KeyModifiers>? RoomLeftClicked;

    // A numbered waypoint chip (a loop or exp/hr sketch being built) was dragged from
    // one room and dropped on another: move that waypoint there.
    public event Action<RoomKey, RoomKey>? WaypointDragged;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // Zoom about the cursor: keep the world point under the
        // cursor fixed in screen space across the zoom transition.
        Point cursor = e.GetPosition(this);
        double zoomBefore = _zoom;
        double factor = e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1;
        double zoomAfter = Math.Clamp(zoomBefore * factor, MinZoom, 4.0);
        if (Math.Abs(zoomAfter - zoomBefore) < 1e-6) return;

        // Reverse-project the cursor into world space at the old zoom,
        // re-project at the new zoom, and adjust pan so the cursor's
        // world point lands at the same screen pixel.
        double cxOld = (cursor.X - Bounds.Width  / 2 - _panX) / (TileWorldSize * zoomBefore);
        double cyOld = (cursor.Y - Bounds.Height / 2 - _panY) / (TileWorldSize * zoomBefore);
        _zoom = zoomAfter;
        _panX = Math.Round(cursor.X - Bounds.Width  / 2 - cxOld * TileWorldSize * _zoom);
        _panY = Math.Round(cursor.Y - Bounds.Height / 2 - cyOld * TileWorldSize * _zoom);
        ViewMoved();
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
            _chipDragging = false;
            _chipDropTarget = null;
            _chipDragFrom = LoopBuilderWaypoints is { Count: > 0 } wps
                && TryHitTestRoom(point.Position, out RoomKey pressed) && wps.Contains(pressed)
                    ? pressed : null;
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
                RoomRightClicked?.Invoke(hit, point.Position, e.KeyModifiers);
            }
            else
            {
                // Empty-space right-click: clear the context target so the
                // menu doesn't keep showing the previous room's entries
                // (teleports, etc.) after the map has shifted under it.
                RoomRightClicked?.Invoke(null, point.Position, e.KeyModifiers);
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
                _chipDragging = _chipDragFrom is not null;
            }

            if (_chipDragging)
            {
                _chipDragPoint = now;
                _chipDropTarget = TryHitTestRoom(now, out RoomKey over) ? over : null;
                _dynamicLayer?.InvalidateVisual();
                // No room tooltips while a chip is held — the room under the cursor
                // changes constantly, same as a pan.
                if (_hoverRoom is not null)
                {
                    _hoverRoom = null;
                    RoomHovered?.Invoke(null, now);
                }
                _hoverTimer.Stop();
                return;
            }

            if (_isDragging)
            {
                _panX = Math.Round(_panStartX + dx);
                _panY = Math.Round(_panStartY + dy);
                ViewMoved();

                // Arm the auto-follow suppression window — the user
                // is actively browsing; don't yank back to live
                // player position for the AutoFollowHoldSeconds hold.
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
            foreach ((Direction cd, MudPlay.Models.Profile.KeyChord chord) in chords)
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

    private static bool ChordMatches(Avalonia.Input.KeyEventArgs e, MudPlay.Models.Profile.KeyChord chord)
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

        if (_chipDragging)
        {
            RoomKey from = _chipDragFrom!.Value;
            EndChipDrag();
            // Dropped on another room → move the waypoint; anywhere else it stays put.
            if (TryHitTestRoom(releasePos, out RoomKey drop) && !drop.Equals(from))
                WaypointDragged?.Invoke(from, drop);
            e.Handled = true;
            return;
        }
        _chipDragFrom = null;

        if (!wasDragging)
        {
            if (TryHitTestRoom(releasePos, out RoomKey hit))
            {
                // Move the crawler selection to the clicked room and keep
                // the click sticky for the hold by arming auto-follow
                // suppression, instead of bouncing back to live player
                // position on the next in-game move.
                SuppressAutoFollow();
                SelectedRoomKey = hit;

                // Notify the host (NavigationViewModel → loop builder
                // when LoopMode is active).
                RoomLeftClicked?.Invoke(hit, releasePos, e.KeyModifiers);
            }
            else
            {
                // A plain click on empty map space (no square hit) clears the
                // selection — the cyan crawler ring disappears and SelectedRoomKey
                // goes back to null. A drag (map pan) is excluded above.
                SelectedRoomKey = null;
            }
        }
        e.Handled = true;
    }

    private void EndChipDrag()
    {
        _chipDragFrom = null;
        _chipDragging = false;
        _chipDropTarget = null;
        _dynamicLayer?.InvalidateVisual();
    }

    // Losing the pointer mid-drag (a window stealing focus) drops the chip back. Our
    // own release frees the capture too, after clearing _leftPressed — that one isn't
    // a loss, and cancelling there threw away every drop before it landed.
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!_leftPressed) return;
        _leftPressed = false;
        _isDragging = false;
        if (_chipDragFrom is not null) EndChipDrag();
    }

    // Centre the view on the room at key. pan = -coord * zoom puts the cell's
    // world point exactly at screen centre. No-op when the room isn't in the
    // active layout.
    public void CenterOnRoom(RoomKey key)
    {
        if (Layout is null) return;
        if (!TryGetCoord(key, out (int X, int Y) coord)) return;
        _panX = Math.Round(-coord.X * TileWorldSize * _zoom);
        _panY = Math.Round(-coord.Y * TileWorldSize * _zoom);
        ViewMoved();
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
            DrawCenteredMessage(context, "No room data loaded. Import game data first.");
    }

    // ----- Layers ----------------------------------------------------------
    // The map is drawn as two child layers. The static one holds what only changes
    // when the layout or a room class does: every tile, exit line and room node with
    // its markers. The dynamic one holds what moves as you play: the current and
    // destination rooms, the selection, the routes and the waypoints. Avalonia keeps
    // each layer's drawing until that layer is invalidated, so a step, a route update
    // or a pan no longer walks and redraws the whole layout; a pan only shifts the
    // static layer's transform.
    //
    // The static layer is also cached as a bitmap, so the renderer draws one picture
    // for it rather than replaying every tile and line each frame. It reaches
    // _staticMargin past each window edge (the bitmap covers the layer's own bounds,
    // so it is arranged that much larger) and is redrawn once a pan has used that up.
    // Pans are whole pixels, so the shifted bitmap lines up exactly with the dynamic
    // layer drawn over it.
    private MapLayer? _staticLayer;
    private MapLayer? _dynamicLayer;
    private double _staticPanX, _staticPanY, _staticZoom;
    private double _staticMargin;

    // How strongly the other floors show under the current one.
    private const double OtherLevelsOpacity = 0.26;
    // A shadow room's markers fade further than its node: a red halt ring or a
    // skull at the floor's shade still out-shouts the dim rooms around it.
    private const double OtherLevelsMarkerOpacity = 0.6;

    // A quarter of the longer side: a 1920x1080 map caches about 24 MB, and a pan that
    // uses the margin up costs one static redraw (about 10 ms on a 6,500-room layout).
    private static double MarginFor(Size size) => Math.Round(Math.Max(size.Width, size.Height) * 0.25);

    protected override Size ArrangeOverride(Size finalSize)
    {
        double margin = MarginFor(finalSize);
        if (margin != _staticMargin)
        {
            _staticMargin = margin;
            InvalidateStatic("resize");
        }
        _staticLayer?.Arrange(new Rect(-margin, -margin, finalSize.Width + 2 * margin, finalSize.Height + 2 * margin));
        _dynamicLayer?.Arrange(new Rect(finalSize));
        return finalSize;
    }

    private sealed class MapLayer : Control
    {
        private readonly Action<DrawingContext> _draw;

        public MapLayer(Action<DrawingContext> draw)
        {
            _draw = draw;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext context) => _draw(context);
    }

    // Room-class properties: what the static layer draws.
    private static readonly HashSet<AvaloniaProperty> StaticLayerProperties = new()
    {
        LayoutProperty, GraphProperty, LairModeProperty, LairRespawnSecondsProperty,
        LairMaxRespawnSecondsProperty, LairMonsterCountsProperty, HighlightShopsProperty, SpellModeProperty,
        SpellTeleportClassesProperty,
        AvoidedRoomsProperty, LevelGatedRoomsProperty, StashRoomsProperty, GhRoomsProperty, GhFullRoomsProperty,
        LoopSequenceNumbersProperty, AutoLairRoomsProperty, TeleportRoomsProperty, DeathRoomsProperty,
        BossRoomsProperty, StopBeforeBossRoomsProperty, TrainerRoomsProperty, NavLineStylesProperty,
        OtherLevelsProperty,
    };

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Bounds carries position too: the map shifting a pixel because the header
        // above it rewrapped changes nothing drawn.
        bool resized = change.Property == BoundsProperty
            && change.OldValue is Rect before && change.NewValue is Rect after && before.Size != after.Size;
        if (change.Property != BoundsProperty && StaticLayerProperties.Contains(change.Property)
            && SameContents(change.OldValue, change.NewValue))
            return;
        if (resized) InvalidateStatic("resize");
        else if (StaticLayerProperties.Contains(change.Property)) InvalidateStatic(change.Property.Name);
        // Everything that affects drawing at all reaches the dynamic layer: the
        // current and destination rooms are redrawn there over their static nodes.
        if (resized || StaticLayerProperties.Contains(change.Property) || DynamicLayerProperties.Contains(change.Property))
            _dynamicLayer?.InvalidateVisual();
    }

    private static readonly HashSet<AvaloniaProperty> DynamicLayerProperties = new()
    {
        CurrentRoomKeyProperty, DestinationRoomKeyProperty, WalkPathProperty, LoopPathProperty,
        LoopBuilderPathProperty, LoopBuilderWaypointsProperty, LoopRunningWaypointsProperty,
        AutoLairWaypointsProperty, AutoLairApproachPathProperty, LoopApproachPreviewPathProperty,
        WalkPathIsAutoLairProperty, SelectedRoomKeyProperty, PreviewPathProperty, LeaderRoutePathProperty,
        WhereTargetRoomsProperty, ComparisonRecordedPathProperty, ComparisonConvertedPathProperty,
        ComparisonStuckRoomsProperty, RouteLinesOnOtherLevelsProperty,
    };

    // The view moved (pan, zoom, re-centre). The cached static layer stands in
    // straight away, shifted for a pan and stretched for a zoom, and is redrawn at
    // the new view only when that stops being good enough. While it still serves, a
    // zoom is redrawn once the wheel rests: a redraw of a town and its other floors
    // takes tens of milliseconds, and one per wheel tick or per drag step is what
    // piled up into seconds of lag. Once it no longer serves (see StandInServes) it
    // is redrawn at once, at most every StaticRedrawGap, wheel still turning or not:
    // the routes are drawn live over it, and on a long zoom out they ran across
    // blank space around a picture shrunk to the middle of the view until the wheel
    // stopped.
    private void ViewMoved()
    {
        if (!StandInServes())
            ScheduleStaticRedraw(debounce: false);
        else if (_zoom != _staticZoom)
            ScheduleStaticRedraw(debounce: true);
        ApplyStaticTransform();
        _dynamicLayer?.InvalidateVisual();
    }

    // A stand-in stretched past this shows rooms as blurred blocks the live routes
    // no longer read against.
    private const double MaxStandInStretch = 2.0;

    private bool StandInServes() => StandInServes(
        _zoom, _panX, _panY, _staticZoom, _staticPanX, _staticPanY, _staticMargin, Bounds.Size);

    // Whether a static layer drawn at one view, moved onto another, still covers all
    // of it and isn't stretched out of recognition. For a plain pan that is the
    // margin; zooming out shrinks the picture, so it gives out after about one and a
    // half times.
    internal static bool StandInServes(double zoom, double panX, double panY,
        double drawnZoom, double drawnPanX, double drawnPanY, double margin, Size view)
    {
        if (drawnZoom <= 0) return false;
        double scale = zoom / drawnZoom;
        if (scale > MaxStandInStretch) return false;
        double w = view.Width, h = view.Height;
        // The layer's top-left corner on screen, as ApplyStaticTransform places it.
        double left = w / 2 + panX - scale * (w / 2 + drawnPanX + margin);
        double top = h / 2 + panY - scale * (h / 2 + drawnPanY + margin);
        return left <= 0 && top <= 0
            && left + scale * (w + 2 * margin) >= w
            && top + scale * (h + 2 * margin) >= h;
    }

    // Maps the static layer, drawn at _staticZoom / _staticPan, onto the view as it is
    // now. A world point sits at (W/2 + pan + w * zoom) on screen, so moving from the
    // drawn view to this one scales about the drawn centre by zoom / _staticZoom and
    // shifts by the change of centre. The layer's origin is its top-left corner,
    // _staticMargin up and left of the map's.
    private void ApplyStaticTransform()
    {
        if (_staticLayer is not { } layer || _staticZoom <= 0) return;
        double scale = _zoom / _staticZoom;
        double m = _staticMargin;
        double ox = Bounds.Width / 2 + _panX + m - scale * (Bounds.Width / 2 + _staticPanX + m);
        double oy = Bounds.Height / 2 + _panY + m - scale * (Bounds.Height / 2 + _staticPanY + m);
        layer.RenderTransform = scale == 1.0
            ? new TranslateTransform(ox, oy)
            : new MatrixTransform(new Matrix(scale, 0, 0, scale, ox, oy));
    }

    private static readonly TimeSpan StaticRedrawGap = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan ZoomRestDelay = TimeSpan.FromMilliseconds(150);
    private DispatcherTimer? _staticRedrawTimer;
    private DateTime _lastStaticRedraw;

    private void ScheduleStaticRedraw(bool debounce)
    {
        _staticRedrawTimer ??= new DispatcherTimer(TimeSpan.Zero, DispatcherPriority.Background, (_, _) =>
        {
            _staticRedrawTimer!.Stop();
            InvalidateStatic(_zoom != _staticZoom ? "zoom" : "pan");
        });
        if (debounce)
        {
            _staticRedrawTimer.Stop();
            _staticRedrawTimer.Interval = ZoomRestDelay;
            _staticRedrawTimer.Start();
            return;
        }
        TimeSpan since = DateTime.UtcNow - _lastStaticRedraw;
        if (since >= StaticRedrawGap)
        {
            InvalidateStatic(_zoom != _staticZoom ? "zoom" : "pan");
            return;
        }
        // Whatever was pending (a zoom's wait for the wheel to rest among them)
        // gives way: this redraw is owed as soon as the gap allows.
        _staticRedrawTimer.Stop();
        _staticRedrawTimer.Interval = StaticRedrawGap - since;
        _staticRedrawTimer.Start();
    }

    // A room set or count map republished with the same contents: the overlays are
    // rebuilt from scratch on every change of what they mirror, and each one used to
    // cost a full redraw of the map even when nothing in it had changed.
    private static bool SameContents(object? before, object? after)
    {
        if (ReferenceEquals(before, after)) return true;
        return (before, after) switch
        {
            (IReadOnlySet<RoomKey> a, IReadOnlySet<RoomKey> b) => a.Count == b.Count && a.SetEquals(b),
            (IReadOnlyDictionary<RoomKey, int> a, IReadOnlyDictionary<RoomKey, int> b) =>
                a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out int v) && v == kv.Value),
            _ => false,
        };
    }

    // Why the static layer is being redrawn, for the performance log.
    private string _staticCause = "first draw";

    // Redraw the static layer at the view as it is now. The view it is drawn at is
    // taken here, not in the render: a visual can't change its own transform while
    // it renders, and a pan before that render lands as a shift from this view.
    private void InvalidateStatic(string cause)
    {
        _staticCause = cause;
        _staticRedrawTimer?.Stop();
        _lastStaticRedraw = DateTime.UtcNow;
        _staticPanX = _panX;
        _staticPanY = _panY;
        _staticZoom = _zoom;
        if (_staticLayer is not { } layer) return;
        layer.RenderTransform = null;
        // A cached visual is only drawn again when its cache or transform has changed
        // since the last draw; an invalidate alone leaves the old picture up. A pan or
        // zoom always moved the transform first, so those redrew — an overlay toggle
        // did not, and showed only after the view was next moved (report
        // paradigm-20261004-013848). A fresh cache makes every redraw land.
        layer.CacheMode = new BitmapCache();
        layer.InvalidateVisual();
    }

    private void DrawStaticLayer(DrawingContext context)
    {
        if (Layout is null || Layout.CoordToRoom.Count == 0) return;
        using PerformanceMonitor.Scope? timing = AppServices.CurrentOrNull?.Performance is { IsCollecting: true } perf
            ? perf.Measure($"map redraw ({_staticCause})") : null;

        double tilePixels = TileWorldSize * _staticZoom;
        if (tilePixels < MinTilePixels) return;

        // The layer starts _staticMargin up and left of the map.
        double cx = Bounds.Width  / 2 + _staticPanX + _staticMargin;
        double cy = Bounds.Height / 2 + _staticPanY + _staticMargin;
        Rect area = new(new Size(Bounds.Width + 2 * _staticMargin, Bounds.Height + 2 * _staticMargin));

        // The other floors first, dimmed, so the current floor reads on top.
        if (OtherLevels is { CoordToRoom.Count: > 0 } other)
        {
            _drawingOtherLevels = other;
            try
            {
                using (context.PushOpacity(OtherLevelsOpacity))
                    DrawFloor(context, other, tilePixels, cx, cy, area);
            }
            finally
            {
                _drawingOtherLevels = null;
            }
        }
        DrawFloor(context, Layout, tilePixels, cx, cy, area);
    }

    // One floor of the static layer: cell tiles, exit lines, then the room nodes
    // with what sits on them, every node drawn as neither current nor destination
    // (the dynamic layer draws those two on top).
    private void DrawFloor(DrawingContext context, RoomLayout floor, double tilePixels, double cx, double cy, Rect area)
    {
        foreach (KeyValuePair<(int X, int Y), RoomKey> kvp in floor.CoordToRoom)
        {
            Rect cell = ComputeCellRect(kvp.Key, tilePixels, cx, cy);
            if (!cell.Intersects(area)) continue;
            context.FillRectangle(TileBg, cell);
            context.DrawRectangle(null, TileBorderPen, cell);
        }

        DrawAllExitLines(context, tilePixels, cx, cy, area);

        // Markers are left off once rooms are too small to read them on.
        bool details = tilePixels >= MinDetailTilePixels;
        foreach (KeyValuePair<(int X, int Y), RoomKey> kvp in floor.CoordToRoom)
        {
            Rect cell = ComputeCellRect(kvp.Key, tilePixels, cx, cy);
            if (!cell.Intersects(area)) continue;
            if (details) DrawCellContents(context, cell, kvp.Value, tilePixels, staticPass: true);
            else DrawRoomNode(context, cell, kvp.Value, staticPass: true);
        }
    }

    // A room's node and the per-cell markers on it.
    private void DrawCellContents(DrawingContext context, Rect cell, RoomKey key, double tilePixels, bool staticPass)
    {
        DrawRoomNode(context, cell, key, staticPass);
        if (_drawingOtherLevels is null)
        {
            DrawCellMarkers(context, cell, key, tilePixels);
            return;
        }
        using (context.PushOpacity(OtherLevelsMarkerOpacity))
            DrawCellMarkers(context, cell, key, tilePixels);
    }

    private void DrawCellMarkers(DrawingContext context, Rect cell, RoomKey key, double tilePixels)
    {
        if (LevelGatedRooms is { } gated && gated.Contains(key))
            DrawLevelGateMarker(context, cell);

        if (AvoidedRooms is not null && AvoidedRooms.Contains(key))
            DrawAvoidX(context, cell);

        if (StashRooms is not null && StashRooms.Contains(key))
            DrawStashX(context, cell);

        if (GhRooms is not null && GhRooms.Contains(key))
            DrawRobotIcon(context, cell,
                full: GhFullRooms is not null && GhFullRooms.Contains(key));

        if (DeathRooms is not null && DeathRooms.Contains(key))
            DrawSkull(context, cell);

        if (BossRooms is not null && BossRooms.Contains(key))
            DrawBossCrown(context, cell,
                stopBefore: StopBeforeBossRooms is not null && StopBeforeBossRooms.Contains(key));

        if (TrainerRooms is not null && TrainerRooms.Contains(key))
            DrawTrainerIcon(context, cell);

        if (LoopSequenceNumbers is not null
            && LoopSequenceNumbers.TryGetValue(key, out int seq)
            && tilePixels >= 16)
            DrawSequenceNumber(context, cell, seq);

        if (LairMode is LairDisplayMode.Count or LairDisplayMode.HeatCount
            && LairMonsterCounts is not null
            && LairMonsterCounts.TryGetValue(key, out int lairCount)
            && tilePixels >= 16)
            DrawSequenceNumber(context, cell, lairCount);
    }

    private void DrawDynamicLayer(DrawingContext context)
    {
        if (Layout is null || Layout.CoordToRoom.Count == 0) return;

        double tilePixels = TileWorldSize * _zoom;
        if (tilePixels < MinTilePixels) return;

        double cx = Bounds.Width  / 2 + _panX;
        double cy = Bounds.Height / 2 + _panY;
        Rect viewport = new(Bounds.Size);

        // The current and destination rooms, over the plain nodes the static layer
        // drew for them.
        void DrawOnTop(RoomKey key)
        {
            if (!TryGetCoord(key, out (int X, int Y) coord)) return;
            Rect cell = ComputeCellRect(coord, tilePixels, cx, cy);
            if (!cell.Intersects(viewport)) return;
            // A room on another floor keeps that floor's up/down hints.
            _drawingOtherLevels = Layout.Positions.ContainsKey(key) ? null : OtherLevels;
            try { DrawCellContents(context, cell, key, tilePixels, staticPass: false); }
            finally { _drawingOtherLevels = null; }
        }
        if (CurrentRoomKey is { } current) DrawOnTop(current);
        if (DestinationRoomKey is { } destination && !destination.Equals(CurrentRoomKey)) DrawOnTop(destination);

        // @where target — a transient green flash the VM clears when its timer expires.
        // Drawn right on the node so it reads as a marked square.
        if (WhereTargetRooms is { Count: > 0 } whereRooms)
            foreach (RoomKey key in whereRooms)
                if (TryGetCoord(key, out (int X, int Y) coord)
                    && ComputeCellRect(coord, tilePixels, cx, cy) is var cell && cell.Intersects(viewport))
                    DrawWhereHighlight(context, cell);

        // Crawler selection ring — drawn inside the cell with a
        // small inset so it sits between the cell border and the
        // room node, distinct from the amber current-room ring.
        if (SelectedRoomKey is { } sel && TryGetCoord(sel, out (int X, int Y) selCoord))
            context.DrawRectangle(null, SelectionPen, ComputeCellRect(selCoord, tilePixels, cx, cy).Deflate(1));

        // Pass 4: top-of-stack polylines. Loop-builder preview and the
        // loop-approach preview share one pen (semantically distinct,
        // visually identical: "this is a planned cycle"); the walk
        // preview, a followed leader's route, the running loop and the
        // walk go on top of those so the user's primary signal is the
        // active automation.
        EnsureNavPens();
        DrawPathPolyline(context, LoopBuilderPath,        _loopBuilderPen, tilePixels, cx, cy);
        DrawPathPolyline(context, LoopApproachPreviewPath, _loopBuilderPen, tilePixels, cx, cy);
        DrawPathPolyline(context, PreviewPath,            _previewPathPen, tilePixels, cx, cy);
        DrawPathPolyline(context, LeaderRoutePath,        _followRoutePen, tilePixels, cx, cy);
        DrawPathPolyline(context, LoopPath,               _loopPathPen,    tilePixels, cx, cy);
        // When AutoLair is driving the walker, the dedicated approach
        // path renders the FULL leg in orange and stays stable across
        // walker sub-step shrinkage — suppress the per-step WalkPath
        // so the two layers don't fight. WalkPathIsAutoLair still
        // colours the plain WalkPath orange for auto-lair walks outside
        // the approach phases, when there's no approach path to draw.
        if (AutoLairApproachPath is { Count: > 1 })
        {
            DrawPathPolyline(context, AutoLairApproachPath, _autoLairWalkPen, tilePixels, cx, cy);
        }
        else
        {
            IPen walkPen = WalkPathIsAutoLair ? _autoLairWalkPen : _walkPathPen;
            DrawPathPolyline(context, WalkPath, walkPen, tilePixels, cx, cy);
        }

        // Trap overlay: redraw ONLY the red trapped-exit segments, on top of the
        // travel polylines just drawn. A preview / active route running along a trap
        // would otherwise hide it — and merely thickening the trap line isn't enough
        // when the route pen is as wide (or wider). Re-drawing the red on top makes the
        // trap read through the route unconditionally. With no route drawn the static
        // layer's own trap lines already show, and this pass walks the whole layout.
        if (AnyRouteDrawn())
            DrawAllExitLines(context, tilePixels, cx, cy, viewport, trapOverlay: true);

        // The import review's comparison sits over every other route.
        DrawPathPolyline(context, ComparisonRecordedPath,  ComparisonRecordedPen,  tilePixels, cx, cy);
        DrawPathPolyline(context, ComparisonConvertedPath, ComparisonConvertedPen, tilePixels, cx, cy);
        if (ComparisonStuckRooms is { Count: > 0 } stuck)
        {
            double arm = Math.Max(3, tilePixels * 0.3);
            foreach (RoomKey k in stuck)
            {
                if (!TryGetCoord(k, out (int X, int Y) c)) continue;
                Point m = new(cx + c.X * tilePixels, cy + c.Y * tilePixels);
                context.DrawLine(ComparisonStuckPen, new Point(m.X - arm, m.Y - arm), new Point(m.X + arm, m.Y + arm));
                context.DrawLine(ComparisonStuckPen, new Point(m.X - arm, m.Y + arm), new Point(m.X + arm, m.Y - arm));
            }
        }

        // Lift the current + destination markers back on top of the routes so the walk-to
        // line ending at the destination doesn't cover its dot.
        DrawEndpointMarkersOnTop(context, tilePixels, cx, cy, viewport);

        // Pass 6: numbered waypoint markers — drawn last so they sit on top of every
        // polyline and every room node fill. Red while building, green while running
        // (both numbered to match the CURRENT NAV rows), amber for Auto-Lair.
        DrawNumberedWaypoints(context, LoopBuilderWaypoints,
            LoopBuilderWaypointFill, LoopBuilderWaypointRing, tilePixels, cx, cy);
        DrawNumberedWaypoints(context, LoopRunningWaypoints,
            LoopRunningWaypointFill, LoopRunningWaypointRing, tilePixels, cx, cy);
        DrawNumberedWaypoints(context, AutoLairWaypoints,
            AutoLairWaypointFill, AutoLairWaypointRing, tilePixels, cx, cy);
        DrawChipDrag(context, tilePixels, cx, cy);
    }

    private bool AnyRouteDrawn() =>
        LoopBuilderPath is { Count: > 1 } || LoopApproachPreviewPath is { Count: > 1 }
        || PreviewPath is { Count: > 1 } || LeaderRoutePath is { Count: > 1 } || LoopPath is { Count: > 1 }
        || AutoLairApproachPath is { Count: > 1 } || WalkPath is { Count: > 1 }
        || ComparisonRecordedPath is { Count: > 1 } || ComparisonConvertedPath is { Count: > 1 };

    // The chip being dragged, under the cursor, with the room it would land on ringed.
    private void DrawChipDrag(DrawingContext ctx, double tilePixels, double cx, double cy)
    {
        if (!_chipDragging || _chipDragFrom is not { } from || LoopBuilderWaypoints is not { } wps) return;
        if (_chipDropTarget is { } target && !target.Equals(from) && Layout is not null
            && TryGetCoord(target, out var coord))
            ctx.DrawRectangle(null, ChipDropPen, ComputeCellRect(coord, tilePixels, cx, cy).Inflate(2));

        double radius = Math.Clamp(tilePixels * 0.34, 7.0, 15.0);
        using (ctx.PushOpacity(0.8))
            ctx.DrawEllipse(LoopBuilderWaypointFill, LoopBuilderWaypointRing, _chipDragPoint, radius, radius);
        int number = 0;
        for (int i = wps.Count - 1; i >= 0 && number == 0; i--)
            if (wps[i].Equals(from)) number = i + 1;
        string label = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        FormattedText ft = new(label, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Inter", FontStyle.Normal, FontWeight.Bold),
            Math.Clamp(tilePixels * 0.33, 10.0, 14.0), NumberedWaypointTextBrush);
        ctx.DrawText(ft, new Point(_chipDragPoint.X - ft.Width / 2, _chipDragPoint.Y - ft.Height / 2));
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

    // Draws every connector of the layout (classified once per layout by
    // ClassifyConnectors): adjacent as a solid line, bridge as a dashed line,
    // stub to the cell edge.
    // trapOverlay: draw ONLY the red trap segments (skipping base lines, arrows, spell
    // walls) so this can be re-run on top of the travel polylines to keep a trapped exit
    // visible under a route. The full pass (trapOverlay false) draws everything.
    private void DrawAllExitLines(DrawingContext ctx, double tilePixels, double cx, double cy, Rect viewport, bool trapOverlay = false)
    {
        if (DrawLayout is not { } layout) return;

        // Arrowheads and spell bars are skipped once rooms shrink past reading them.
        bool details = tilePixels >= MinDetailTilePixels;
        foreach (Connector c in ConnectorsOf(layout))
        {
            Point srcPt = new(cx + c.Source.X * tilePixels, cy + c.Source.Y * tilePixels);
            // Nothing of a connector whose box (padded by a cell for arrowheads,
            // stubs and spell bars) misses the drawn area can show.
            Point farPt = new(cx + c.Far.X * tilePixels, cy + c.Far.Y * tilePixels);
            if (!new Rect(srcPt, farPt).Inflate(tilePixels).Intersects(viewport)) continue;

            bool isTrap = c.SrcTrap || c.TgtTrap;
            if (trapOverlay && !isTrap) continue;   // overlay pass draws only trap segments
            switch (c.Kind)
            {
                case ConnectionKind.Adjacent:
                {
                    // Grid-adjacent — clean continuous connector.
                    if (trapOverlay)
                    {
                        DrawTrapOverlay(ctx, srcPt, farPt, TrapOverlayPen, c.SrcTrap, c.TgtTrap, tilePixels);
                        break;
                    }
                    IPen basePen = c.IsAction ? ActionPen : c.IsHidden ? HiddenPen : ExitPen;
                    DrawExitConnector(ctx, srcPt, farPt, basePen, TrapPen, c.SrcTrap, c.TgtTrap);
                    if (!details) break;
                    if (c.OneWay) DrawOneWayArrow(ctx, isTrap ? TrapPen : basePen, srcPt, farPt, tilePixels);
                    if (c.IsSpell) DrawSpellWall(ctx, SpellWallPen, Midpoint(srcPt, farPt), c.Dir, tilePixels);
                    break;
                }
                case ConnectionKind.Bridge:
                {
                    // Connected but not grid-adjacent — dashed direct
                    // line between the two room centres, angled along
                    // the real connection instead of a stub into space.
                    if (trapOverlay)
                    {
                        DrawTrapOverlay(ctx, srcPt, farPt, TrapBridgePen, c.SrcTrap, c.TgtTrap, tilePixels);
                        break;
                    }
                    IPen baseBridge = c.IsAction ? ActionBridgePen
                             : c.IsHidden ? HiddenBridgePen : ExitBridgePen;
                    DrawExitConnector(ctx, srcPt, farPt, baseBridge, TrapBridgePen, c.SrcTrap, c.TgtTrap);
                    if (!details) break;
                    if (c.OneWay) DrawOneWayArrow(ctx, isTrap ? TrapBridgePen : baseBridge, srcPt, farPt, tilePixels);
                    if (c.IsSpell) DrawSpellWall(ctx, SpellWallPen, Midpoint(srcPt, farPt), c.Dir, tilePixels);
                    break;
                }
                default:
                {
                    if (trapOverlay) break;   // stubs go to unplaced rooms — no route runs along one
                    // Target genuinely unplaced (dropped / blacklisted, a
                    // one-way cast pocket mouth, or too far to bridge) — stub
                    // to the cell edge. The spell-wall bar sits ON the cell
                    // divider (the stub's edge point), exactly where it lands
                    // between two placed rooms, rather than halfway down the
                    // stub. A one-way exit keeps its directional arrowhead —
                    // slightly enlarged and tipped at the divider — so a
                    // cut-off pocket still reads as "out this way, no return".
                    IPen pen = isTrap ? TrapPen : c.IsAction ? ActionPen : c.IsHidden ? HiddenPen : ExitPen;
                    Rect cell = ComputeCellRect(c.Source, tilePixels, cx, cy);
                    if (StubEndpoint(cell, srcPt.X, srcPt.Y, c.Dir) is not { } endPt) break;
                    ctx.DrawLine(pen, srcPt, endPt);
                    if (!details) break;
                    if (c.OneWay) DrawOneWayArrow(ctx, pen, srcPt, endPt, tilePixels, scale: 1.3, tipBack: 0.0);
                    if (c.IsSpell) DrawSpellWall(ctx, SpellWallPen, endPt, c.Dir, tilePixels);
                    break;
                }
            }
        }
    }

    // One connection as DrawAllExitLines draws it, in grid cells. Far is the target's
    // cell for a placed target, else the cell the exit points at.
    private readonly record struct Connector(
        (int X, int Y) Source, (int X, int Y) Far, Direction Dir, ConnectionKind Kind,
        bool SrcTrap, bool TgtTrap, bool IsAction, bool IsHidden, bool IsSpell, bool OneWay);

    // Every connection of a layout, classified once and kept with it. Nothing here
    // depends on the view, and working it out (each exit's real target, its way back,
    // trap / action / hidden / spell on both sides) was most of what a static-layer
    // redraw cost: 10–20 ms over a town and its other floors, on every pan past the
    // margin and every zoom step.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<RoomLayout, ConnectorSet> _connectors = new();

    private sealed record ConnectorSet(RoomGraphManager? Graph, List<Connector> Connectors);

    private List<Connector> ConnectorsOf(RoomLayout layout)
    {
        if (_connectors.TryGetValue(layout, out ConnectorSet? set) && ReferenceEquals(set.Graph, Graph))
            return set.Connectors;
        List<Connector> connectors = ClassifyConnectors(layout);
        _connectors.AddOrUpdate(layout, new ConnectorSet(Graph, connectors));
        return connectors;
    }

    // Walks RoomLayout.EdgesFromCoord once and keeps each connection exactly
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
    // Reads the edge hints through DrawLayout, so the caller has it set to layout.
    private List<Connector> ClassifyConnectors(RoomLayout layout)
    {
        List<Connector> connectors = new();
        var drawn = new HashSet<((int X, int Y) A, (int X, int Y) B)>();
        foreach (KeyValuePair<(int X, int Y), IReadOnlySet<Direction>> entry in layout.EdgesFromCoord)
        {
            (int X, int Y) source = entry.Key;

            // Resolve the room sitting at the source cell once — its exit
            // table tells us where each exit ACTUALLY lands, which may
            // differ from the grid-adjacent cell when the layout couldn't
            // place the pair flat.
            bool haveSrcKey = layout.CoordToRoom.TryGetValue(source, out RoomKey srcKey);
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
                    targetPlaced = layout.Positions.TryGetValue(exit.Target, out (int X, int Y) ac);
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
                    targetPlaced = layout.CoordToRoom.ContainsKey(expected);
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
                // routing info ("needs a command/action — a lever, an
                // ask-door, or a `go path`-style named exit");
                // hidden-cyan is reveal info ("needs sea <dir>").
                // Traps are DIRECTIONAL — a trap on A's exit toward B does not imply
                // B's exit back is trapped. Track each side separately: the connector
                // is split at its midpoint so only the trapped HALF is red. Full red =
                // trapped both ways; half red (against the room whose exit is trapped)
                // = a one-way trap; no red = clean both ways. Painting the whole line
                // red for a one-way trap wrongly implied the return trip was dangerous.
                bool srcTrap = IsTrapEdge(source, dir);
                bool tgtTrap = IsTrapEdge(bCoord, Opposite(dir));
                bool isTrap = srcTrap || tgtTrap;
                bool isAction = IsActionRequiredEdge(source, dir)
                             || IsActionRequiredEdge(bCoord, Opposite(dir));
                bool isHidden = !isAction
                    && (IsHiddenEdge(source, dir)
                     || IsHiddenEdge(bCoord, Opposite(dir)));

                // A cast-on-walk exit gets a perpendicular spell-wall bar across
                // its connector regardless of the trap/action/hidden line colour
                // — the two convey different things (why the edge is special vs.
                // that a spell fires when you cross it).
                bool isSpell = IsSpellEdge(source, dir)
                            || IsSpellEdge(bCoord, Opposite(dir));

                connectors.Add(new Connector(source, bCoord, dir,
                    ClassifyConnection(targetPlaced, source, expected, actual, BridgeMaxCells),
                    srcTrap, tgtTrap, isAction, isHidden, isSpell, oneWay));
            }
        }
        return connectors;
    }

    // Draw one room-to-room connector, colouring each HALF by whether the exit
    // leaving that end is trapped. Both trapped → one solid trap line; neither →
    // one base line; exactly one → split at the midpoint so only the trapped half
    // (the half against the room whose exit is trapped) is red. Keeps a one-way trap
    // from reading as a two-way one.
    private static void DrawExitConnector(DrawingContext ctx, Point srcPt, Point tgtPt,
        IPen basePen, IPen trapPen, bool srcTrap, bool tgtTrap)
    {
        if (srcTrap == tgtTrap)
        {
            ctx.DrawLine(srcTrap ? trapPen : basePen, srcPt, tgtPt);
            return;
        }
        Point mid = Midpoint(srcPt, tgtPt);
        ctx.DrawLine(srcTrap ? trapPen : basePen, srcPt, mid);
        ctx.DrawLine(tgtTrap ? trapPen : basePen, mid, tgtPt);
    }

    // Redraw only the red trapped portion(s) of a connector — used by the trap-overlay
    // pass to lay the trap back over a travel polyline. Mirrors DrawExitConnector's
    // midpoint split but paints nothing for the non-trapped side, so the route stays
    // visible everywhere except the trapped segment it crosses. Both ends are trimmed
    // back to the room-node square edge so the on-top red only fills the between-squares
    // gap (like the base exit line, which the node covers up to its edge) rather than
    // drawing across the room squares themselves.
    private static void DrawTrapOverlay(DrawingContext ctx, Point srcPt, Point tgtPt,
        IPen trapPen, bool srcTrap, bool tgtTrap, double tilePixels)
    {
        double dx = tgtPt.X - srcPt.X, dy = tgtPt.Y - srcPt.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3) return;
        double ux = dx / len, uy = dy / len;
        double nodeHalf = Math.Max(tilePixels * 0.45, 3.0) / 2;      // matches DrawRoomNode's node square
        double trim = nodeHalf / Math.Max(Math.Abs(ux), Math.Abs(uy));   // centre → node-square edge
        if (2 * trim >= len) return;                                 // squares meet — no visible gap
        Point a = new(srcPt.X + ux * trim, srcPt.Y + uy * trim);
        Point b = new(tgtPt.X - ux * trim, tgtPt.Y - uy * trim);
        Point mid = Midpoint(srcPt, tgtPt);   // split at the true midpoint (sits in the gap)
        if (srcTrap && tgtTrap) { ctx.DrawLine(trapPen, a, b); return; }
        if (srcTrap) ctx.DrawLine(trapPen, a, mid);
        else if (tgtTrap) ctx.DrawLine(trapPen, mid, b);
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
        if (DrawLayout?.TrapEdgesFromCoord is not { } traps) return false;
        return traps.TryGetValue(coord, out IReadOnlySet<Direction>? set)
            && set.Contains(dir);
    }

    // True when the exit at coord heading dir fires a spell when walked
    // ("(Cast: ...)"). Drives the perpendicular spell-wall glyph. Same
    // pre-computed-set lookup as IsTrapEdge.
    private bool IsSpellEdge((int X, int Y) coord, Direction dir)
    {
        if (DrawLayout?.SpellEdgesFromCoord is not { } spells) return false;
        return spells.TryGetValue(coord, out IReadOnlySet<Direction>? set)
            && set.Contains(dir);
    }

    // True when the exit at coord heading dir is a
    // RoomExitHint.MultiActionHidden — i.e. one or more in-room actions
    // (lever, switch, button …) must execute before the walker can traverse.
    // Looked up from the graph during connector classification rather than
    // carried as another pre-computed set on RoomLayout: action-required edges
    // are sparse and the result is cached per layout.
    private bool IsActionRequiredEdge((int X, int Y) coord, Direction dir)
    {
        if (Graph is null || DrawLayout is not { } layout) return false;
        if (!layout.CoordToRoom.TryGetValue(coord, out RoomKey key)) return false;
        if (Graph.GetRoom(key) is not { } room) return false;
        if (!room.Exits.TryGetValue(dir, out RoomExit exit)) return false;
        // Both shapes read to the player as "you can't just walk this direction —
        // a command is needed": MultiActionHidden (a lever/ask-door acted on first)
        // and Text (a named-command exit like `go path` — the walker types the word
        // instead of the compass step). Same magenta "Action required" stub for both.
        return exit.Hint is RoomExitHint.MultiActionHidden or RoomExitHint.Text;
    }

    // True when the exit at coord heading dir is a
    // RoomExitHint.SearchableHidden — present but masked from "Obvious exits:"
    // until the player runs `sea <dir>`. Same lookup pattern as
    // IsActionRequiredEdge.
    private bool IsHiddenEdge((int X, int Y) coord, Direction dir)
    {
        if (Graph is null || DrawLayout is not { } layout) return false;
        if (!layout.CoordToRoom.TryGetValue(coord, out RoomKey key)) return false;
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
        bool throughOtherLevels = RouteLinesOnOtherLevels;
        foreach (RoomKey key in path)
        {
            (int X, int Y) coord;
            if (throughOtherLevels ? !TryGetCoord(key, out coord) : !Layout.Positions.TryGetValue(key, out coord))
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
            // Only connect two consecutive rooms that share a real graph exit.
            // A paused walker's RemainingRoomKeys prepends the LIVE room, so after
            // a manual move OFF the route its first pair is the (off-route) current
            // room and the stale next step — not adjacent, and without this guard
            // that pair renders as a straight line clear across the map (report
            // paradigm walk-to→manual artifact). Gap-bridged edges (graph-connected
            // but not grid-adjacent) still carry an exit, so they stay drawn; only a
            // genuinely unconnected pair is skipped. Teleport hops are excluded too
            // — their two sides draw on their own planes.
            if (prev is { } p && prevKey is { } pk
                && !IsTeleportHop(pk, key)
                && RoomsConnected(pk, key))
                ctx.DrawLine(pen, p, here);
            prev = here;
            prevKey = key;
        }
    }

    private bool IsTeleportHop(RoomKey from, RoomKey to)
        => Graph?.GetRoom(from) is { } room
           && room.Exits.TryGetValue(Direction.Teleport, out RoomExit tele)
           && tele.Target.Equals(to);

    // Whether a graph exit joins the two rooms in either direction — the test for
    // whether a route polyline segment between them is real (a valid walk step is
    // always a graph edge) rather than a stale cross-map jump.
    private bool RoomsConnected(RoomKey a, RoomKey b)
    {
        if (Graph is null) return false;
        if (Graph.GetRoom(a) is { } ra)
            foreach (RoomExit e in ra.Exits.Values)
                if (e.Target.Equals(b)) return true;
        if (Graph.GetRoom(b) is { } rb)
            foreach (RoomExit e in rb.Exits.Values)
                if (e.Target.Equals(a)) return true;
        return false;
    }

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

    // Amber wedge in the room node's TOP-LEFT corner — the third member of the
    // corner-badge family, opposite the U/D badges on the right so position
    // alone tells them apart. Sized to the drawn room NODE (DrawRoomNode's
    // cell.Width * 0.45 square), not the whole tile, so it sits inside the
    // visible room square instead of spilling across the gap between tiles.
    private static void DrawLevelGateMarker(DrawingContext ctx, Rect cell)
    {
        double nodeSize = Math.Max(cell.Width * 0.45, 3.0);
        double nx = cell.X + (cell.Width  - nodeSize) / 2;
        double ny = cell.Y + (cell.Height - nodeSize) / 2;
        double size = Math.Max(nodeSize * 0.50, 7.0);

        StreamGeometry wedge = new();
        using (StreamGeometryContext g = wedge.Open())
        {
            g.BeginFigure(new Point(nx + size, ny), isFilled: true);
            g.LineTo(new Point(nx, ny));
            g.LineTo(new Point(nx, ny + size));
            g.EndFigure(isClosed: true);
        }
        ctx.DrawGeometry(LevelGateFill, VerticalBadgeEdgePen, wedge);
    }

    // Green flash for the room an @where reply located — a translucent fill + ring
    // over the whole cell so it stands out at a glance; the VM clears it when its timer expires.
    private static void DrawWhereHighlight(DrawingContext ctx, Rect cell)
        => ctx.DrawRectangle(WhereTargetFill, WhereTargetPen, new RoundedRect(cell.Deflate(1), cell.Width * 0.14));

    // A tiny robot head marking a room the user labeled for Roomba Mode — antenna,
    // rounded head, two eyes. Cyan so it reads distinctly against the red avoid and
    // gold stash crosses. Vector primitives (no emoji glyph) so it renders at any
    // zoom on any font.
    private static readonly IBrush RobotFillBrush = new SolidColorBrush(Color.Parse("#FF7FE0FF"));
    private static readonly IPen   RobotRimPen    = new Pen(new SolidColorBrush(Color.Parse("#FF10384A")), 1.0);
    private static readonly IBrush RobotEyeBrush  = new SolidColorBrush(Color.Parse("#FF10384A"));

    // Amber, matching the nav status line's "held for a reason" colour — a full
    // room isn't an error, it's a room needing attention.
    private static readonly IPen GhFullRingPen =
        new Pen(new SolidColorBrush(Color.Parse("#FFE0A030")), 1.6);

    private static void DrawRobotIcon(DrawingContext ctx, Rect cell, bool full = false)
    {
        double s = Math.Max(cell.Width * 0.34, 3.5);
        double cxm = cell.X + cell.Width / 2.0;
        double cym = cell.Y + cell.Height / 2.0;

        // "No space left" ring around the robot. A ring rather than a recolour so
        // the room still reads as a Roomba room at a glance — it's the same room,
        // just out of space.
        if (full)
            ctx.DrawEllipse(null, GhFullRingPen, new Point(cxm, cym), s * 0.95, s * 0.95);

        // Head placed so the antenna above balances the head below — the whole glyph
        // (tip to head-bottom) stays centred on the cell, ~0.4 cell tall, so it sits
        // fully inside the room square rather than poking out the top.
        double hw = s * 0.5, hh = s * 0.40;
        Rect head = new(cxm - hw, cym - hh + s * 0.14, hw * 2, hh * 2);
        ctx.DrawRectangle(RobotFillBrush, RobotRimPen, new RoundedRect(head, s * 0.14));

        // Antenna: stalk + tip above the head.
        double ax = cxm, ayBase = head.Y, ayTip = head.Y - s * 0.20;
        ctx.DrawLine(RobotRimPen, new Point(ax, ayBase), new Point(ax, ayTip));
        ctx.DrawEllipse(RobotFillBrush, RobotRimPen, new Point(ax, ayTip), s * 0.08, s * 0.08);

        // Two eyes.
        double eyeR = Math.Max(s * 0.08, 0.7);
        double eyeY = head.Y + head.Height * 0.45;
        ctx.DrawEllipse(RobotEyeBrush, null, new Point(head.X + head.Width * 0.32, eyeY), eyeR, eyeR);
        ctx.DrawEllipse(RobotEyeBrush, null, new Point(head.X + head.Width * 0.68, eyeY), eyeR, eyeR);
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

    // A gold crown marking a boss room (listed by a Bosses-table entry). Built from
    // primitives like DrawSkull — a three-spike silhouette with jewelled tips — so
    // it renders crisply at any zoom. When stopBefore is set (the boss's entry is
    // flagged "stop before entering"), a red halt ring backed by a black edge is
    // drawn around it so a walk-to that halts one room short is unmistakable.
    private static void DrawBossCrown(DrawingContext ctx, Rect cell, bool stopBefore)
    {
        // Size to the drawn room NODE (DrawRoomNode's cell.Width * 0.45 square),
        // not the whole tile, so the crown sits inside the visible room square
        // instead of spilling over the gap between tiles.
        double nodeSize = Math.Max(cell.Width * 0.45, 3.0);
        double mx = cell.X + cell.Width  / 2.0;
        double my = cell.Y + cell.Height / 2.0;
        double r  = nodeSize * 0.42;                  // crown ~fills the room square

        // Stop-before halt ring first (under the crown) — black edge then red so it
        // reads as a red ring with a dark outline on any cell fill. CIRCUMSCRIBES the
        // room node: the square's four corners touch the ring's inner edge (radius =
        // half-diagonal + half the edge-pen), so the crown gets the whole square to
        // fill inside it.
        if (stopBefore)
        {
            double ringR = nodeSize * 0.5 * Math.Sqrt(2.0) + StopRingEdgePen.Thickness / 2.0;
            ctx.DrawEllipse(null, StopRingEdgePen, new Point(mx, my), ringR, ringR);
            ctx.DrawEllipse(null, StopRingPen,     new Point(mx, my), ringR, ringR);
        }

        StreamGeometry crown = new();
        using (StreamGeometryContext g = crown.Open())
        {
            g.BeginFigure(new Point(mx - r,        my + r * 0.55), isFilled: true);
            g.LineTo(new Point(mx - r,        my - r * 0.25));   // left spike
            g.LineTo(new Point(mx - r * 0.42, my + r * 0.15));   // left dip
            g.LineTo(new Point(mx,            my - r * 0.75));   // centre spike (tallest)
            g.LineTo(new Point(mx + r * 0.42, my + r * 0.15));   // right dip
            g.LineTo(new Point(mx + r,        my - r * 0.25));   // right spike
            g.LineTo(new Point(mx + r,        my + r * 0.55));   // base
            g.EndFigure(true);
        }
        ctx.DrawGeometry(CrownFill, CrownRimPen, crown);

        // Jewelled spike tips so the crown still reads as a crown at small sizes.
        double jr = r * 0.15;
        ctx.DrawEllipse(CrownFill, CrownRimPen, new Point(mx - r, my - r * 0.25), jr, jr);
        ctx.DrawEllipse(CrownFill, CrownRimPen, new Point(mx,     my - r * 0.75), jr, jr);
        ctx.DrawEllipse(CrownFill, CrownRimPen, new Point(mx + r, my - r * 0.25), jr, jr);
    }

    // A triple up-chevron ("level up here") marking a trainer room. Three stacked
    // carets drawn from stroke primitives (no glyph), sized to the room node like
    // the crown; a dark edge under the bright green keeps them legible on any fill.
    private static void DrawTrainerIcon(DrawingContext ctx, Rect cell)
    {
        double nodeSize = Math.Max(cell.Width * 0.45, 3.0);
        double mx = cell.X + cell.Width  / 2.0;
        double my = cell.Y + cell.Height / 2.0;
        double w = nodeSize * 0.26;   // half-width of each caret
        double h = nodeSize * 0.18;   // arm drop of each caret
        double topApexY = my - nodeSize * 0.26;
        double spacing  = nodeSize * 0.16;   // apex-to-apex; < h so the carets nest

        // All dark edges first, then all green on top, so a lower caret's edge never
        // covers the caret above it.
        for (int i = 0; i < 3; i++)
            DrawChevron(ctx, TrainerChevronEdgePen, mx, topApexY + i * spacing, w, h);
        for (int i = 0; i < 3; i++)
            DrawChevron(ctx, TrainerChevronPen, mx, topApexY + i * spacing, w, h);
    }

    // One upward caret (^): apex at (cx, apexY), arms dropping to ±w / +h.
    private static void DrawChevron(DrawingContext ctx, IPen pen, double cx, double apexY, double w, double h)
    {
        ctx.DrawLine(pen, new Point(cx - w, apexY + h), new Point(cx, apexY));
        ctx.DrawLine(pen, new Point(cx, apexY), new Point(cx + w, apexY + h));
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

    // Shared renderer for the numbered per-waypoint circle markers — the loop-builder
    // (red), the running loop (green), and Auto-Lair (amber) all use it. Markers are
    // numbered by list order (1, 2, 3, ...) so the map lines up with the CURRENT NAV
    // rows, and double as an easy-to-spot "this is a waypoint" indicator at any zoom.
    // A waypoint not on the current layout (different floor / disconnected island) is
    // skipped.
    private void DrawNumberedWaypoints(DrawingContext ctx, IReadOnlyList<RoomKey>? waypoints,
        IBrush fill, IPen ring, double tilePixels, double cx, double cy)
    {
        if (waypoints is not { Count: > 0 }) return;
        if (Layout is null) return;

        double radius = Math.Clamp(tilePixels * 0.34, 7.0, 15.0);
        Typeface tf = new("Inter", FontStyle.Normal, FontWeight.Bold);
        double textSize = Math.Clamp(tilePixels * 0.33, 10.0, 14.0);

        for (int i = 0; i < waypoints.Count; i++)
        {
            RoomKey key = waypoints[i];
            if (!TryGetCoord(key, out var coord)) continue;
            Rect cell = ComputeCellRect(coord, tilePixels, cx, cy);
            Point centre = new(
                cell.X + cell.Width  / 2.0,
                cell.Y + cell.Height / 2.0);
            ctx.DrawEllipse(fill, ring, centre, radius, radius);

            string label = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            FormattedText ft = new(label, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, tf, textSize, NumberedWaypointTextBrush);
            ctx.DrawText(ft, new Point(
                centre.X - ft.Width  / 2,
                centre.Y - ft.Height / 2));
        }
    }

    // Where a stub connector from the cell centre (mx, my) meets the cell edge
    // for a planar direction. Null for U / D (not rendered as stubs). Shared by
    // the stub line and the spell-wall placement so both agree on the stub geometry.
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

    // Which 30s respawn bucket a lair falls in: bucket 0 = 30s, 1 = 60s, ...
    // Clamped at 0 so a sub-30s value still lands on the fastest (red) stop.
    internal static int HeatBucketIndex(int seconds) =>
        Math.Max(0, (int)Math.Round((seconds - HeatBaseSeconds) / (double)HeatStepSeconds));

    // Fill + border for a lair's respawn seconds. Buckets 0..9 (30s..5min) use
    // the fixed rainbow; slower lairs fade purple->black over (5min, maxSeconds]
    // — maxSeconds is the whole-set longest lair, so it lands exactly on black.
    private static (IBrush fill, IPen pen) HeatColorFor(int seconds, int maxSeconds)
    {
        int bucket = HeatBucketIndex(seconds);
        if (bucket < HeatFixed.Length) return HeatFixed[bucket];

        int snapped = HeatBaseSeconds + bucket * HeatStepSeconds;
        int max = Math.Max(maxSeconds, snapped);
        var key = (snapped, max);
        if (_tailHeat.TryGetValue(key, out (IBrush fill, IPen pen) cached)) return cached;

        double denom = Math.Max(HeatStepSeconds, max - HeatFixedMaxSeconds);
        double t = Math.Clamp((snapped - HeatFixedMaxSeconds) / denom, 0.0, 1.0);
        Color purple = Color.Parse(HeatFixedHex[^1]);
        Color fillColor = LightenToward(purple, Colors.Black, t);
        var result = ((IBrush)new SolidColorBrush(fillColor),
                      (IPen)new Pen(new SolidColorBrush(LightenToward(fillColor, Colors.White, 0.4)), 1.5));
        _tailHeat[key] = result;
        return result;
    }

    // staticPass: drawn for the static layer, as neither current nor destination.
    private void DrawRoomNode(DrawingContext ctx, Rect cell, RoomKey key, bool staticPass)
    {
        double nodeSize = Math.Max(cell.Width * 0.45, 3.0);
        double nx = cell.X + (cell.Width  - nodeSize) / 2;
        double ny = cell.Y + (cell.Height - nodeSize) / 2;
        Rect node = new(nx, ny, nodeSize, nodeSize);

        bool isCurrent = !staticPass && CurrentRoomKey is { } current && current.Equals(key);
        bool isDestination = !staticPass && !isCurrent && DestinationRoomKey is { } dest && dest.Equals(key);
        bool isAutoLair = AutoLairRooms is not null && AutoLairRooms.Contains(key);
        Room? room = Graph?.GetRoom(key);

        IBrush fill;
        IPen pen;
        // True once the up/down hint has coloured the WHOLE node (no higher-priority
        // class claimed it) — the corner badge is then redundant and skipped.
        bool verticalFillIsPrimary = false;
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
        else if (LairMode != LairDisplayMode.Off && room is { HasLair: true })
        {
            if (LairMode is LairDisplayMode.Heat or LairDisplayMode.HeatCount
                && LairRespawnSeconds is { } respawn
                && respawn.TryGetValue(key, out int secs))
            {
                (fill, pen) = HeatColorFor(secs, LairMaxRespawnSeconds);
            }
            else
            {
                fill = LairFill;
                pen = LairBorderPen;
            }
        }
        else if (HighlightShops && room is { Shop: > 0 })
        {
            fill = ShopFill;
            pen = ShopBorderPen;
        }
        else if (SpellMode != SpellDisplayMode.Off && room is { Spell: > 0 })
        {
            (fill, pen) = SpellMode switch
            {
                SpellDisplayMode.ByName     => SpellColorFor(room.Spell),
                SpellDisplayMode.ByTeleport => SpellTeleportColorFor(room.Spell),
                _                           => (SpellFill, SpellBorderPen),
            };
        }
        else if (DrawLayout?.VerticalHints is { } vhints && vhints.TryGetValue(key, out VerticalHint hint))
        {
            (fill, pen) = hint switch
            {
                VerticalHint.Both => ((IBrush)UpDownFill, (IPen)UpDownBorderPen),
                VerticalHint.Up   => (UpFill,             UpBorderPen),
                VerticalHint.Down => (DownFill,           DownBorderPen),
                _                 => ((IBrush)RoomFill,   (IPen)RoomBorderPen),
            };
            // The whole node now carries the up/down colour, so the corner badge
            // would just be the same hue drawn on itself — suppress it below.
            verticalFillIsPrimary = hint != VerticalHint.None;
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

        // Vertical-exit corner badges — drawn only when a higher-priority class
        // (current / destination / Auto-Lair / Lair / Shop / Spell) claimed the
        // cell fill, so "this room goes up/down" still shows. When the up/down hint
        // IS the whole-node fill, the badge is redundant (same hue on itself) and
        // skipped — otherwise a rimmed triangle sits on a matching square.
        if (!verticalFillIsPrimary
            && DrawLayout?.VerticalHints is { } vh
            && vh.TryGetValue(key, out VerticalHint vhint)
            && vhint != VerticalHint.None)
        {
            DrawVerticalCornerBadge(ctx, node, vhint);
        }

        if (isCurrent || isDestination)
            DrawEndpointMarker(ctx, cell, isCurrent);
    }

    // The current / destination "thick perimeter ring + centre dot" marker — same shape
    // for both so the destination reads as "the other end of the pair", not a room class.
    // Drawn inside DrawRoomNode (the dynamic layer's current / destination nodes) AND
    // re-drawn on top of the route polylines later,
    // because a walk-to line ends at the destination and would otherwise cover its dot.
    private static void DrawEndpointMarker(DrawingContext ctx, Rect cell, bool current)
    {
        Rect ring = cell.Deflate(2);
        IPen  outerPen = current ? PlayerOuterPen : DestinationOuterPen;
        IBrush dotFill = current ? PlayerDotFill  : DestinationDotFill;
        IPen   dotPen  = current ? PlayerDotPen   : DestinationDotPen;
        ctx.DrawRectangle(null, outerPen, ring);

        double dotSize = Math.Max(cell.Width * 0.22, 4.0);
        double dx = cell.X + (cell.Width  - dotSize) / 2;
        double dy = cell.Y + (cell.Height - dotSize) / 2;
        Rect dot = new(dx, dy, dotSize, dotSize);
        ctx.DrawGeometry(dotFill, dotPen, new EllipseGeometry(dot));
    }

    // Re-draw the current + destination markers on top of the route polylines so the
    // walk-to line ending at the destination doesn't hide its centre dot (DrawDynamicLayer
    // already drew them under the routes; this lifts them back above).
    private void DrawEndpointMarkersOnTop(DrawingContext ctx, double tilePixels, double cx, double cy, Rect viewport)
    {
        if (Layout is null) return;
        void Draw(RoomKey? key, bool current)
        {
            if (key is not { } k || !TryGetCoord(k, out (int X, int Y) coord)) return;
            Rect cell = ComputeCellRect(coord, tilePixels, cx, cy);
            if (cell.Intersects(viewport)) DrawEndpointMarker(ctx, cell, current);
        }
        Draw(CurrentRoomKey, current: true);
        // Destination only when it's a different room than current (isCurrent wins in
        // DrawRoomNode, so match that here).
        if (DestinationRoomKey is { } dest && (CurrentRoomKey is not { } cur || !cur.Equals(dest)))
            Draw(dest, current: false);
    }

    // Draws small filled triangles in the right corners of the node to
    // indicate U/D exits:
    //   - top-right green triangle when the room has an Up exit;
    //   - bottom-right yellow triangle when it has a Down exit;
    //   - both triangles for Up+Down.
    // Only drawn when another class claimed the node fill, so the up/down hint
    // still shows. Triangle size scales with the node so it stays glanceable on small cells
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
            ctx.DrawGeometry(UpFill, VerticalBadgeEdgePen, geo);
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
            ctx.DrawGeometry(DownFill, VerticalBadgeEdgePen, geo);
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
            && (Layout.CoordToRoom.TryGetValue((gx, gy), out hit)
                || (OtherLevels?.CoordToRoom.TryGetValue((gx, gy), out hit) ?? false)))
        {
            return true;
        }
        return false;
    }
}
