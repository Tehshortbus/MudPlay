using System.Text.Json.Serialization;

namespace FujinTerm.Models.Profile;

/// <summary>
/// One record of a character's death — captured by the death-message
/// detector when the post-suicide / killed-in-combat <c>You now have N
/// lives remaining.</c> line arrives. Persisted on
/// <see cref="CharacterProfile.DeathHistory"/> and consumed by the
/// Phase 9 Workshop DEATH section.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Room"/> records the room the character was standing in
/// when the death message fired — i.e. where they died, not where the
/// server respawned them. The respawn room is recorded normally by the
/// room tracker once the next observation lands.
/// </para>
/// <para>
/// <see cref="LivesRemaining"/> is captured from the same line that
/// triggered the detection so the Workshop history shows the
/// declining count. <see cref="MessageText"/> is the verbatim line so
/// custom realm phrasings round-trip without lossy normalization.
/// </para>
/// <para>
/// Capturing the <i>items lost</i> at death (and what was equipped at
/// the time, for re-equip on recovery) is deferred until the inventory
/// tracker exists — there is no <c>LostItems</c> field yet, so the
/// DEATH section renders an Items-Lost placeholder and the
/// <see cref="DeathRecoveryStatus.Partial"/> distinction can only be
/// reached manually for now.
/// </para>
/// </remarks>
public sealed class DeathRecord
{
    /// <summary>
    /// 1-based sequence number within the character's death history,
    /// shown in the DEATH grid's <c>#</c> column. Assigned when the
    /// record is appended (= prior count + 1) so it stays stable even
    /// if older records are cleared.
    /// </summary>
    public int RecordNumber { get; set; }

    public DateTimeOffset At { get; set; }
    public RoomRef? Room { get; set; }

    /// <summary>
    /// Display name of the room the character died in, captured at death
    /// time so the grid can show it without a graph lookup (the room may
    /// belong to a game-data set that isn't loaded later). <c>null</c>
    /// when the room was unknown at death.
    /// </summary>
    public string? RoomName { get; set; }

    public int LivesRemaining { get; set; }
    public string? MessageText { get; set; }

    /// <summary>Recovery state — drives the stoplight tint in the grid.</summary>
    public DeathRecoveryStatus Status { get; set; } = DeathRecoveryStatus.Active;

    /// <summary>
    /// Free-text note about the recovery outcome (e.g. "marked recovered
    /// by user"), surfaced as the grid row's tooltip. <c>null</c> until
    /// a recovery action touches the record.
    /// </summary>
    public string? RecoveryMessage { get; set; }

    /// <summary>
    /// <c>"{Map}/{Room}"</c> for the DEATH grid's map/room column, or
    /// <c>"—"</c> when the death room was unknown. Display-only — not
    /// persisted.
    /// </summary>
    [JsonIgnore]
    public string RoomKeyText => Room is null ? "—" : $"{Room.Map}/{Room.Room}";

    public DeathRecord() { }

    public DeathRecord(DateTimeOffset at, RoomRef? room, int livesRemaining, string? messageText)
    {
        At = at;
        Room = room;
        LivesRemaining = livesRemaining;
        MessageText = messageText;
    }
}
