using Avalonia.Threading;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.Game.Inventory;

// Shared driver for MovementCoordinator.AcquisitionGate. Both the item engine
// (AutoGetItemsManager) and the cash engine (CashManager) feed this one
// instance; it owns the single assert/clear of the gate so the two engines'
// states are AND-ed — the walker resumes only once both have finished looting.
//
// MovementCoordinator keys gates by name in a HashSet, so a gate is
// single-owner: if both engines asserted "Acquisition" directly, the first to
// clear would drop it while the other still wanted it held. This driver is the
// single owner; engines report activity and it ANDs.
//
// The gate is held while either condition is live:
//   - Pending deferred items: AutoGetItemsManager queued gets that wait for
//     the room's combat to finish. Held with no timeout until flushed or the
//     room changes. Asserting here — while the Combat gate is still up, on the
//     same EntitiesObserved pass that CombatStateTracker later uses to clear
//     the Combat gate — is what defeats the synchronous walker-resume race:
//     without it the walker would step out the instant Combat clears, before
//     the loot flush runs.
//   - Settle window: a short quiet period after the last get command. There is
//     no server-side item-pickup confirmation line to key on, so get-clear for
//     items is command-side: once gets stop flowing for SettleWindow,
//     collection is treated as finished and the walker may resume.
public sealed class AcquisitionGate : IDisposable
{
    // Asserter identity recorded in the [Gate] log + MovementCoordinator.History.
    public const string AsserterName = "Acquisition";

    // Quiet period after the last get before the gate releases. No server-side
    // item-pickup confirmation line exists, so get-clear is command-side: once
    // gets stop flowing for this long with no pending deferred items,
    // collection is treated as done. Kept short so a cleared room's loop resumes
    // promptly — consecutive corpse-drop gets arrive within the same server
    // frame (tens of ms), so this only has to outlast that burst, not a full
    // combat round.
    private static readonly TimeSpan SettleWindow = TimeSpan.FromMilliseconds(600);

    private readonly MovementCoordinator _coordinator;
    private readonly LogService? _log;
    private readonly DispatcherTimer _settle;

    private int _pendingDeferred;
    private bool _asserted;
    private bool _disposed;

    public AcquisitionGate(MovementCoordinator coordinator, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;
        _log = log;
        _settle = new DispatcherTimer { Interval = SettleWindow };
        _settle.Tick += OnSettleElapsed;
    }

    // Note that the item engine queued count gets to fire once the room's
    // combat finishes. Asserts the gate now — while the Combat gate is still
    // held — so the walker can't step out the instant combat clears, before the
    // deferred flush runs. A count of 0 is a no-op (nothing to wait for).
    public void NoteDeferredPending(int count)
    {
        _pendingDeferred = count;
        if (count > 0) Assert($"deferred-pending={count}");
    }

    // Note a get command was just dispatched (item or cash). Asserts the gate
    // and re-arms the settle window; the gate releases SettleWindow after the
    // last get with no further activity and no pending deferred items.
    public void NoteGetSent()
    {
        Assert("get-sent");
        _settle.Stop();
        _settle.Start();
    }

    // The deferred queue was flushed or discarded — no items wait on combat
    // anymore. If the flush armed the settle window (gets went out) the gate
    // stays held until that elapses; if the queue was dropped without any gets
    // (e.g. the user walked away mid-combat), the gate releases immediately.
    public void NoteDeferredCleared()
    {
        _pendingDeferred = 0;
        if (!_settle.IsEnabled) Release("deferred-cleared");
    }

    private void OnSettleElapsed(object? sender, EventArgs e)
    {
        _settle.Stop();
        if (_pendingDeferred > 0) return;   // still waiting on combat — keep held
        Release("settle-elapsed");
    }

    private void Assert(string reason)
    {
        if (_asserted) return;
        _asserted = true;
        _coordinator.AssertGate(MovementCoordinator.AcquisitionGate, AsserterName, reason);
    }

    private void Release(string reason)
    {
        if (!_asserted) return;
        _asserted = false;
        _coordinator.ClearGate(MovementCoordinator.AcquisitionGate, AsserterName, reason);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settle.Stop();
        _settle.Tick -= OnSettleElapsed;
        Release("disposed");
    }
}
