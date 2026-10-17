using Avalonia.Threading;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Combat;

// Holds the movement loop for a brief settle window when a combat line arrives in
// a LIT room our view shows empty — the lit-room twin of DarkRoomMovementSettle.
//
// The race this closes: in a loop through aggro monsters, a room can render
// empty (no "Also here:") and confirm the loop's in-flight move a beat BEFORE a
// pursuing hostile's leap-in / first attack line is parsed into the room. If the
// loop advances on that confirm, it sends the next move while a live mob is
// swinging at us — we walk away from the fight (the "skipping past hostile
// targets" / "movement command sent while in combat" reports).
//
// CombatManager already detects the exact moment: a combat line with no
// engageable in view and no current target means something is hitting us that
// the room lost, so it sends a CR to force a short re-display and raises
// RoomAppearsEmptyDuringCombat. We assert CombatRedisplaySettleGate on that
// signal, which flips the engines to Paused before the pending move's confirm
// reaches the loop's synchronous next-step dispatch (the combat line lands while
// the move is still Pending — verified from capture). During the window one of
// two things happens on the CR re-display: the hostile surfaces and the Combat
// gate asserts to take over the hold (loop resumes into the fight once the room
// clears), or the room really is empty (a clean kill's trailing line) and we
// clear on that same observation so the loop steps on — a delay bounded by one
// CR round-trip, not the full window. A short timeout is the fallback for when no
// re-display ever comes.
//
// Dark rooms are handled by DarkRoomMovementSettle instead: CombatManager
// suppresses the CR in the dark, so RoomAppearsEmptyDuringCombat never fires
// there and the two settles never overlap. The gate lives in the engine-wait
// tier (like DarkRoomSettleGate) so it never flips the toolbar Start/Pause/Stop.
public sealed class CombatRedisplaySettle : IDisposable
{
    public const string LogCategory = "CombatRedisplaySettle";

    // The hold must outlast a leap-in reveal that arrives as a fresh room render.
    // The primary clear is the next observation (the CR response), so this is only
    // the fallback ceiling for a re-display that never lands — kept short so a
    // genuinely-empty room a stray combat line touched doesn't stall the loop.
    public static readonly TimeSpan DefaultSettleWindow = TimeSpan.FromMilliseconds(1500);

    private readonly CombatManager _combat;
    private readonly RoomEntityClassifier _classifier;
    private readonly MovementCoordinator _coordinator;
    private readonly LogService? _log;
    private readonly TimeSpan _window;

    // When set, the settle window is scheduled through this callback instead of a
    // real DispatcherTimer (which needs a live Avalonia dispatcher).
    private readonly Action<TimeSpan, Action>? _scheduleOverride;

    private DispatcherTimer? _timer;
    private bool _asserted;
    private bool _disposed;

    public CombatRedisplaySettle(
        CombatManager combat,
        RoomEntityClassifier classifier,
        MovementCoordinator coordinator,
        LogService? log = null,
        TimeSpan? window = null,
        Action<TimeSpan, Action>? scheduleOverride = null)
    {
        ArgumentNullException.ThrowIfNull(combat);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(coordinator);
        _combat = combat;
        _classifier = classifier;
        _coordinator = coordinator;
        _log = log;
        _window = window ?? DefaultSettleWindow;
        _scheduleOverride = scheduleOverride;
        _combat.RoomAppearsEmptyDuringCombat += OnRoomAppearsEmpty;
        _classifier.EntitiesObserved += OnEntitiesObserved;
    }

    private void OnRoomAppearsEmpty()
    {
        if (_disposed) return;

        if (!_asserted)
        {
            _asserted = true;
            _coordinator.AssertGate(
                MovementCoordinator.CombatRedisplaySettleGate, LogCategory,
                $"combat line in apparently-empty room — settling {_window.TotalMilliseconds:F0}ms for re-display");
        }

        RestartTimer();
    }

    // The CR re-display (or any fresh room render) landed. Clear the settle gate:
    // if the render revealed a hostile, CombatStateTracker asserted the Combat gate
    // on this same observation and takes over the hold; if the room is empty, the
    // loop steps on. Either way our brief hold is done — the delay for an empty
    // room is one CR round-trip, not the full window.
    private void OnEntitiesObserved(RoomEntitiesObservation _)
    {
        if (_disposed || !_asserted) return;
        ClearGate("re-display observed");
    }

    private void RestartTimer()
    {
        if (_scheduleOverride is not null)
        {
            _scheduleOverride(_window, OnSettleElapsed);
            return;
        }

        _timer ??= new DispatcherTimer();
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _timer.Tick += OnTimerTick;
        _timer.Interval = _window;
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer?.Stop();
        OnSettleElapsed();
    }

    private void OnSettleElapsed()
    {
        if (!_asserted) return;
        ClearGate("settle window elapsed — no re-display");
    }

    private void ClearGate(string reason)
    {
        _asserted = false;
        _timer?.Stop();
        _coordinator.ClearGate(
            MovementCoordinator.CombatRedisplaySettleGate, LogCategory, reason);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _combat.RoomAppearsEmptyDuringCombat -= OnRoomAppearsEmpty;
        _classifier.EntitiesObserved -= OnEntitiesObserved;
        if (_timer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnTimerTick;
            _timer = null;
        }
        if (_asserted)
        {
            _asserted = false;
            _coordinator.ClearGate(
                MovementCoordinator.CombatRedisplaySettleGate, LogCategory, "disposed");
        }
    }
}
