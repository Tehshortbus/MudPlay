namespace FujinTerm.Models.Profile;

/// <summary>
/// JSON-friendly representation of a <see cref="Game.Map.RoomKey"/>
/// for per-character storage (avoided rooms, stash rooms, future
/// favorites bookmarks). Plain mutable POCO so
/// <see cref="System.Text.Json"/> round-trips it without a converter.
/// </summary>
public sealed class RoomRef
{
    public int Map { get; set; }
    public int Room { get; set; }

    public RoomRef() { }

    public RoomRef(int map, int room)
    {
        Map = map;
        Room = room;
    }
}
