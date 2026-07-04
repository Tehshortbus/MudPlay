using System.Linq;
using System.Text.Json.Serialization;

namespace FujinTerm.Models.Profile;

// One record of a character's death — captured by the death-message detector
// when the post-suicide / killed-in-combat "You now have N lives remaining."
// line arrives. Persisted on CharacterProfile.DeathHistory and consumed by the
// Workshop DEATH section.
//
// Room records the room the character was standing in when the death message
// fired — i.e. where they died, not where the server respawned them. The
// respawn room is recorded normally by the room tracker once the next
// observation lands.
//
// LivesRemaining is captured from the same line that triggered the detection so
// the Workshop history shows the declining count. MessageText is the verbatim
// line so custom realm phrasings round-trip without lossy normalization.
//
// EquippedAtDeath and LostItems capture the deathpile contents at the moment of
// death from the inventory tracker's last-known snapshot — the worn half
// (re-equippable on recovery) and the carried-but-unworn half respectively.
// Either may be null on records written before inventory tracking landed, or
// when no i dump had been observed yet.
public sealed class DeathRecord
{
    // 1-based sequence number within the character's death history, shown in the
    // DEATH grid's # column. Assigned when the record is appended (= prior count
    // + 1) so it stays stable even if older records are cleared.
    public int RecordNumber { get; set; }

    public DateTimeOffset At { get; set; }
    public RoomRef? Room { get; set; }

    // Display name of the room the character died in, captured at death time so
    // the grid can show it without a graph lookup (the room may belong to a
    // game-data set that isn't loaded later). null when the room was unknown at
    // death.
    public string? RoomName { get; set; }

    public int LivesRemaining { get; set; }
    public string? MessageText { get; set; }

    // Recovery state — drives the stoplight tint in the grid.
    public DeathRecoveryStatus Status { get; set; } = DeathRecoveryStatus.Active;

    // Free-text note about the recovery outcome (e.g. "marked recovered by
    // user"), surfaced as the grid row's tooltip. null until a recovery action
    // touches the record.
    public string? RecoveryMessage { get; set; }

    // Items worn at the moment of death, captured from the inventory tracker's
    // last-known snapshot. Each carries its slot so auto-equip can pick the
    // right re-equip verb (hold vs wear). null when inventory tracking hadn't
    // observed an i dump (or on pre-tracking records).
    public List<DeathItem>? EquippedAtDeath { get; set; }

    // Carried-but-unworn items lost into the deathpile, captured from the
    // inventory tracker's last-known snapshot. null under the same conditions as
    // EquippedAtDeath.
    public List<DeathItem>? LostItems { get; set; }

    // "{Map}/{Room}" for the DEATH grid's map/room column, or "—" when the death
    // room was unknown. Display-only — not persisted.
    [JsonIgnore]
    public string RoomKeyText => Room is null ? "—" : $"{Room.Map}/{Room.Room}";

    // Local-time "yyyy-MM-dd HH:mm:ss" for the DEATH grid's "Died" column — date
    // AND time, shown in the player's local zone (records persist At as UTC).
    // Display-only — not persisted.
    [JsonIgnore]
    public string DiedText => At == default ? "—" : At.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    // Newline-joined worn-item names for the DEATH detail panel's "Equipped at
    // death" column, or a placeholder when none were recorded. Display-only —
    // not persisted.
    [JsonIgnore]
    public string EquippedAtDeathText => DescribeItems(EquippedAtDeath);

    // Newline-joined carried-item names for the DEATH detail panel's "Inventory
    // lost" column, or a placeholder when none were recorded. Display-only — not
    // persisted.
    [JsonIgnore]
    public string LostItemsText => DescribeItems(LostItems);

    private static string DescribeItems(List<DeathItem>? items) =>
        items is { Count: > 0 }
            ? string.Join("\n", items.Select(i => i.Name))
            : "None recorded.";

    public DeathRecord() { }

    public DeathRecord(DateTimeOffset at, RoomRef? room, int livesRemaining, string? messageText)
    {
        At = at;
        Room = room;
        LivesRemaining = livesRemaining;
        MessageText = messageText;
    }
}
