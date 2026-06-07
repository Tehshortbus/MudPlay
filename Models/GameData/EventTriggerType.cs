namespace FujinTerm.Models.GameData;

/// <summary>
/// When a <see cref="ScheduledEvent"/> fires.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Logon</b> — fires on every successful game entry (every
///   <c>LoginAutomator.LoggedIntoGame</c>). Includes first-connect and
///   every subsequent reconnect.</item>
///   <item><b>Logoff</b> — fires once during a clean shutdown, before
///   the wire closes. Dropped connections skip Logoff events.</item>
///   <item><b>Relog</b> — fires alongside Logon, but ONLY when the
///   game entry is a reconnect (a prior in-session disconnect
///   happened). Never fires on the first game entry of a program
///   run.</item>
///   <item><b>AtTime</b> — fires at the configured HH:mm wall-clock
///   local time. Only fires while connected + in-game; no catch-up
///   after a missed window.</item>
///   <item><b>Every</b> — recurring cadence based on
///   <see cref="ScheduledEvent.EveryAmount"/> and
///   <see cref="ScheduledEvent.EveryUnit"/>. Timer pauses on
///   disconnect and restarts from zero on reconnect (no anchor
///   preservation).</item>
/// </list>
/// </remarks>
public enum EventTriggerType
{
    Logon = 0,
    Logoff = 1,
    Relog = 2,
    AtTime = 3,
    Every = 4,
}
