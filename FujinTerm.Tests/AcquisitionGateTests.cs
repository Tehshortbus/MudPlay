using FujinTerm.Game.Inventory;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.J — <see cref="AcquisitionGate"/> drives the single
/// <see cref="MovementCoordinator.AcquisitionGate"/> on behalf of both
/// the item and cash engines. These tests cover the synchronous state
/// machine; the time-based settle release (a <c>DispatcherTimer</c>
/// tick) is Avalonia plumbing not exercised in headless xUnit.
/// </summary>
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
}
