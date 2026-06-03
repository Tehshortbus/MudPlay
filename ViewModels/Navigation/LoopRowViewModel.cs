using FujinTerm.Game.Map;

namespace FujinTerm.ViewModels.Navigation;

/// <summary>
/// Display-only wrapper around a <see cref="Loop"/> for the
/// Navigation right-rail LOOPS section. Carries the human-friendly
/// labels the list template binds to plus the underlying
/// <see cref="Source"/> for the Run / Edit / Delete commands.
/// </summary>
public sealed class LoopRowViewModel
{
    public Loop Source { get; }
    public string Name => Source.Name;

    /// <summary>"4 rooms · L3" — room count + the editor-set level tag if present. Level is not stored on Loop yet; this just shows the count.</summary>
    public string SubLabel => $"{Source.RoomCount} room{(Source.RoomCount == 1 ? "" : "s")}";

    /// <summary>"2h ago" / "yesterday" / "—".</summary>
    public string LastRunBadge => Source.LastRunAt is not { } ts ? "—" : Humanise(DateTimeOffset.UtcNow - ts);

    public LoopRowViewModel(Loop source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
    }

    private static string Humanise(TimeSpan span)
    {
        if (span.TotalSeconds < 60) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24)   return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 2)     return "yesterday";
        if (span.TotalDays < 7)     return $"{(int)span.TotalDays}d ago";
        return $"{(int)(span.TotalDays / 7)}w ago";
    }
}
