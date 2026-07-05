using FujinTerm.Game;
using FujinTerm.Game.Combat;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// The death-halt bridge: on our own death it asserts
/// <see cref="MovementCoordinator.UserGate"/> so every movement engine stops and
/// we sit in the graveyard until a manual resume, flavouring the chip via
/// <see cref="PlayerDeathMovementHalt.HaltedForDeath"/>.
/// </summary>
public sealed class PlayerDeathMovementHaltTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public DeathLineWatcher Watcher { get; }
        public MovementCoordinator Coord { get; } = new();
        public PlayerDeathMovementHalt Halt { get; }
        public int FlavourChanges { get; private set; }

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Watcher = new DeathLineWatcher(Router);
            Halt = new PlayerDeathMovementHalt(Watcher, Coord);
            Halt.HaltedForDeathChanged += () => FlavourChanges++;
        }

        public void Die() => Feed("You have been slain by a giant rat.");

        public void Feed(string line)
        {
            LineExtractor.EmittedLine emitted = new(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false);
            Router.Dispatch(emitted);
        }

        public void Dispose()
        {
            Halt.Dispose();
            Watcher.Dispose();
        }
    }

    [Fact]
    public void FreshBridge_NotHaltedNotPaused()
    {
        using Harness h = new();
        Assert.False(h.Halt.HaltedForDeath);
        Assert.False(h.Coord.IsPaused);
        Assert.DoesNotContain(MovementCoordinator.UserGate, h.Coord.AssertedGates);
    }

    [Fact]
    public void Death_AssertsUserGateAndHalts()
    {
        using Harness h = new();
        h.Die();

        Assert.True(h.Halt.HaltedForDeath);
        Assert.True(h.Coord.IsPaused);
        Assert.Contains(MovementCoordinator.UserGate, h.Coord.AssertedGates);
    }

    [Fact]
    public void Death_TagsAsserterInHistory()
    {
        using Harness h = new();
        h.Die();

        GateTransitionEntry entry = h.Coord.History.Single(e =>
            e.Gate == MovementCoordinator.UserGate && e.Asserted);
        Assert.Equal(PlayerDeathMovementHalt.AsserterName, entry.Asserter);
    }

    [Fact]
    public void UserResume_AutoClearsFlavour()
    {
        using Harness h = new();
        h.Die();
        Assert.True(h.Halt.HaltedForDeath);

        // A manual resume clears UserGate through any Navigation affordance.
        h.Coord.ClearGate(MovementCoordinator.UserGate);

        Assert.False(h.Halt.HaltedForDeath);
        Assert.False(h.Coord.IsPaused);
    }

    [Fact]
    public void FlavourClears_WhenUserGateGoes_EvenWithOtherGatesHeld()
    {
        // HaltedForDeath keys off UserGate specifically, not overall IsPaused —
        // a still-asserted combat gate must not keep the "recovering" flavour up.
        using Harness h = new();
        h.Coord.AssertGate(MovementCoordinator.CombatGate);
        h.Die();
        Assert.True(h.Halt.HaltedForDeath);

        h.Coord.ClearGate(MovementCoordinator.UserGate);

        Assert.False(h.Halt.HaltedForDeath);
        Assert.True(h.Coord.IsPaused); // combat still holds movement
    }

    [Fact]
    public void ManualUserPause_DoesNotFlagHalt()
    {
        // A user pause with no death behind it must read as plain "Paused",
        // never "recovering".
        using Harness h = new();
        h.Coord.AssertGate(MovementCoordinator.UserGate);

        Assert.False(h.Halt.HaltedForDeath);
    }

    [Fact]
    public void FlavourChange_FiresOnceEachDirection()
    {
        using Harness h = new();
        h.Die();                                            // false -> true
        h.Coord.ClearGate(MovementCoordinator.UserGate);    // true -> false

        Assert.Equal(2, h.FlavourChanges);
        Assert.False(h.Halt.HaltedForDeath);
    }

    [Fact]
    public void DeathWhileAlreadyPaused_StillFlagsHalt()
    {
        // Dying during a manual pause: AssertGate is idempotent (no second
        // GatesChanged), but the death still flips the flavour so the chip
        // switches from "Paused" to "Paused — recovering".
        using Harness h = new();
        h.Coord.AssertGate(MovementCoordinator.UserGate);
        h.Die();

        Assert.True(h.Halt.HaltedForDeath);
    }

    [Fact]
    public void Dispose_StopsReactingToDeath()
    {
        using Harness h = new();
        h.Halt.Dispose();
        h.Die();

        Assert.False(h.Halt.HaltedForDeath);
        Assert.DoesNotContain(MovementCoordinator.UserGate, h.Coord.AssertedGates);
    }
}
