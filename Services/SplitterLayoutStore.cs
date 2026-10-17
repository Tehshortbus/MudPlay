using Avalonia.Controls;
using MudPlay.Models.Profile;

namespace MudPlay.Services;

// Per-character splitter-position memory for two-pane resizable dialogs.
// Parallels WindowLayoutStore for the split inside a window or tab (e.g. the
// Monster Intel window's two panes).
//
// Each dialog calls AttachGrid once during construction with a stable id + the
// Grid whose ColumnDefinitions to drive + the two column indexes (LEFT and
// RIGHT of the splitter). The store wires the parent Window's Opened + Closing
// handlers so the column widths restore from the profile on open and the
// current ratio captures back on close.
//
// Ratio semantics: stored value is leftWidth / (leftWidth + rightWidth) at
// close time. On restore the value is applied as a star-width pair
// (ratio*, (1-ratio)*) so the dialog still scales when the user resizes the
// window. Restore is skipped when the ratio looks degenerate (less than 5% or
// more than 95%) so a stuck splitter doesn't hide a whole pane.
public sealed class SplitterLayoutStore
{
    private const double MinRatio = 0.05;
    private const double MaxRatio = 0.95;

    private readonly Dictionary<string, double> _ratios =
        new(StringComparer.OrdinalIgnoreCase);

    public SplitterLayoutStore(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.ProfileLoaded += p => ApplyFromProfile(p.SplitterRatios);
        profile.ProfileClosed += () => _ratios.Clear();
        profile.ProfileSaving += p => p.SplitterRatios = Snapshot();
    }

    // Wire grid's ColumnDefinitions to the per-profile ratio store. Calls back
    // into the supplied owner window for Opened / Closing events so the dialog
    // doesn't have to manage subscriptions itself. owner is the host window that
    // drives the lifecycle events; leftColumnIndex / rightColumnIndex are the
    // LEFT / RIGHT column indexes in grid.ColumnDefinitions; id is the stable
    // dictionary key.
    public void AttachGrid(Window owner, Grid grid, int leftColumnIndex, int rightColumnIndex, string id)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        owner.Opened  += (_, _) => RestoreOnto(grid, leftColumnIndex, rightColumnIndex, id);
        owner.Closing += (_, _) => CaptureFrom(grid, leftColumnIndex, rightColumnIndex, id);
    }

    // The same for a grid that lives in a tab rather than a window of its own. A tab
    // has no Opened / Closing to follow: it is restored each time it comes on screen
    // and captured when it leaves, and also as the window around it closes, since a
    // closing window doesn't take its content off screen first.
    public void AttachGridRows(Control host, Grid grid, int topRowIndex, int bottomRowIndex, string id)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        Window? window = null;
        void Capture(object? sender, EventArgs e) => CaptureFromRows(grid, topRowIndex, bottomRowIndex, id);
        host.AttachedToVisualTree += (_, _) =>
        {
            RestoreRowsOnto(grid, topRowIndex, bottomRowIndex, id);
            window = TopLevel.GetTopLevel(host) as Window;
            if (window is not null) window.Closing += Capture;
        };
        host.DetachedFromVisualTree += (_, _) =>
        {
            Capture(null, EventArgs.Empty);
            if (window is not null) window.Closing -= Capture;
            window = null;
        };
    }

    // Snapshot every known ratio, for the profile save.
    public Dictionary<string, double> Snapshot()
        => new(_ratios, StringComparer.OrdinalIgnoreCase);

    // Replace the in-memory map with whatever a freshly-loaded profile carries.
    public void ApplyFromProfile(IReadOnlyDictionary<string, double>? incoming)
    {
        _ratios.Clear();
        if (incoming is null) return;
        foreach ((string id, double r) in incoming) _ratios[id] = r;
    }

    private void RestoreOnto(Grid grid, int leftIdx, int rightIdx, string id)
    {
        if (!_ratios.TryGetValue(id, out double ratio)) return;
        if (ratio < MinRatio || ratio > MaxRatio) return;
        if (leftIdx >= grid.ColumnDefinitions.Count) return;
        if (rightIdx >= grid.ColumnDefinitions.Count) return;

        // Stored as a star-width pair (ratio*, (1-ratio)*) so the
        // pane widths scale proportionally when the dialog itself is
        // resized. The middle splitter column keeps its declared
        // fixed width — unchanged.
        grid.ColumnDefinitions[leftIdx].Width  = new GridLength(ratio,       GridUnitType.Star);
        grid.ColumnDefinitions[rightIdx].Width = new GridLength(1.0 - ratio, GridUnitType.Star);
    }

    private void CaptureFrom(Grid grid, int leftIdx, int rightIdx, string id)
    {
        if (leftIdx >= grid.ColumnDefinitions.Count) return;
        if (rightIdx >= grid.ColumnDefinitions.Count) return;

        double left  = grid.ColumnDefinitions[leftIdx].ActualWidth;
        double right = grid.ColumnDefinitions[rightIdx].ActualWidth;
        double total = left + right;
        if (total <= 0) return;

        double ratio = left / total;
        if (ratio < MinRatio || ratio > MaxRatio) return;
        _ratios[id] = ratio;
    }

    private void RestoreRowsOnto(Grid grid, int topIdx, int bottomIdx, string id)
    {
        if (!_ratios.TryGetValue(id, out double ratio)) return;
        if (ratio < MinRatio || ratio > MaxRatio) return;
        if (topIdx >= grid.RowDefinitions.Count) return;
        if (bottomIdx >= grid.RowDefinitions.Count) return;

        grid.RowDefinitions[topIdx].Height    = new GridLength(ratio,       GridUnitType.Star);
        grid.RowDefinitions[bottomIdx].Height = new GridLength(1.0 - ratio, GridUnitType.Star);
    }

    private void CaptureFromRows(Grid grid, int topIdx, int bottomIdx, string id)
    {
        if (topIdx >= grid.RowDefinitions.Count) return;
        if (bottomIdx >= grid.RowDefinitions.Count) return;

        double top    = grid.RowDefinitions[topIdx].ActualHeight;
        double bottom = grid.RowDefinitions[bottomIdx].ActualHeight;
        double total  = top + bottom;
        if (total <= 0) return;

        double ratio = top / total;
        if (ratio < MinRatio || ratio > MaxRatio) return;
        _ratios[id] = ratio;
    }
}
