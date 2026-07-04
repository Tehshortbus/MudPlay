using FujinTerm.Game.Map;

namespace FujinTerm.ViewModels.Navigation;

// One entry in the Navigation right-rail search results list. Carries just
// enough to render a row (primary + secondary line + optional step distance)
// and the key the user-pick callback needs.
//
// Two row shapes share this record because the dropdown renders them in a
// single uniform template:
//   - Plain room match: MonsterTag is null. PrimaryLine shows "M/R - Name",
//     SecondaryLine the step distance.
//   - Monster-room match: MonsterTag set (e.g. "Goblin Warrior · regen 4h").
//     PrimaryLine shows the monster header, SecondaryLine the room reference
//     + step distance. Multiple rooms hosting the same monster surface as
//     multiple entries — clicking one queues that specific room.
public sealed record RoomSearchResult(
    RoomKey Key,
    string Name,
    int? StepsFromCurrent,
    string? MonsterTag = null)
{
    // Legacy alias for older bindings — same as PrimaryLine's room form.
    public string DisplayName => $"{Key.Map}/{Key.Room} - {Name}";

    // Legacy sublabel for older bindings.
    public string DisplayLocation => StepsFromCurrent switch
    {
        null => string.Empty,
        0    => "here",
        1    => "1 step",
        _    => $"{StepsFromCurrent} steps",
    };

    // Top line in the dropdown row. Monster tag when present, otherwise the room reference.
    public string PrimaryLine => MonsterTag ?? $"{Key.Map}/{Key.Room} - {Name}";

    // True when this row carries no walkable destination — used for monster
    // matches whose lair isn't recorded in game data (unique bosses,
    // wandering spawns). The click handler skips these so they behave as
    // informational labels in the dropdown.
    public bool IsInformational => Key.Map <= 0 || Key.Room <= 0;

    // Bottom line: when this is a monster match, the underlying room; otherwise the step distance.
    public string SecondaryLine => MonsterTag is null
        ? DisplayLocation
        : (IsInformational
            ? "(no known lair location)"
            : StepsFromCurrent switch
            {
                null => $"{Key.Map}/{Key.Room} - {Name}",
                0    => $"{Key.Map}/{Key.Room} - {Name} · here",
                1    => $"{Key.Map}/{Key.Room} - {Name} · 1 step",
                _    => $"{Key.Map}/{Key.Room} - {Name} · {StepsFromCurrent} steps",
            });
}
