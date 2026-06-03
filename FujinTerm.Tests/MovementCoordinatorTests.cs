using FujinTerm.Game.Map;
using Xunit;

namespace FujinTerm.Tests;

public sealed class MovementCoordinatorTests
{
    [Fact]
    public void Fresh_NotPaused()
    {
        MovementCoordinator c = new();
        Assert.False(c.IsPaused);
        Assert.Empty(c.AssertedGates);
    }

    [Fact]
    public void AssertGate_TogglesPausedState()
    {
        MovementCoordinator c = new();
        bool? last = null;
        c.PauseStateChanged += p => last = p;

        c.AssertGate("user");

        Assert.True(c.IsPaused);
        Assert.True(last);
    }

    [Fact]
    public void AssertGate_Idempotent_DoesNotRefire()
    {
        MovementCoordinator c = new();
        int fires = 0;
        c.PauseStateChanged += _ => fires++;

        c.AssertGate("user");
        c.AssertGate("user");

        Assert.Equal(1, fires);
    }

    [Fact]
    public void ClearOneGate_WithAnotherActive_StaysPaused()
    {
        MovementCoordinator c = new();
        int fires = 0;
        c.PauseStateChanged += _ => fires++;

        c.AssertGate("user");
        c.AssertGate("@wait");
        Assert.Equal(1, fires);

        c.ClearGate("user");
        Assert.True(c.IsPaused);
        Assert.Equal(1, fires);                   // no transition fire
    }

    [Fact]
    public void ClearLastGate_TransitionsBackToUnpaused()
    {
        MovementCoordinator c = new();
        bool? last = null;
        c.PauseStateChanged += p => last = p;

        c.AssertGate("user");
        c.ClearGate("user");

        Assert.False(c.IsPaused);
        Assert.False(last);
    }

    [Fact]
    public void AssertedGates_ListsAllCurrentlyAsserted()
    {
        MovementCoordinator c = new();
        c.AssertGate("user");
        c.AssertGate("@wait");
        Assert.Contains("user", c.AssertedGates);
        Assert.Contains("@wait", c.AssertedGates);
        Assert.Equal(2, c.AssertedGates.Count);
    }
}
