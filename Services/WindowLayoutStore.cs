using Avalonia;
using Avalonia.Controls;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

// Per-character window position + size memory. Each top-level window calls
// AttachWindow once during construction with a stable id ("main", "backscroll",
// etc.); the store wires the Opened / Closing handlers so the window restores
// its prior bounds on Opened and captures the current ones on Closing.
//
// The dictionary is hydrated from CharacterProfile.WindowBounds on
// ProfileService.ProfileLoaded and snapshotted back on
// ProfileService.ProfileSaving. A window that the user has never moved / resized
// has no entry, so opening it uses whatever position + size the XAML declared.
//
// Tiny windows (under 80×60) and zero-sized windows are rejected on capture —
// those are usually transient measurements during the teardown sequence, not
// the user's "where I last left it" state.
public sealed class WindowLayoutStore
{
    private const double MinPersistedWidth = 80;
    private const double MinPersistedHeight = 60;

    private readonly Dictionary<string, WindowBounds> _bounds =
        new(StringComparer.OrdinalIgnoreCase);

    public WindowLayoutStore(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.ProfileLoaded += p => ApplyFromProfile(p.WindowBounds);
        profile.ProfileClosed += () => _bounds.Clear();
        profile.ProfileSaving += p => p.WindowBounds = Snapshot();
    }

    // Wire window's Opened / Closing handlers to the per-profile bounds store.
    // Idempotent: calling twice with the same id and window is a no-op (handlers
    // register once per Window instance).
    public void AttachWindow(Window window, string id)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        window.Opened += (_, _) => RestoreOnto(window, id);
        window.Closing += (_, _) => CaptureFrom(window, id);
    }

    // Take a snapshot of every known window's bounds — used by ProfileSaving.
    public Dictionary<string, WindowBounds> Snapshot()
        => new(_bounds, StringComparer.OrdinalIgnoreCase);

    // Replace the in-memory map with whatever a freshly-loaded profile carries.
    public void ApplyFromProfile(IReadOnlyDictionary<string, WindowBounds>? incoming)
    {
        _bounds.Clear();
        if (incoming is null) return;
        foreach ((string id, WindowBounds layout) in incoming)
            _bounds[id] = Clone(layout);
    }

    private void RestoreOnto(Window window, string id)
    {
        if (!_bounds.TryGetValue(id, out WindowBounds? layout)) return;

        if (layout.Width >= MinPersistedWidth && layout.Height >= MinPersistedHeight)
        {
            window.Width = layout.Width;
            window.Height = layout.Height;
        }

        // Position only when it looks set (X+Y both zero is the WM
        // "Centre on parent" fallback we don't want to override).
        if (layout.X != 0 || layout.Y != 0)
        {
            window.Position = new PixelPoint((int)layout.X, (int)layout.Y);
        }

        window.WindowState = layout.Maximized
            ? WindowState.Maximized
            : WindowState.Normal;
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

    private static WindowBounds Clone(WindowBounds src) => new()
    {
        X = src.X,
        Y = src.Y,
        Width = src.Width,
        Height = src.Height,
        Maximized = src.Maximized,
    };
}
