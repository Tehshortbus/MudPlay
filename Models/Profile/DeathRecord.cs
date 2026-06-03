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
/// </remarks>
public sealed class DeathRecord
{
    public DateTimeOffset At { get; set; }
    public RoomRef? Room { get; set; }
    public int LivesRemaining { get; set; }
    public string? MessageText { get; set; }

    public DeathRecord() { }

    public DeathRecord(DateTimeOffset at, RoomRef? room, int livesRemaining, string? messageText)
    {
        At = at;
        Room = room;
        LivesRemaining = livesRemaining;
        MessageText = messageText;
    }
}
