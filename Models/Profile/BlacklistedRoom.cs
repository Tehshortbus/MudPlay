namespace FujinTerm.Models.Profile;

// One entry in a BBS's room-blacklist side-file. Hides the targeted room from
// the navigation map render and the search box (typical for ganghouse /
// sysop-only rooms behind dead-end doors that clutter the layout). Storage:
// Data/BBS/{bbs}/room_blacklist.json.
//
// Name is captured at add-time from the room's Rooms.json entry
// (Game.Map.Room.DisplayName) so the Modify-Blacklist dialog can render a
// human-readable list without re-loading the rooms graph. The name is
// informational only — the (Map, Room) tuple is the lookup key.
public sealed class BlacklistedRoom
{
    public int Map { get; set; }
    public int Room { get; set; }
    public string Name { get; set; } = "???";

    public BlacklistedRoom() { }

    public BlacklistedRoom(int map, int room, string name)
    {
        Map = map;
        Room = room;
        Name = name;
    }
}
