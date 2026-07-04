namespace FujinTerm.Models.Profile;

// JSON-friendly entry for one marked lair room inside a LairSetup. Carries the
// Game.Map.RoomKey wire pair plus an optional override respawn timer that beats
// the game-data default (Lairs[GroupIndex].AvgDelay for stock realms).
//
// Plain mutable POCO so System.Text.Json round-trips it without a converter.
// Order in the parent LairSetup is irrelevant — the scheduler picks targets
// live based on respawn timers + travel cost, not on file order.
public sealed class LairMarker
{
    public int Map { get; set; }
    public int Room { get; set; }

    // User-set respawn timer in whole seconds. null when the scheduler should
    // fall back to the game-data default.
    public int? OverrideRespawnSeconds { get; set; }

    // When true, the scheduler treats this marker as paused without removing it
    // from the setup — useful for "skip this lair for the rest of the session"
    // without losing the bookmark.
    public bool Skip { get; set; }

    public LairMarker() { }

    public LairMarker(int map, int room, int? overrideRespawnSeconds = null, bool skip = false)
    {
        Map = map;
        Room = room;
        OverrideRespawnSeconds = overrideRespawnSeconds;
        Skip = skip;
    }
}
