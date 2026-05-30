namespace FujinTerm.Models.GameData;

/// <summary>
/// One named room shortcut. Stored in a virtual folder hierarchy via
/// <see cref="Path"/> (slash-separated, e.g.
/// <c>"Cities/Silvermere/Bank"</c>). Consumed by the Phase 7 Goto +
/// Loop dialogs as the left-rail sidebar entries.
/// </summary>
/// <param name="Name">Display name shown in the favorites tree leaf.</param>
/// <param name="Path">
/// Slash-separated folder path the favorite lives under — empty
/// string for "root". The path is purely organisational; the engine
/// targets the named room via <see cref="RoomId"/>.
/// </param>
/// <param name="RoomId">Target room id from the active game-data set's <c>Rooms</c> table.</param>
/// <param name="Notes">Optional free-text annotation (e.g. "shop opens 9 → 17").</param>
public sealed record Favorite(
    string Name,
    string Path,
    int RoomId,
    string? Notes = null);
