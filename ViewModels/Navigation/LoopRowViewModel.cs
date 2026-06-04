using FujinTerm.Game.Map;

namespace FujinTerm.ViewModels.Navigation;

/// <summary>
/// Display-only wrapper around a <see cref="Loop"/> for the
/// Navigation right-rail LOOPS section. Carries the human-friendly
/// labels the list template binds to plus the underlying
/// <see cref="Source"/> for the Run command.
/// </summary>
public sealed class LoopRowViewModel
{
    public Loop Source { get; }
    public string Name => Source.Name;

    /// <summary>"4 rooms" — per user direction the rail shows only name + count.</summary>
    public string SubLabel => $"{Source.RoomCount} room{(Source.RoomCount == 1 ? "" : "s")}";

    public LoopRowViewModel(Loop source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
    }
}
