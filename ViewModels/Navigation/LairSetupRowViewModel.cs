using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.Navigation;

/// <summary>
/// Display-only wrapper around a saved <see cref="LairSetup"/> for the
/// Navigation right-rail "Loops + Auto-Lairs" section. Mirrors
/// <see cref="LoopRowViewModel"/> so the two row templates line up
/// visually; the amber accent vs the loop-row green is the only
/// foreground difference.
/// </summary>
public sealed class LairSetupRowViewModel
{
    public LairSetup Source { get; }
    public string Name => Source.Name;

    /// <summary>"3 lairs" — marker count as a one-line sublabel.</summary>
    public string SubLabel =>
        $"{Source.MarkerCount} lair{(Source.MarkerCount == 1 ? "" : "s")}";

    /// <summary>
    /// "{map}/{room}" of the first marker, or empty when the setup is
    /// empty. Shown alongside <see cref="SubLabel"/> so the user can
    /// distinguish similarly-named setups by their anchor without
    /// expanding the row.
    /// </summary>
    public string AnchorKey =>
        Source.Markers.Count == 0
            ? string.Empty
            : $"{Source.Markers[0].Map}/{Source.Markers[0].Room}";

    public LairSetupRowViewModel(LairSetup source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
    }
}
