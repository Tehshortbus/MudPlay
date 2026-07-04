namespace FujinTerm.Models.GameData;

// Cadence unit for an EventTriggerType.Every-style trigger. Pairs with
// ScheduledEvent.EveryAmount to produce the firing interval.
public enum EventTimeUnit
{
    Seconds = 0,
    Minutes = 1,
    Hours = 2,
}
