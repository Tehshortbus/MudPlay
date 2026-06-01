using Avalonia.Controls;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

/// <summary>
/// Per-character splitter-position memory for two-pane resizable
/// dialogs. Parallels <see cref="WindowLayoutStore"/> for the
/// horizontal split inside a window (e.g. the MonsterEditDialog's
/// editable-vs-MDB pane split).
/// </summary>
/// <remarks>
/// <para>
/// Each dialog calls <see cref="AttachGrid"/> once during construction
/// with a stable id + the Grid whose ColumnDefinitions to drive + the
/// two column indexes (LEFT and RIGHT of the splitter). The store
/// wires the parent <see cref="Window"/>'s Opened + Closing handlers
/// so the column widths restore from the profile on open and the
/// current ratio captures back on close.
/// </para>
/// <para>
/// Ratio semantics: stored value is <c>leftWidth / (leftWidth + rightWidth)</c>
/// at close time. On restore the value is applied as a star-width pair
/// <c>(ratio*, (1-ratio)*)</c> so the dialog still scales when the
/// user resizes the window. Restore is skipped when the ratio looks
/// degenerate (less than 5% or more than 95%) so a stuck splitter
/// doesn't hide a whole pane.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Wire <paramref name="grid"/>'s ColumnDefinitions to the
    /// per-profile ratio store. Calls back into the supplied
    /// <paramref name="owner"/> window for Opened / Closing events so
    /// the dialog doesn't have to manage subscriptions itself.
    /// </summary>
    /// <param name="owner">The host window — drives the lifecycle events.</param>
    /// <param name="grid">The Grid whose ColumnDefinitions carry the splittable columns.</param>
    /// <param name="leftColumnIndex">Index of the LEFT column in <paramref name="grid"/>.ColumnDefinitions.</param>
    /// <param name="rightColumnIndex">Index of the RIGHT column.</param>
    /// <param name="id">Stable identifier — used as the dictionary key.</param>
    public void AttachGrid(Window owner, Grid grid, int leftColumnIndex, int rightColumnIndex, string id)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        owner.Opened  += (_, _) => RestoreOnto(grid, leftColumnIndex, rightColumnIndex, id);
        owner.Closing += (_, _) => CaptureFrom(grid, leftColumnIndex, rightColumnIndex, id);
    }

    /// <summary>Snapshot every known ratio — used by ProfileSaving.</summary>
    public Dictionary<string, double> Snapshot()
        => new(_ratios, StringComparer.OrdinalIgnoreCase);

    /// <summary>Replace the in-memory map with whatever a freshly-loaded profile carries.</summary>
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
}
