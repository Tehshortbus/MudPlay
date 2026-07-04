using System.ComponentModel;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.Game;

// Suppresses this client's own automated movement while it is a party
// follower. In MajorMUD movement is leader-driven — the leader walks and the
// game drags followers along — so a follower that also drove its own walk /
// loop / auto-lair would fight the leader's drag and desync the map. While
// PartyState.IsInParty is true and SelfIsLeader is false this asserts
// MovementCoordinator.FollowerGate, holding every movement engine silently; the
// gate clears the moment we lead the party or leave it, so a queued walk / loop
// / auto-lair resumes on its own.
//
// Read-only on party state (mirrors PartyVitalsWatcher): it only subscribes to
// PartyState property changes and never writes a party field, so PartyManager
// stays the sole writer.
//
// The suppression is unconditional — leader-driven movement is a hard game
// constraint, not a user preference — so there is no settings toggle behind it.
// Because it rides the shared MovementCoordinator it composes with every other
// pause source: leaving follower state clears only this gate, and movement
// stays held if anything else (combat, health, user pause) is still asserting.
public sealed class PartyFollowerMovementGate : IDisposable
{
    // Identifier surfaced in MovementCoordinator.History when the gate flips the
    // Follower gate.
    public const string AsserterName = "PartyFollowerMovementGate";

    private readonly PartyState _party;
    private readonly MovementCoordinator _coordinator;
    private readonly LogService? _log;
    private bool _gateAsserted;

    public PartyFollowerMovementGate(
        PartyState party,
        MovementCoordinator coordinator,
        LogService? log = null)
    {
        _party = party;
        _coordinator = coordinator;
        _log = log;

        _party.PropertyChanged += OnPartyPropertyChanged;
        Evaluate();
    }

    private void OnPartyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PartyState.IsInParty) or nameof(PartyState.SelfIsLeader))
            Evaluate();
    }

    // Assert / clear the Follower gate from the current party role. Idempotent —
    // only touches the coordinator when the held state flips.
    public void Evaluate()
    {
        bool shouldHold = _party.IsInParty && !_party.SelfIsLeader;
        if (shouldHold == _gateAsserted) return;
        _gateAsserted = shouldHold;

        if (shouldHold)
        {
            string reason = _party.LeaderName is { Length: > 0 } leader
                ? $"following {leader}"
                : "following";
            _coordinator.AssertGate(MovementCoordinator.FollowerGate, AsserterName, reason);
            _log?.Info("Party", "Following — leader drives movement; holding own auto-walk.");
        }
        else
        {
            _coordinator.ClearGate(MovementCoordinator.FollowerGate, AsserterName, "leading or solo");
            _log?.Info("Party", "No longer a follower — own movement resumes.");
        }
    }

    public void Dispose()
    {
        _party.PropertyChanged -= OnPartyPropertyChanged;
    }
}
