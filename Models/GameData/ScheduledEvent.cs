using System;
using FujinTerm.Models.Profile;

namespace FujinTerm.Models.GameData;

/// <summary>
/// One user-defined scheduled / lifecycle event. Per-character;
/// persisted on <see cref="CharacterProfile.Events"/> and consumed at
/// runtime by <c>EventManager</c>.
/// </summary>
/// <remarks>
/// <para>
/// Flat shape: exactly one trigger-side field (<see cref="AtTime"/>,
/// <see cref="EveryAmount"/> / <see cref="EveryUnit"/>) and exactly
/// one action-side field (<see cref="WalkToTarget"/>,
/// <see cref="LoopName"/>, <see cref="AutoLairSetupName"/>, or
/// <see cref="CommandText"/>) is populated depending on
/// <see cref="TriggerType"/> + <see cref="ActionType"/>. Other fields
/// stay null so JSON round-tripping doesn't carry stale params from a
/// previous trigger / action shape.
/// </para>
/// <para>
/// JSON-friendly mutable POCO (settable properties) so
/// <see cref="System.Text.Json"/> serialises without a custom
/// converter and so the edit dialog can two-way bind to fields.
/// </para>
/// </remarks>
public sealed class ScheduledEvent
{
    /// <summary>Readable label for the list view + log lines. May be empty.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Event row exists but won't fire. Set manually OR by the
    /// saved-target reconciler when a referenced loop / auto-lair name
    /// is no longer in its manager's collection.
    /// </summary>
    public bool Disabled { get; set; }

    /// <summary>Which lifecycle / schedule trigger fires this event.</summary>
    public EventTriggerType TriggerType { get; set; }

    /// <summary>
    /// Wall-clock local fire time as <c>HH:mm</c> (24-hour) when
    /// <see cref="TriggerType"/> is
    /// <see cref="EventTriggerType.AtTime"/>; null otherwise.
    /// </summary>
    public string? AtTime { get; set; }

    /// <summary>
    /// Cadence amount paired with <see cref="EveryUnit"/> when
    /// <see cref="TriggerType"/> is
    /// <see cref="EventTriggerType.Every"/>; null otherwise.
    /// </summary>
    public int? EveryAmount { get; set; }

    /// <summary>
    /// Cadence unit paired with <see cref="EveryAmount"/>. Null when
    /// <see cref="TriggerType"/> is not <see cref="EventTriggerType.Every"/>.
    /// </summary>
    public EventTimeUnit? EveryUnit { get; set; }

    /// <summary>Which action the event runs when fired.</summary>
    public EventActionType ActionType { get; set; }

    /// <summary>
    /// Coord target for <see cref="EventActionType.WalkTo"/>. Stored
    /// as a coord (not a name) so renames in the active game-data set
    /// don't invalidate the reference. Null for other action types.
    /// </summary>
    public RoomRef? WalkToTarget { get; set; }

    /// <summary>
    /// Saved loop name (case-insensitive lookup against
    /// <c>LoopManager.Loops</c>) for
    /// <see cref="EventActionType.Loop"/>. Null for other action
    /// types.
    /// </summary>
    public string? LoopName { get; set; }

    /// <summary>
    /// Saved auto-lair setup name (case-insensitive lookup against
    /// <c>LairManager.Setups</c>) for
    /// <see cref="EventActionType.AutoLair"/>. Null for other action
    /// types.
    /// </summary>
    public string? AutoLairSetupName { get; set; }

    /// <summary>
    /// Free-form command text for <see cref="EventActionType.Command"/>.
    /// Supports multi-fire via <c>^M</c> and <c>;</c> separators —
    /// each chunk is split out and sent as its own CR-terminated line.
    /// Null for other action types.
    /// </summary>
    public string? CommandText { get; set; }

    /// <summary>
    /// Tries to parse <see cref="AtTime"/> as 24-hour <c>HH:mm</c>
    /// into a <see cref="TimeOnly"/>. Returns null when the field is
    /// blank or malformed. Centralised here so the editor + engine
    /// agree on the format.
    /// </summary>
    public TimeOnly? TryParseAtTime() =>
        !string.IsNullOrWhiteSpace(AtTime)
        && TimeOnly.TryParseExact(AtTime, "HH:mm", out TimeOnly parsed)
            ? parsed
            : null;

    /// <summary>
    /// Materialise the <see cref="EveryAmount"/> + <see cref="EveryUnit"/>
    /// pair as a <see cref="TimeSpan"/>. Returns null when either
    /// field is unset or amount &lt;= 0. Clamps amount to a minimum of
    /// 1 to avoid degenerate zero-tick timers.
    /// </summary>
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
