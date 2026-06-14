using Avalonia.Threading;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.Game.Inventory;

/// <summary>
/// Phase 9 PR 9.J — shared driver for
/// <see cref="MovementCoordinator.AcquisitionGate"/>. Both the item
/// engine (<see cref="AutoGetItemsManager"/>) and the cash engine
/// (<c>CashManager</c>) feed this one instance; it owns the single
/// assert/clear of the gate so the two engines' states are AND-ed —
/// the walker resumes only once <b>both</b> have finished looting.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MovementCoordinator"/> keys gates by name in a
/// <see cref="HashSet{T}"/>, so a gate is single-owner: if both engines
/// asserted <c>"Acquisition"</c> directly, the first to clear would drop
/// it while the other still wanted it held. This driver is the single
/// owner; engines report activity and it ANDs.
/// </para>
/// <para>
/// Gate is held while <b>either</b> condition is live:
/// </para>
/// <list type="bullet">
/// <item><b>Pending deferred items</b> — <see cref="AutoGetItemsManager"/>
/// queued gets that wait for the room's combat to finish. Held with no
/// timeout until flushed or the room changes. Asserting here — while the
/// Combat gate is still up, on the <i>same</i>
/// <c>EntitiesObserved</c> pass that
/// <c>CombatStateTracker</c> later uses to clear the Combat gate — is
/// what defeats the synchronous walker-resume race: without it the
/// walker would step out the instant Combat clears, before the loot
/// flush runs.</item>
/// <item><b>Settle window</b> — a short quiet period after the last
/// <c>get</c> command. There is no server-side item-pickup confirmation
/// line to key on, so get-clear for items is command-side: once gets
/// stop flowing for <see cref="SettleWindow"/>, collection is treated as
/// finished and the walker may resume.</item>
/// </list>
/// </remarks>
public sealed class AcquisitionGate : IDisposable
{
    /// <summary>Asserter identity recorded in the <c>[Gate]</c> log +
    /// <see cref="MovementCoordinator.History"/>.</summary>
    public const string AsserterName = "Acquisition";

    /// <summary>Quiet period after the last <c>get</c> before the gate
    /// releases. No server-side item-pickup confirmation line exists, so
    /// get-clear is command-side: once gets stop flowing for this long
    /// with no pending deferred items, collection is treated as
    /// done.</summary>
    private static readonly TimeSpan SettleWindow = TimeSpan.FromMilliseconds(1200);

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

    /// <summary>
    /// Note that the item engine queued <paramref name="count"/> gets to
    /// fire once the room's combat finishes. Asserts the gate now — while
    /// the Combat gate is still held — so the walker can't step out the
    /// instant combat clears, before the deferred flush runs. A count of
    /// 0 is a no-op (nothing to wait for).
    /// </summary>
    public void NoteDeferredPending(int count)
    {
        _pendingDeferred = count;
        if (count > 0) Assert($"deferred-pending={count}");
    }

    /// <summary>
    /// Note a <c>get</c> command was just dispatched (item or cash).
    /// Asserts the gate and re-arms the settle window; the gate releases
    /// <see cref="SettleWindow"/> after the last get with no further
    /// activity and no pending deferred items.
    /// </summary>
    public void NoteGetSent()
    {
        Assert("get-sent");
        _settle.Stop();
        _settle.Start();
    }

    /// <summary>
    /// The deferred queue was flushed or discarded — no items wait on
    /// combat anymore. If the flush armed the settle window (gets went
    /// out) the gate stays held until that elapses; if the queue was
    /// dropped without any gets (e.g. the user walked away mid-combat),
    /// the gate releases immediately.
    /// </summary>
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
