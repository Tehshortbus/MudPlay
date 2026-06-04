using FujinTerm.Game.Map;

namespace FujinTerm.ViewModels.Navigation;

/// <summary>
/// One entry in the Navigation right-rail search results list. Carries
/// just enough to render a row (primary + secondary line + optional
/// step distance) and the key the user-pick callback needs.
/// </summary>
/// <remarks>
/// <para>
/// Two row shapes share this record because the dropdown renders them
/// in a single uniform template:
/// </para>
/// <list type="bullet">
/// <item><term>Plain room match</term><description>
/// <see cref="MonsterTag"/> is null. <see cref="PrimaryLine"/> shows
/// <c>"M/R - Name"</c>, <see cref="SecondaryLine"/> the step distance.
/// </description></item>
/// <item><term>Monster-room match</term><description>
/// <see cref="MonsterTag"/> set (e.g. <c>"Goblin Warrior · regen 4h"</c>).
/// <see cref="PrimaryLine"/> shows the monster header, <see cref="SecondaryLine"/>
/// the room reference + step distance. Multiple rooms hosting the same
/// monster surface as multiple entries — clicking one queues that
/// specific room.
/// </description></item>
/// </list>
/// </remarks>
public sealed record RoomSearchResult(
    RoomKey Key,
    string Name,
    int? StepsFromCurrent,
    string? MonsterTag = null)
{
    /// <summary>Legacy alias for older bindings — same as <see cref="PrimaryLine"/>'s room form.</summary>
    public string DisplayName => $"{Key.Map}/{Key.Room} - {Name}";

    /// <summary>Legacy sublabel for older bindings.</summary>
    public string DisplayLocation => StepsFromCurrent switch
    {
        null => string.Empty,
        0    => "here",
        1    => "1 step",
        _    => $"{StepsFromCurrent} steps",
    };

    /// <summary>Top line in the dropdown row. Monster tag when present, otherwise the room reference.</summary>
    public string PrimaryLine => MonsterTag ?? $"{Key.Map}/{Key.Room} - {Name}";

    /// <summary>Bottom line: when this is a monster match, the underlying room; otherwise the step distance.</summary>
    public string SecondaryLine => MonsterTag is null
        ? DisplayLocation
        : (StepsFromCurrent switch
        {
            null => $"{Key.Map}/{Key.Room} - {Name}",
            0    => $"{Key.Map}/{Key.Room} - {Name} · here",
            1    => $"{Key.Map}/{Key.Room} - {Name} · 1 step",
            _    => $"{Key.Map}/{Key.Room} - {Name} · {StepsFromCurrent} steps",
        });
}
