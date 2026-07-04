using FujinTerm.Game.Map;
using FujinTerm.Game.Remote;
using FujinTerm.Services;

namespace FujinTerm.Game;

// Bridges inbound @wait / .@held signals from other party members onto the
// MovementCoordinator.PartyWaitGate. PartyEssentialHandlers records each member
// asking us to hold (its WaitingMembers set) and raises PauseGateChanged on the
// 0↔1 transition; while at least one member is waiting this asserts the gate so
// the active movement engine (loop / Auto-Lair / walk-to) holds until every
// member sends @ok. Without this the WaitingMembers set only drove the
// PartyWindow's per-row WAIT chip — the @wait never actually paused our own
// automation, so a loop kept walking away from a resting party member.
//
// The leader-side opt-out (PartySettings.IgnoreWaitWhenLeading) is honoured
// upstream in PartyEssentialHandlers.NotePause, which drops a follower's @wait
// before it reaches WaitingMembers when we're leading and the user opted out —
// so IsPaused already reflects that choice and this bridge needs no settings
// read of its own.
//
// Read-only on party state (mirrors PartyFollowerMovementGate / PartyVitalsWatcher):
// it only subscribes to PartyEssentialHandlers.PauseGateChanged and never writes
// party state. Because it rides the shared MovementCoordinator it composes with
// every other pause source — clearing this gate leaves movement held if anything
// else (combat, health, follower, user pause) is still asserting.
public sealed class PartyWaitMovementGate : IDisposable
{
    // Identifier surfaced in MovementCoordinator.History when the gate flips.
    public const string AsserterName = "PartyWaitMovementGate";

    private readonly PartyEssentialHandlers _essentials;
    private readonly MovementCoordinator _coordinator;
    private readonly LogService? _log;
    private bool _gateAsserted;

    public PartyWaitMovementGate(
        PartyEssentialHandlers essentials,
        MovementCoordinator coordinator,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(essentials);
        ArgumentNullException.ThrowIfNull(coordinator);
        _essentials = essentials;
        _coordinator = coordinator;
        _log = log;

        _essentials.PauseGateChanged += OnPauseGateChanged;
        Evaluate();
    }

    private void OnPauseGateChanged(bool _) => Evaluate();

    // Assert / clear PartyWaitGate from the inbound-@wait set. Idempotent — only
    // touches the coordinator when the held state actually flips.
    public void Evaluate()
    {
        bool shouldHold = _essentials.IsPaused;
        if (shouldHold == _gateAsserted) return;
        _gateAsserted = shouldHold;

        if (shouldHold)
        {
            string who = string.Join(", ", _essentials.WaitingMembers);
            string reason = who.Length > 0 ? $"members={who}" : "party @wait";
            _coordinator.AssertGate(MovementCoordinator.PartyWaitGate, AsserterName, reason);
            _log?.Info("Party", "Holding — a party member asked us to @wait.");
        }
        else
        {
            _coordinator.ClearGate(MovementCoordinator.PartyWaitGate, AsserterName, "all @ok");
            _log?.Info("Party", "Party @wait cleared — resuming.");
        }
    }

    public void Dispose()
    {
        _essentials.PauseGateChanged -= OnPauseGateChanged;
    }
}
