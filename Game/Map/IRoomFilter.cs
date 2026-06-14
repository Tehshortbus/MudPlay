namespace FujinTerm.Game.Map;

/// <summary>
/// Pathing-time room filter — when supplied to
/// <see cref="BfsMapper.FindPath"/>, any room with
/// <see cref="IsAvoided"/>=true is treated as a non-traversable node
/// (cannot be on a path; cannot be a path's intermediate hop). The
/// source and destination are evaluated by the caller so a user can
/// still <c>SetLocated</c> on an avoided room without breaking the
/// walker.
/// </summary>
/// <remarks>
/// PR 7.6 ships the per-character <c>AvoidedRoomFilter</c> backed by
/// the profile's avoided-rooms list. PR 7.5 only depends on the
/// interface so the BFS engine can be tested without wiring the
/// profile layer.
/// </remarks>
public interface IRoomFilter
{
    bool IsAvoided(RoomKey key);

    /// <summary>
    /// True when the exit's movement restriction (e.g. a Form-A
    /// <c>(Level: MIN to MAX)</c> gate) excludes the player, making the
    /// exit non-traversable for path planning. Default implementation
    /// never blocks — only filters that know the player's level
    /// (<see cref="Services.MovementFilter"/>) override it. BFS skips a
    /// blocked exit the same way it skips an avoided room; the walker
    /// can re-probe with gates ignored to tell "all routes level-gated"
    /// apart from "graph-disconnected".
    /// </summary>
    bool IsExitBlocked(in RoomExit exit) => false;
}
