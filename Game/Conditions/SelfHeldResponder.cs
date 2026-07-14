using System.ComponentModel;
using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game.Conditions;

// Handles the LOCAL side of our own held / knocked-down state — the sibling of
// SelfConfusionResponder for the MovementPrevented flag.
//
// A knockdown ("You are knocked off your feet…" → "You are flat on your back!")
// blocks movement at the server: every move sent while down is refused with
// "You are flat on your back!" and does nothing. A follower down in this state
// is already held by FollowerGate, but a held LEADER (or solo player) has no one
// to signal — their .@held say-pause is eaten just like a confused leader's
// @wait — so their loop kept walking, hammering the server with refused moves
// and stranding the RoomTracker on the unresolved step (the report behind this).
//
// So on our own held state this:
//   1. sets our own party-window Held chip (SetMemberAilment on the self row) —
//      the say-driven chip mirror only fires for OTHER members' announcements, so
//      a held leader never lit their own chip, and
//   2. asserts MovementCoordinator.HeldGate so our own walk / loop / auto-lair
//      holds until "You get back on your feet." clears the condition.
//
// Both undo on clear. There's no opt-out — a knockdown always holds movement
// (unlike confusion's Ignore Confusion), so this has no Reevaluate.
public sealed class SelfHeldResponder : IDisposable
{
    public const string AsserterName = "SelfHeldMovementGate";
    private const string LogCategory = "Ailment";

    private readonly ConditionTracker _conditions;
    private readonly PartyManager _party;
    private readonly MovementCoordinator _coordinator;
    private readonly LogService? _log;

    private bool _wasHeld;
    private bool _gateAsserted;
    private bool _disposed;

    public SelfHeldResponder(
        ConditionTracker conditions,
        PartyManager party,
        MovementCoordinator coordinator,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(coordinator);
        _conditions = conditions;
        _party = party;
        _coordinator = coordinator;
        _log = log;

        _conditions.PropertyChanged += OnConditionsChanged;
    }

    private void OnConditionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConditionTracker.ActiveFlags))
            Evaluate();
    }

    // Recompute the held state and fire the transition once per edge. Public so
    // tests can drive it without a PropertyChanged round-trip.
    public void Evaluate()
    {
        bool held = _conditions.IsMovementPrevented;
        if (held == _wasHeld) return;
        _wasHeld = held;

        if (held)
        {
            SetSelfChip(true);
            AssertGate();
            _log?.Info(LogCategory, "Held — self chip set, navigation held.");
        }
        else
        {
            SetSelfChip(false);
            ClearGate();
            _log?.Info(LogCategory, "Held cleared — self chip cleared, navigation resumes.");
        }
    }

    private void SetSelfChip(bool active)
    {
        if (string.IsNullOrEmpty(_party.LocalCharacterName)) return;
        _party.SetMemberAilment(_party.LocalCharacterName, MessageFlags.MovementPrevented, active);
    }

    private void AssertGate()
    {
        if (_gateAsserted) return;
        _gateAsserted = true;
        _coordinator.AssertGate(MovementCoordinator.HeldGate, AsserterName, "self held");
    }

    private void ClearGate()
    {
        if (!_gateAsserted) return;
        _gateAsserted = false;
        _coordinator.ClearGate(MovementCoordinator.HeldGate, AsserterName, "held cleared");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conditions.PropertyChanged -= OnConditionsChanged;
        // Don't leave a stuck gate if torn down mid-held (character swap).
        ClearGate();
    }
}
