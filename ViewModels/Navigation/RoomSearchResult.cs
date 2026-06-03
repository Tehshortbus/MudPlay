using FujinTerm.Game.Map;

namespace FujinTerm.ViewModels.Navigation;

/// <summary>
/// One entry in the Navigation right-rail search results list. Carries
/// just enough to render a row (name + map/room badge + optional step
/// distance) and the key the user-pick callback needs.
/// </summary>
public sealed record RoomSearchResult(RoomKey Key, string Name, int? StepsFromCurrent)
{
    /// <summary>Right-rail label — <c>"Town Gates"</c>.</summary>
    public string DisplayName => Name;

    /// <summary>Right-rail sub-label — <c>"1/1 · 14 steps"</c> or <c>"1/1 · here"</c>.</summary>
    public string DisplayLocation => StepsFromCurrent switch
    {
        null => $"{Key}",
        0    => $"{Key} · here",
        1    => $"{Key} · 1 step",
        _    => $"{Key} · {StepsFromCurrent} steps",
    };
}
