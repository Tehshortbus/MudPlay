using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.Navigation;

// Display-only wrapper around a saved LairSetup for the Navigation
// right-rail "Loops + Auto-Lairs" section. Mirrors LoopRowViewModel so the
// two row templates line up visually; the amber accent vs the loop-row
// green is the only foreground difference.
public sealed class LairSetupRowViewModel
{
    public LairSetup Source { get; }
    public string Name => Source.Name;

    // "3 lairs" — marker count as a one-line sublabel.
    public string SubLabel =>
        $"{Source.MarkerCount} lair{(Source.MarkerCount == 1 ? "" : "s")}";

    // "{map}/{room}" of the first marker, or empty when the setup is empty.
    // Shown alongside SubLabel so the user can distinguish similarly-named
    // setups by their anchor without expanding the row.
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
