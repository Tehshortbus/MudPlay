using System.ComponentModel;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Conditions;

// Holds our own walk / loop / auto-lair while we're afraid. Fear runs us at random
// through the room's obvious exits until it wears off, so a move of ours mostly ends
// up fighting it, and stepping out of a room with no obvious exits (the only way out
// hidden) is exactly what lets it start running us (GAME_MECHANICS "Fear"). Waiting
// it out and resuming afterwards is the better play; RoomTracker follows the forced
// moves meanwhile (TryFearMove) so the engine resumes from where we really are.
public sealed class SelfFearMovementGate : IDisposable
{
    public const string AsserterName = "SelfFearMovementGate";
    private const string LogCategory = "Ailment";

    private readonly ConditionTracker _conditions;
    private readonly MovementCoordinator _coordinator;
    private readonly LogService? _log;

    private bool _gateAsserted;
    private bool _disposed;

    public SelfFearMovementGate(ConditionTracker conditions, MovementCoordinator coordinator, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(coordinator);
        _conditions = conditions;
        _coordinator = coordinator;
        _log = log;
        _conditions.PropertyChanged += OnConditionsChanged;
    }

    private void OnConditionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConditionTracker.ActiveFlags))
            Evaluate();
    }

    // Assert on fear's onset, clear on its wear-off.
    public void Evaluate()
    {
        bool feared = _conditions.IsFeared;
        if (feared == _gateAsserted) return;
        _gateAsserted = feared;
        if (feared)
        {
            _coordinator.AssertGate(MovementCoordinator.FearGate, AsserterName, "self afraid");
            _log?.Info(LogCategory, "Afraid — navigation held until the fear wears off.");
        }
        else
        {
            _coordinator.ClearGate(MovementCoordinator.FearGate, AsserterName, "fear wore off");
            _log?.Info(LogCategory, "Fear wore off — navigation resumes.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conditions.PropertyChanged -= OnConditionsChanged;
        if (_gateAsserted)
        {
            _gateAsserted = false;
            _coordinator.ClearGate(MovementCoordinator.FearGate, AsserterName, "disposed");
        }
    }
}
