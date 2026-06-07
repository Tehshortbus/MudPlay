namespace FujinTerm.Models.GameData;

/// <summary>
/// Cadence unit for an <see cref="EventTriggerType.Every"/>-style
/// trigger. Pairs with <see cref="ScheduledEvent.EveryAmount"/> to
/// produce the firing interval.
/// </summary>
public enum EventTimeUnit
{
    Seconds = 0,
    Minutes = 1,
    Hours = 2,
}
