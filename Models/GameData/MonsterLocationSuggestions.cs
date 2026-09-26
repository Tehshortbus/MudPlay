namespace MudPlay.Models.GameData;

// The distinct Landmass / Region / Area labels already in use for a realm's monsters —
// the typeahead lists behind the monster record's three location boxes.
public sealed record MonsterLocationSuggestions(
    IReadOnlyList<string> Landmasses,
    IReadOnlyList<string> Regions,
    IReadOnlyList<string> Areas)
{
    public static MonsterLocationSuggestions Empty { get; } =
        new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
}
