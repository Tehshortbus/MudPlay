namespace FujinTerm.Models.Profile;

/// <summary>
/// JSON-friendly entry for one marked lair room inside a
/// <see cref="LairSetup"/>. Carries the <see cref="Game.Map.RoomKey"/>
/// wire pair plus an optional override respawn timer that beats the
/// game-data default (<c>Lairs[GroupIndex].AvgDelay</c> for stock
/// realms; <see cref="Game.Map.MpFile"/>-free).
/// </summary>
/// <remarks>
/// Plain mutable POCO so <see cref="System.Text.Json"/> round-trips it
/// without a converter. Order in the parent <see cref="LairSetup"/> is
/// irrelevant — the scheduler picks targets live based on respawn
/// timers + travel cost, not on file order.
/// </remarks>
public sealed class LairMarker
{
    public int Map { get; set; }
    public int Room { get; set; }

    /// <summary>
    /// User-set respawn timer in whole seconds. <c>null</c> when the
    /// scheduler should fall back to the game-data default.
    /// </summary>
    public int? OverrideRespawnSeconds { get; set; }

    /// <summary>
    /// When true, the scheduler treats this marker as paused without
    /// removing it from the setup — useful for "skip this lair for the
    /// rest of the session" without losing the bookmark.
    /// </summary>
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
