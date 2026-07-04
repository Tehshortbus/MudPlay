using System;
using FujinTerm.Models.Profile;

namespace FujinTerm.Models.GameData;

// One user-defined scheduled / lifecycle event. Per-character; persisted
// on CharacterProfile.Events and consumed at runtime by EventManager.
//
// Flat shape: exactly one trigger-side field (AtTime, EveryAmount /
// EveryUnit) and exactly one action-side field (WalkToTarget, LoopName,
// AutoLairSetupName, or CommandText) is populated depending on TriggerType
// + ActionType. Other fields stay null so JSON round-tripping doesn't carry
// stale params from a previous trigger / action shape.
//
// JSON-friendly mutable POCO (settable properties) so System.Text.Json
// serialises without a custom converter and so the edit dialog can two-way
// bind to fields.
public sealed class ScheduledEvent
{
    // Readable label for the list view + log lines. May be empty.
    public string Name { get; set; } = string.Empty;

    // Event row exists but won't fire. Set manually OR by the saved-target
    // reconciler when a referenced loop / auto-lair name is no longer in
    // its manager's collection.
    public bool Disabled { get; set; }

    // Which lifecycle / schedule trigger fires this event.
    public EventTriggerType TriggerType { get; set; }

    // Wall-clock local fire time as HH:mm (24-hour) when TriggerType is
    // AtTime; null otherwise.
    public string? AtTime { get; set; }

    // Cadence amount paired with EveryUnit when TriggerType is Every; null
    // otherwise.
    public int? EveryAmount { get; set; }

    // Cadence unit paired with EveryAmount. Null when TriggerType is not
    // Every.
    public EventTimeUnit? EveryUnit { get; set; }

    // Which action the event runs when fired.
    public EventActionType ActionType { get; set; }

    // Coord target for EventActionType.WalkTo. Stored as a coord (not a
    // name) so renames in the active game-data set don't invalidate the
    // reference. Null for other action types.
    public RoomRef? WalkToTarget { get; set; }

    // Saved loop name (case-insensitive lookup against LoopManager.Loops)
    // for EventActionType.Loop. Null for other action types.
    public string? LoopName { get; set; }

    // Saved auto-lair setup name (case-insensitive lookup against
    // LairManager.Setups) for EventActionType.AutoLair. Null for other
    // action types.
    public string? AutoLairSetupName { get; set; }

    // Free-form command text for EventActionType.Command. Supports
    // multi-fire via ^M and ; separators — each chunk is split out and sent
    // as its own CR-terminated line. Null for other action types.
    public string? CommandText { get; set; }

    // Tries to parse AtTime as 24-hour HH:mm into a TimeOnly. Returns null
    // when the field is blank or malformed. Centralised here so the editor
    // + engine agree on the format.
    public TimeOnly? TryParseAtTime() =>
        !string.IsNullOrWhiteSpace(AtTime)
        && TimeOnly.TryParseExact(AtTime, "HH:mm", out TimeOnly parsed)
            ? parsed
            : null;

    // Materialise the EveryAmount + EveryUnit pair as a TimeSpan. Returns
    // null when either field is unset or amount <= 0. Clamps amount to a
    // minimum of 1 to avoid degenerate zero-tick timers.
    public TimeSpan? TryParseEvery()
    {
        if (EveryAmount is not { } amount || amount <= 0) return null;
        if (EveryUnit is not { } unit) return null;
        return unit switch
        {
            EventTimeUnit.Seconds => TimeSpan.FromSeconds(amount),
            EventTimeUnit.Minutes => TimeSpan.FromMinutes(amount),
            EventTimeUnit.Hours   => TimeSpan.FromHours(amount),
            _ => null,
        };
    }
}
