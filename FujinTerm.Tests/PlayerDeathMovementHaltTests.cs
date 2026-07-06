using System.IO;
using FujinTerm.Game;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// The death-halt bridge: on our own death it asserts
/// <see cref="MovementCoordinator.UserGate"/> so every movement engine stops and
/// we sit in the graveyard until a manual resume, flavouring the chip via
/// <see cref="PlayerDeathMovementHalt.HaltedForDeath"/>. It rides
/// <see cref="RoomTracker.PlayerDeathObserved"/>, which fires for BOTH death
/// phrasings, so a miracle-save death ("You have N lives left.") halts as surely
/// as a plain "slain by" one.
/// </summary>
public sealed class PlayerDeathMovementHaltTests
{
    private sealed class Harness : IDisposable
    {
        private const string GraphJson = """
        [
          { "Number": 1, "Name": "Start", "Map": 1, "Light": 0, "Shop": 0,
            "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

        private readonly string _root;
        public RoomTracker Tracker { get; }
        public MovementCoordinator Coord { get; } = new();
        public PlayerDeathMovementHalt Halt { get; }
        public int FlavourChanges { get; private set; }

        public Harness()
        {
            _root = Path.Combine(Path.GetTempPath(), "fujinterm-deathhalt-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_root, "alpha"));
            File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
            GameDataCache cache = new(_root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");

            Tracker = new RoomTracker(graph);
            Halt = new PlayerDeathMovementHalt(Tracker, Coord);
            Halt.HaltedForDeathChanged += () => FlavourChanges++;
        }

        // A miracle-save death — the phrasing that DeathLineWatcher's "slain by"
        // watcher never sees. RoomTracker.NoteDeath fires PlayerDeathObserved
        // regardless of phrasing.
        public void Die() => Tracker.NoteDeath(6, "You have 6 lives left.");

        public void Dispose()
        {
            Halt.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
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
