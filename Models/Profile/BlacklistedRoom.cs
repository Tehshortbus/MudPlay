using System.ComponentModel;
using System.Runtime.CompilerServices;

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
//
// CannotBeReached is a stronger, opt-in flag layered on top of blacklisting:
// some rooms live in the MDB but no normal player can ever stand in them (dev /
// orphan rooms with no walkable inbound edge). A plain blacklist only declutters
// the map, but such a room is still a valid (Name, ExitMask) candidate the
// RoomTracker can resolve the player's position INTO — which strands the nav
// system in a place the player can't be. When this flag is set the graph drops
// the room from position-candidate resolution entirely. It's separate from the
// blacklist bit because a normally-reachable room can be blacklisted purely to
// tidy the render while still being a legitimate position.
public sealed class BlacklistedRoom : INotifyPropertyChanged
{
    public int Map { get; set; }
    public int Room { get; set; }
    public string Name { get; set; } = "???";

    // Notifying so the Modify-Blacklist dialog's per-row checkbox reflects a
    // programmatic flip (the "Toggle can't reach" bulk button), not just direct
    // clicks. The (Map, Room, Name) fields are set once at construction and
    // never edited in-place, so they stay plain auto-properties.
    private bool _cannotBeReached;
    public bool CannotBeReached
    {
        get => _cannotBeReached;
        set
        {
            if (_cannotBeReached == value) return;
            _cannotBeReached = value;
            OnPropertyChanged();
        }
    }

    public BlacklistedRoom() { }

    public BlacklistedRoom(int map, int room, string name, bool cannotBeReached = false)
    {
        Map = map;
        Room = room;
        Name = name;
        _cannotBeReached = cannotBeReached;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
