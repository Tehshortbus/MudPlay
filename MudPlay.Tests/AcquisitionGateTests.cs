using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// AcquisitionGate drives the single
// MovementCoordinator.AcquisitionGate on behalf of both
// the item and cash engines. These tests cover the synchronous state
// machine; the time-based settle release (a DispatcherTimer
// tick) is Avalonia plumbing not exercised in headless xUnit.
public sealed class AcquisitionGateTests
{
    private static bool Asserted(MovementCoordinator c) =>
        c.AssertedGates.Contains(MovementCoordinator.AcquisitionGate);

    [Fact]
    public void DeferredPending_AssertsGate()
    {
        MovementCoordinator coord = new();
        using AcquisitionGate gate = new(coord);

        gate.NoteDeferredPending(2);

        Assert.True(Asserted(coord));
        Assert.True(coord.IsPaused);
    }

    [Fact]
    public void DeferredPending_Zero_DoesNotAssert()
    {
        MovementCoordinator coord = new();
        using AcquisitionGate gate = new(coord);

        gate.NoteDeferredPending(0);

        Assert.False(Asserted(coord));
    }

    [Fact]
    public void GetSent_AssertsGate()
    {
        MovementCoordinator coord = new();
        using AcquisitionGate gate = new(coord);

        gate.NoteGetSent();

        Assert.True(Asserted(coord));
    }

    [Fact]
    public void DeferredCleared_WithoutGets_ReleasesImmediately()
    {
        MovementCoordinator coord = new();
        using AcquisitionGate gate = new(coord);
        gate.NoteDeferredPending(3);
        Assert.True(Asserted(coord));

        // Queue dropped (e.g. user walked away mid-combat) — no gets armed
        // the settle window, so the gate releases right away.
        gate.NoteDeferredCleared();

        Assert.False(Asserted(coord));
    }

    [Fact]
    public void DeferredCleared_AfterGets_StaysHeldForSettle()
    {
        MovementCoordinator coord = new();
        using AcquisitionGate gate = new(coord);
        gate.NoteDeferredPending(1);

        // Flush: a get goes out (arms settle), then the queue is cleared.
        // The settle window now governs release, so the gate stays held.
        gate.NoteGetSent();
        gate.NoteDeferredCleared();

        Assert.True(Asserted(coord));
    }

    [Fact]
    public void Assert_IsIdempotent_SingleGateName()
    {
        MovementCoordinator coord = new();
        using AcquisitionGate gate = new(coord);

        gate.NoteGetSent();
        gate.NoteGetSent();
        gate.NoteDeferredPending(2);

        Assert.Single(coord.AssertedGates, MovementCoordinator.AcquisitionGate);
    }

    [Fact]
    public void Dispose_ReleasesGate()
    {
        MovementCoordinator coord = new();
        AcquisitionGate gate = new(coord);
        gate.NoteGetSent();
        Assert.True(Asserted(coord));

        gate.Dispose();

        Assert.False(Asserted(coord));
    }

    [Fact]
    public void GetConfirmed_ReleasesGate_WithoutWaitingOnSettle()
    {
        MovementCoordinator coord = new();
        using AcquisitionGate gate = new(coord);
        gate.NoteGetSent();
        Assert.True(Asserted(coord));

        // Server confirmed the pickup — the gate releases immediately.
        gate.NoteGetConfirmed();

        Assert.False(Asserted(coord));
    }

    [Fact]
    public void MultipleGets_ReleaseOnlyAfterAllConfirmed()
    {
        MovementCoordinator coord = new();
        using AcquisitionGate gate = new(coord);
        gate.NoteGetSent();
        gate.NoteGetSent();

        gate.NoteGetConfirmed();
        Assert.True(Asserted(coord));       // one still outstanding

        gate.NoteGetConfirmed();
        Assert.False(Asserted(coord));      // both confirmed → released
    }

    [Fact]
    public void GetConfirmed_WithPendingDeferred_StaysHeld()
    {
        MovementCoordinator coord = new();
        using AcquisitionGate gate = new(coord);
        gate.NoteDeferredPending(1);        // still waiting on combat
        gate.NoteGetSent();

        gate.NoteGetConfirmed();

        Assert.True(Asserted(coord));       // deferred items still pending
    }

    [Fact]
    public void GetConfirmed_WithNothingOutstanding_IsNoOp()
    {
        MovementCoordinator coord = new();
        using AcquisitionGate gate = new(coord);

        // A stray confirmation (e.g. a manual get) with nothing pending does
        // nothing — the gate was never asserted.
        gate.NoteGetConfirmed();

        Assert.False(Asserted(coord));
    }

    [Fact]
    public void ReLookPending_HoldsUntilTheDisplayArrives()
    {
        MovementCoordinator coord = new();
        using AcquisitionGate gate = new(coord);

        gate.NoteReLookPending();
        Assert.True(Asserted(coord));

        gate.NoteReLookDone();
        Assert.False(Asserted(coord));
    }

    [Fact]
    public void ReLookDone_WithAGetOut_StaysHeldUntilItConfirms()
    {
        MovementCoordinator coord = new();
        using AcquisitionGate gate = new(coord);
        gate.NoteReLookPending();
        gate.NoteGetSent();                 // the survey listed a flagged drop

        gate.NoteReLookDone();
        Assert.True(Asserted(coord));

        gate.NoteGetConfirmed();
        Assert.False(Asserted(coord));
    }

    [Fact]
    public void ReLookPending_OutlastsAnotherEnginesRelease()
    {
        MovementCoordinator coord = new();
        using AcquisitionGate gate = new(coord);
        gate.NoteDeferredPending(1);        // the cash engine's own hold
        gate.NoteReLookPending();

        gate.NoteDeferredCleared();
        Assert.True(Asserted(coord));       // the floor check is still out

        gate.NoteReLookDone();
        Assert.False(Asserted(coord));
    }

    [Fact]
    public void ReLookDone_LeavesDeferredItemsHolding()
    {
        MovementCoordinator coord = new();
        using AcquisitionGate gate = new(coord);
        gate.NoteReLookPending();
        gate.NoteDeferredPending(2);        // a monster is still here; gets wait on it

        gate.NoteReLookDone();

        Assert.True(Asserted(coord));
    }
}
