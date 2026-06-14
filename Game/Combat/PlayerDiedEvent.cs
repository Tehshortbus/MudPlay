namespace FujinTerm.Game.Combat;

/// <summary>
/// Payload of <see cref="DeathLineWatcher.PlayerDied"/>. Carries the
/// killer's name as observed on the wire + the timestamp of the line
/// — DeathRecoveryManager (PR 9.I) uses both to populate its
/// per-character death record (location is read separately from
/// <see cref="Game.Map.RoomTracker.State"/>).
/// </summary>
/// <param name="Killer">Whatever the "slain by &lt;name&gt;." line
/// captured. Usually a monster's base name; possibly another player
/// in PvP-enabled realms (FujinTerm engines stay PvE but we still
/// observe and record the line so the death history is complete).</param>
/// <param name="At">Wall-clock time the death line was observed.</param>
public readonly record struct PlayerDiedEvent(string Killer, DateTimeOffset At);
