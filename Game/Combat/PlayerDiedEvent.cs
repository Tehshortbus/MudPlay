namespace FujinTerm.Game.Combat;

// Payload of DeathLineWatcher.PlayerDied. Carries the killer's name as observed
// on the wire + the timestamp of the line — DeathRecoveryManager uses both to
// populate its per-character death record (location is read separately from
// RoomTracker.State).
//
// Killer is whatever the "slain by <name>." line captured. Usually a monster's
// base name; possibly another player in PvP-enabled realms (the auto-engines
// stay PvE but we still observe and record the line so the death history is
// complete). At is the wall-clock time the death line was observed.
public readonly record struct PlayerDiedEvent(string Killer, DateTimeOffset At);
