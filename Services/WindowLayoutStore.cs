using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

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

    public WindowLayoutStore(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.ProfileLoaded += p => ApplyFromProfile(p.WindowBounds);
        profile.ProfileClosed += () => _bounds.Clear();
        profile.ProfileSaving += p => p.WindowBounds = Snapshot();
    }

    // Wire window's Opened / Closing / Closed handlers to the per-profile bounds
    // store. Handlers register once per Window instance.
    public void AttachWindow(Window window, string id)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        window.Opened += (_, _) =>
        {
            _open[id] = window;
            RestoreOnto(window, id);
        };
        window.Closing += (_, _) => CaptureFrom(window, id);
        window.Closed += (_, _) =>
        {
            if (_open.TryGetValue(id, out Window? tracked) && ReferenceEquals(tracked, window))
                _open.Remove(id);
        };
    }

    // Take a snapshot of every known window's bounds — used by ProfileSaving.
    public Dictionary<string, WindowBounds> Snapshot()
        => new(_bounds, StringComparer.OrdinalIgnoreCase);

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

        if (layout.Width >= MinPersistedWidth && layout.Height >= MinPersistedHeight)
        {
            window.Width = layout.Width;
            window.Height = layout.Height;
        }

        if (layout.Maximized)
        {
            window.WindowState = WindowState.Maximized;
            return;
        }
        window.WindowState = WindowState.Normal;

        // X+Y both zero is the "never positioned" sentinel — let the WM place it
        // (CenterOwner) rather than pinning to the desktop origin.
        if (layout.X == 0 && layout.Y == 0) return;

        window.Position = ResolvePosition(window, layout);
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
        // Don't capture transient zero / collapsing sizes — those usually
        // happen during the teardown rather than reflecting where the
        // user actually left the window.
        if (window.Width < MinPersistedWidth || window.Height < MinPersistedHeight)
            return;

        PixelPoint pos = window.Position;
        _bounds[id] = new WindowBounds
        {
            X = pos.X,
            Y = pos.Y,
            Width = window.Width,
            Height = window.Height,
            Maximized = window.WindowState == WindowState.Maximized,
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
