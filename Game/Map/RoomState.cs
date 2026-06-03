using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Live "where am I?" state for the connected character — the room
/// graph node we currently believe the player is standing on, plus the
/// confidence we have in that belief. Consumed by the Navigation
/// window's status strip, the walker (source-room argument), the loop
/// runner, and the auto-lair scheduler.
/// </summary>
/// <remarks>
/// Single-writer invariant: every observable field is owned by
/// <see cref="RoomTracker"/>. Consumers bind / subscribe; they never
/// call the setters. The Phase 3 IL-scan invariant test enforces this
/// at build time.
/// </remarks>
public sealed partial class RoomState : ObservableObject
{
    [ObservableProperty] [field: Owner(typeof(RoomTracker))] private Room? _currentRoom;
    [ObservableProperty] [field: Owner(typeof(RoomTracker))] private RoomConfidence _confidence;
    [ObservableProperty] [field: Owner(typeof(RoomTracker))] private DateTimeOffset _lastUpdatedAt;

    /// <summary>
    /// Running count of consecutive mismatching observations while in
    /// <see cref="RoomConfidence.Suspect"/>. Resets to zero on every
    /// transition to <see cref="RoomConfidence.Confirmed"/>. At the
    /// configured limit the tracker tries replay-from-last-Confirmed
    /// recovery; failure drops us to <see cref="RoomConfidence.Lost"/>.
    /// Surfaced for debug bindings; the user-facing badge stays green
    /// until Lost.
    /// </summary>
    [ObservableProperty] [field: Owner(typeof(RoomTracker))] private int _suspectStrikes;
}
