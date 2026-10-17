using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using MudPlay.Models.Profile;

namespace MudPlay.Services;

// Per-character window position + size memory. Each top-level window calls
// AttachWindow once during construction with a stable id ("main", "backscroll",
// etc.); the store wires Opened / Closing / Closed so the window restores its
// prior bounds on Opened, captures the current ones on Closing, and drops out of
// the open-set on Closed.
//
// The dictionary is hydrated from CharacterProfile.WindowBounds on
// ProfileService.ProfileLoaded and snapshotted back on
// ProfileService.ProfileSaving. A window that the user has never moved / resized
// has no entry, so opening it uses whatever position + size the XAML declared.
//
// Loading a profile also re-applies its layout to windows already open — their
// Opened handler ran against the previous profile, and Opened won't fire again
// on an open window, so a profile switch has to move them explicitly.
//
// Which windows are open is saved with the profile too (OpenWindows): loading it
// opens the ones it had open and closes the ones it didn't, so each profile comes
// back to exactly its own layout. Only windows that registered an opener
// (RegisterOpener) take part. The save that matters runs from the main window's own
// Closing handler, before any child window closes, so the open set is still whole
// then. At startup the profile can load before the main window is up; the reopen
// waits for it.
//
// Restore is screen-aware: a saved position still visible on a connected monitor
// is honoured as-is (a window intentionally parked on a second screen reopens
// there), but one whose monitor was unplugged — or that now falls off every
// screen and can't be grabbed — is re-anchored next to the main UI and clamped
// fully on-screen, so the app never opens split across a monitor that's gone.
//
// Tiny windows (under 80×60) and zero-sized windows are rejected on capture —
// those are usually transient measurements during the teardown sequence, not
// the user's "where I last left it" state.
public sealed class WindowLayoutStore
{
    private const double MinPersistedWidth = 80;
    private const double MinPersistedHeight = 60;

    // A saved position counts as reachable only when at least this much of the
    // window overlaps some connected screen's working area — enough of the
    // title-bar band to grab and drag. Below this the window is treated as
    // off-screen and re-anchored next to the main UI.
    private const int MinReachableWidth = 120;
    private const int MinReachableHeight = 24;

    private readonly Dictionary<string, WindowBounds> _bounds =
        new(StringComparer.OrdinalIgnoreCase);

    // Windows attached and currently open, keyed by id — the set a profile load
    // re-applies the freshly-loaded layout to.
    private readonly Dictionary<string, Window> _open =
        new(StringComparer.OrdinalIgnoreCase);

    // How to open each window a profile can reopen, keyed by id.
    private readonly Dictionary<string, Action> _openers =
        new(StringComparer.OrdinalIgnoreCase);

    // Windows never recorded as open: the main window is always open, and Profile
    // Management is where profiles are switched from, so it's open at every switch's
    // save and would otherwise follow every profile.
    private static readonly HashSet<string> NotRecorded =
        new(StringComparer.OrdinalIgnoreCase) { MainWindowId, "profilemgr" };

    public const string MainWindowId = "main";

    // A loaded profile's open-window list waiting for the main window to open.
    private IReadOnlyList<string>? _pendingOpen;
    private bool _suspendCapture;

    // Ids whose height is content-driven (SizeToContent="Height"). Their saved
    // width + position restore, but a saved height is never re-applied — pinning it
    // would freeze SizeToContent, so the window couldn't shrink/grow as its panels
    // are toggled.
    private readonly HashSet<string> _autoHeightIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _autoSizeIds =
        new(StringComparer.OrdinalIgnoreCase);

    // Edge-snapping / cluster-move for the panel windows. Registered per window in
    // AttachWindow; notified before a programmatic reposition so it doesn't read a
    // layout restore as a user drag. Null when none was supplied.
    private readonly WindowSnapManager? _snap;

    public WindowLayoutStore(ProfileService profile, WindowSnapManager? snap = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _snap = snap;
        profile.ProfileLoaded += p =>
        {
            ApplyFromProfile(p.WindowBounds);
            RestoreOpenWindows(p.OpenWindows);
        };
        profile.ProfileClosed += () => _bounds.Clear();
        profile.ProfileSaving += p =>
        {
            p.WindowBounds = Snapshot();
            p.OpenWindows = OpenWindowIds();
        };
    }

    // How to open the window with this id when a profile had it open.
    public void RegisterOpener(string id, Action open)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(open);
        _openers[id] = open;
    }

    // The ids of the open windows a profile records, sorted.
    public List<string> OpenWindowIds()
        => _open.Keys.Where(id => !NotRecorded.Contains(id))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();

    // Open the windows a loaded profile had open and close the ones it didn't —
    // once the main window is up, since the others open on top of it, and after the
    // rest of the profile load has run (posted), so a reopened window reads the new
    // profile.
    public void RestoreOpenWindows(IReadOnlyList<string>? saved)
    {
        if (saved is null) return;
        if (!_open.ContainsKey(MainWindowId))
        {
            _pendingOpen = saved;
            return;
        }
        Dispatcher.UIThread.Post(() => MatchOpenWindows(saved));
    }

    private void MatchOpenWindows(IReadOnlyList<string> saved)
    {
        (List<string> toOpen, List<string> toClose) = PlanOpenWindows(saved, _open.Keys, _openers.Keys);
        // The loaded profile's layout is already in place; a window closing because it
        // didn't have it open mustn't write the previous profile's position into it.
        _suspendCapture = true;
        try
        {
            foreach (string id in toClose)
                if (_open.TryGetValue(id, out Window? w)) w.Close();
        }
        finally { _suspendCapture = false; }
        foreach (string id in toOpen)
            _openers[id]();
    }

    // Which windows to open and which to close so the open set matches the profile's
    // saved list. Only windows with an opener take part; the main window never does.
    public static (List<string> ToOpen, List<string> ToClose) PlanOpenWindows(
        IEnumerable<string> saved, IEnumerable<string> open, IEnumerable<string> managed)
    {
        HashSet<string> want = new(saved, StringComparer.OrdinalIgnoreCase);
        HashSet<string> have = new(open, StringComparer.OrdinalIgnoreCase);
        List<string> toOpen = new(), toClose = new();
        foreach (string id in managed.OrderBy(i => i, StringComparer.OrdinalIgnoreCase))
        {
            if (NotRecorded.Contains(id)) continue;
            if (want.Contains(id) && !have.Contains(id)) toOpen.Add(id);
            else if (!want.Contains(id) && have.Contains(id)) toClose.Add(id);
        }
        return (toOpen, toClose);
    }

    // Wire window's Opened / Closing / Closed handlers to the per-profile bounds
    // store. Handlers register once per Window instance. Pass autoHeight: true for
    // a SizeToContent="Height" window so its saved height is never re-applied (its
    // content owns the height), or autoSize: true for one whose content owns both
    // its width and height, so only its position is restored.
    public void AttachWindow(Window window, string id, bool autoHeight = false, bool autoSize = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (autoHeight) _autoHeightIds.Add(id);
        if (autoSize) _autoSizeIds.Add(id);
        _snap?.Register(window, id);

        window.Opened += (_, _) =>
        {
            _open[id] = window;
            RestoreOnto(window, id);
            // A profile loaded before the main window was up: reopen its windows now.
            if (id.Equals(MainWindowId, StringComparison.OrdinalIgnoreCase) && _pendingOpen is { } pending)
            {
                _pendingOpen = null;
                Dispatcher.UIThread.Post(() => MatchOpenWindows(pending));
            }
        };
        window.Closing += (_, _) => { if (!_suspendCapture) CaptureFrom(window, id); };
        window.Closed += (_, _) =>
        {
            if (_open.TryGetValue(id, out Window? tracked) && ReferenceEquals(tracked, window))
                _open.Remove(id);
        };
    }

    // Take a snapshot of every known window's bounds, for the profile save.
    //
    // CAPTURE THE STILL-OPEN WINDOWS FIRST. Bounds were only ever recorded in
    // the Closing handler, which is one event too late for the save that
    // matters: the profile is written from MainWindow's OWN Closing handler
    // (Views/MainWindow.axaml.cs), and Closing fires BEFORE the window closes,
    // so at that instant not one child window has closed and none of their
    // Closing handlers has run. The save therefore persisted whatever was
    // captured the last time each panel happened to be closed individually —
    // then the children tore down, updated the dictionary, and the process
    // exited without another save.
    //
    // The symptom is per-window, which is what made it look arbitrary: a panel
    // you close yourself before quitting is remembered, and one you leave open
    // is frozen at an ancient position or has no entry at all — in which case
    // RestoreOnto returns early and the window manager places it (CenterOwner)
    // wherever the main window happens to be. Reported as MudPlay putting
    // "my windows in random locations when I reopen the application".
    //
    // Live capture belongs here rather than on PositionChanged: this runs on
    // every save path — exit, Ctrl+S, and the save a profile SWITCH does before
    // ProfileClosed clears the map — and it costs one dictionary write per open
    // window instead of one per pixel of every drag.
    public Dictionary<string, WindowBounds> Snapshot()
    {
        foreach ((string id, Window window) in _open.ToArray())
            CaptureFrom(window, id);
        return new(_bounds, StringComparer.OrdinalIgnoreCase);
    }

    // Replace the in-memory map with whatever a freshly-loaded profile carries,
    // then move any already-open window onto that profile's saved layout.
    public void ApplyFromProfile(IReadOnlyDictionary<string, WindowBounds>? incoming)
    {
        _bounds.Clear();
        if (incoming is not null)
        {
            foreach ((string id, WindowBounds layout) in incoming)
                _bounds[id] = Clone(layout);
        }

        foreach ((string id, Window window) in _open.ToArray())
            RestoreOnto(window, id);
    }

    private void RestoreOnto(Window window, string id)
    {
        if (!_bounds.TryGetValue(id, out WindowBounds? layout)) return;

        // Content-sized windows restore no size at all, and content-auto-height
        // ones width only — setting a sized dimension would clobber SizeToContent
        // and freeze the window at its last size.
        if (_autoHeightIds.Contains(id))
        {
            if (layout.Width >= MinPersistedWidth) window.Width = layout.Width;
        }
        else if (!_autoSizeIds.Contains(id)
                 && layout.Width >= MinPersistedWidth && layout.Height >= MinPersistedHeight)
        {
            window.Width = layout.Width;
            window.Height = layout.Height;
        }

        // POSITION FIRST, THEN THE STATE. A maximized layout used to return
        // before the position was ever applied, so restoring the window DOWN
        // dropped it wherever the window manager felt like — and the position
        // also decides which monitor it maximizes onto.
        //
        // X+Y both zero is the "never positioned" sentinel — let the WM place it
        // (CenterOwner) rather than pinning to the desktop origin.
        if (layout.X != 0 || layout.Y != 0)
        {
            PixelPoint resolved = ResolvePosition(window, layout);
            // Tell the snap manager this is our reposition, not a user drag, so a
            // profile-load re-layout of the main window doesn't haul the cluster.
            _snap?.ExpectMove(id, resolved);
            window.Position = resolved;
        }

        window.WindowState = layout.Maximized
            ? WindowState.Maximized
            : WindowState.Normal;
    }

    // Turn a saved position into an on-screen one. Honour a position still
    // visible on a connected monitor; otherwise re-anchor next to the main UI.
    private PixelPoint ResolvePosition(Window window, WindowBounds layout)
    {
        PixelPoint saved = new((int)layout.X, (int)layout.Y);

        Screens? screens = window.Screens;
        if (screens is null || screens.All.Count == 0)
            return saved;

        // Position is physical pixels; Width/Height are device-independent —
        // scale by the target screen to compare against the physical-pixel
        // working area.
        double scale = screens.ScreenFromPoint(saved)?.Scaling ?? window.DesktopScaling;
        PixelSize frame = new(
            (int)Math.Ceiling(window.Width * scale),
            (int)Math.Ceiling(window.Height * scale));
        PixelRect rect = new(saved, frame);

        return IsReachable(screens, rect)
            ? saved
            : ReanchorNextToMain(screens, window, frame);
    }

    private static bool IsReachable(Screens screens, PixelRect rect)
    {
        foreach (Screen screen in screens.All)
        {
            PixelRect overlap = screen.WorkingArea.Intersect(rect);
            if (overlap.Width >= MinReachableWidth && overlap.Height >= MinReachableHeight)
                return true;
        }
        return false;
    }

    private static PixelPoint ReanchorNextToMain(Screens screens, Window window, PixelSize frame)
    {
        Window? main = MainWindow;
        bool isMain = ReferenceEquals(window, main);

        Screen target =
            (!isMain && main is not null ? screens.ScreenFromVisual(main) : null)
            ?? screens.Primary
            ?? screens.All[0];

        PixelRect area = target.WorkingArea;

        // A child window cascades just off the main window's corner; the main
        // window itself (or a child with no main to anchor to) centres.
        PixelPoint start = (!isMain && main is not null)
            ? new PixelPoint(main.Position.X + 40, main.Position.Y + 40)
            : new PixelPoint(
                area.X + Math.Max(0, (area.Width - frame.Width) / 2),
                area.Y + Math.Max(0, (area.Height - frame.Height) / 2));

        int maxX = Math.Max(area.X, area.Right - frame.Width);
        int maxY = Math.Max(area.Y, area.Bottom - frame.Height);
        return new PixelPoint(
            Math.Clamp(start.X, area.X, maxX),
            Math.Clamp(start.Y, area.Y, maxY));
    }

    private void CaptureFrom(Window window, string id)
    {
        _bounds.TryGetValue(id, out WindowBounds? previous);
        WindowBounds? captured = BoundsToPersist(
            window.WindowState, window.Position, window.Width, window.Height, previous);
        if (captured is not null) _bounds[id] = captured;
    }

    // Whether a window's current geometry is worth remembering, and as what.
    // Null means "keep the last good bounds". Pure so the two guards below can
    // be tested without a live Window — the window plumbing itself is Avalonia
    // UI and is not unit-tested (see WindowSnapManagerTests).
    internal static WindowBounds? BoundsToPersist(
        WindowState state, PixelPoint pos, double width, double height,
        WindowBounds? previous = null)
    {
        // A minimized window reports a bogus off-screen position (Windows parks
        // minimized windows at ~(-32000,-32000)), so capturing here would overwrite
        // the real "where I left it" with garbage that ResolvePosition then treats as
        // off-screen and re-anchors next to main — the panes "lose their memory" after
        // a Win+D (show-desktop) minimize-all followed by a client restart (report
        // paradigm-20260827-081318). Skip the capture and keep the last good bounds.
        if (state == WindowState.Minimized)
            return null;

        // A MAXIMIZED WINDOW REPORTS ITS MAXIMIZED FRAME, which is not the size
        // to restore it to. Recording it meant a window maximized at exit came
        // back maximized (right) but carrying the whole screen as its NORMAL
        // size, so un-maximizing left it screen-sized instead of the size the
        // user had actually chosen.
        //
        // The geometry already stored IS that size — written by a capture taken
        // while the window was Normal, or inherited from the profile, and
        // nothing else overwrites it — so a maximized capture keeps it and
        // changes only the flag. That needs no PositionChanged tracking and has
        // no race with the platform's own maximize, which updates WindowState
        // and the frame in an order we do not control.
        //
        // With nothing remembered yet (maximized before this window was ever
        // captured Normal) the maximized frame is all there is; the next
        // capture taken Normal replaces it.
        if (state == WindowState.Maximized && previous is not null)
        {
            return new WindowBounds
            {
                X = previous.X,
                Y = previous.Y,
                Width = previous.Width,
                Height = previous.Height,
                Maximized = true,
            };
        }

        // Don't capture transient zero / collapsing sizes — those usually
        // happen during the teardown rather than reflecting where the
        // user actually left the window.
        if (width < MinPersistedWidth || height < MinPersistedHeight)
            return null;

        return new WindowBounds
        {
            X = pos.X,
            Y = pos.Y,
            Width = width,
            Height = height,
            Maximized = state == WindowState.Maximized,
        };
    }

    private static Window? MainWindow =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

    private static WindowBounds Clone(WindowBounds src) => new()
    {
        X = src.X,
        Y = src.Y,
        Width = src.Width,
        Height = src.Height,
        Maximized = src.Maximized,
    };
}
