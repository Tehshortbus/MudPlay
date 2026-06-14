namespace FujinTerm.Models.Profile;

/// <summary>
/// JSON-friendly favourite-room bookmark. Carries the
/// <see cref="Game.Map.RoomKey"/> as <see cref="Map"/> + <see cref="Room"/>
/// (matches <see cref="RoomRef"/>'s wire shape) plus an optional
/// user-typed <see cref="Label"/> for the GOTO pane / quick-jump UI.
/// When <see cref="Label"/> is null/empty, callers fall back to the
/// room's graph display name.
/// </summary>
public sealed class FavoriteRoom
{
    public int Map { get; set; }
    public int Room { get; set; }
    public string? Label { get; set; }

    /// <summary>
    /// Folder path this favourite lives under in the GOTO tree, using
    /// <c>/</c> as the separator (e.g. <c>"Cities/Silvermere"</c>).
    /// Null or empty = the tree root. This mirrors a filesystem folder
    /// layout while still being stored inside the character profile —
    /// the GOTO tree splits the path on <c>/</c> to build its nodes.
    /// </summary>
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
