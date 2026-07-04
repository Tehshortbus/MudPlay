namespace FujinTerm.Models.Profile;

// JSON-friendly favourite-room bookmark. Carries the Game.Map.RoomKey as Map +
// Room (matches RoomRef's wire shape) plus an optional user-typed Label for the
// GOTO pane / quick-jump UI. When Label is null/empty, callers fall back to the
// room's graph display name.
public sealed class FavoriteRoom
{
    public int Map { get; set; }
    public int Room { get; set; }
    public string? Label { get; set; }

    // Folder path this favourite lives under in the GOTO tree, using / as the
    // separator (e.g. "Cities/Silvermere"). Null or empty = the tree root. This
    // mirrors a filesystem folder layout while still being stored inside the
    // character profile — the GOTO tree splits the path on / to build its nodes.
    public string? Folder { get; set; }

    public FavoriteRoom() { }

    public FavoriteRoom(int map, int room, string? label = null, string? folder = null)
    {
        Map = map;
        Room = room;
        Label = label;
        Folder = folder;
    }
}
