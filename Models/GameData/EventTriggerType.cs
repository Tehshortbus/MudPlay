namespace FujinTerm.Models.GameData;

// When a ScheduledEvent fires.
//   Logon — fires on every successful game entry (every
//     LoginAutomator.LoggedIntoGame). Includes first-connect and every
//     subsequent reconnect.
//   Logoff — fires once during a clean shutdown, before the wire closes.
//     Dropped connections skip Logoff events.
//   Relog — fires alongside Logon, but ONLY when the game entry is a
//     reconnect (a prior in-session disconnect happened). Never fires on
//     the first game entry of a program run.
//   AtTime — fires at the configured HH:mm wall-clock local time. Only
//     fires while connected + in-game; no catch-up after a missed window.
//   Every — recurring cadence based on ScheduledEvent.EveryAmount and
//     ScheduledEvent.EveryUnit. Timer pauses on disconnect and restarts
//     from zero on reconnect (no anchor preservation).
public enum EventTriggerType
{
    Logon = 0,
    Logoff = 1,
    Relog = 2,
    AtTime = 3,
    Every = 4,
}
