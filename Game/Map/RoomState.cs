using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// Live "where am I?" state for the connected character — the room graph node we
// currently believe the player is standing on, plus the confidence we have in
// that belief. Consumed by the Navigation window's status strip, the walker
// (source-room argument), the loop runner, and the auto-lair scheduler.
//
// Single-writer invariant: every observable field is owned by RoomTracker.
// Consumers bind / subscribe; they never call the setters. The IL-scan
// invariant test enforces this at build time.
public sealed partial class RoomState : ObservableObject
{
    [ObservableProperty] [field: Owner(typeof(RoomTracker))] private Room? _currentRoom;
    [ObservableProperty] [field: Owner(typeof(RoomTracker))] private RoomConfidence _confidence;
    [ObservableProperty] [field: Owner(typeof(RoomTracker))] private DateTimeOffset _lastUpdatedAt;

    // Running count of consecutive mismatching observations while in
    // RoomConfidence.Suspect. Resets to zero on every transition to
    // RoomConfidence.Confirmed. At the configured limit the tracker tries
    // replay-from-last-Confirmed recovery; failure drops us to
    // RoomConfidence.Lost. Surfaced for debug bindings; the user-facing badge
    // stays green until Lost.
    [ObservableProperty] [field: Owner(typeof(RoomTracker))] private int _suspectStrikes;

    // Directions from the latest observation whose modifier was "open door". The
    // walker checks this before kicking off the door FSM — if the door is
    // already open, send the cardinal move directly and skip the bash/pick wait.
    // null when the last observation had no door modifiers (or when the tracker
    // is in RoomConfidence.Unknown / between rooms).
    [ObservableProperty] [field: Owner(typeof(RoomTracker))]
    private System.Collections.Generic.IReadOnlySet<Direction>? _openDoorDirections;

    // Every direction on the latest observation's "Obvious exits:" line. The
    // walker checks this before searching a graph-hidden exit — if the direction
    // is already showing (a prior `sea` uncovered it, or it simply isn't hidden
    // in this room instance), skip the redundant `sea <dir>` and send the
    // cardinal move directly. null before the first observation.
    [ObservableProperty] [field: Owner(typeof(RoomTracker))]
    private System.Collections.Generic.IReadOnlySet<Direction>? _observedExitDirections;
}
