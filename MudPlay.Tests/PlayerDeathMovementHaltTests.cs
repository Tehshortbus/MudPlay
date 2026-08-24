using System.Collections.Generic;
using System.IO;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// The death-stop bridge: on our own death it full-stops every movement engine
/// (the wired stopper) and CLEARS <see cref="MovementCoordinator.UserGate"/> — a
/// clean stop, same as the Nav Stop button, so nothing survives to re-drive us and
/// a manual/remote nav action afterward runs freely. It rides
/// <see cref="RoomTracker.PlayerDeathObserved"/>, which fires for BOTH death
/// phrasings, so a miracle-save death ("You have N lives left.") stops as surely
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
            "N": "2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Number": 2, "Name": "Graveyard", "Map": 1, "Light": 0, "Shop": 0,
            "Lair": "", "Delay": 5,
            "N": "0", "S": "1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

        private readonly string _root;
        public RoomTracker Tracker { get; }
        public MovementCoordinator Coord { get; } = new();
        public PlayerDeathMovementHalt Halt { get; }
        public int EngineStopCount { get; private set; }
        public List<byte[]> Sent { get; } = new();

        public Harness()
        {
            _root = Path.Combine(Path.GetTempPath(), "mudplay-deathhalt-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_root, "alpha"));
            File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
            GameDataCache cache = new(_root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");

            Tracker = new RoomTracker(graph);
            Halt = new PlayerDeathMovementHalt(Tracker, Coord);
            Halt.SetWireSender(Sent.Add);
            // AppServices always wires a stopper (Walker/LoopRunner/AutoLair Stop);
            // count invocations so the death-stop path is exercised as in production.
            Halt.SetEngineStopper(() => EngineStopCount++);
        }

        // A miracle-save death — the phrasing that DeathLineWatcher's "slain by"
        // watcher never sees. RoomTracker.NoteDeath fires PlayerDeathObserved
        // regardless of phrasing.
        public void Die() => Tracker.NoteDeath(6, "You have 6 lives left.");

        public bool SentCarriageReturn =>
            Sent.Exists(b => Encoding.Latin1.GetString(b) == "\r");

        public void Dispose()
        {
            Halt.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    [Fact]
    public void FreshBridge_NotPaused()
    {
        using Harness h = new();
        Assert.False(h.Coord.IsPaused);
        Assert.DoesNotContain(MovementCoordinator.UserGate, h.Coord.AssertedGates);
    }

    [Fact]
    public void Death_StopsEnginesAndLeavesGateClear()
    {
        // Death is a clean stop: it invokes the engine stopper and leaves the
        // UserGate clear (NOT a lingering pause), so a manual/remote nav action
        // afterward runs freely.
        using Harness h = new();
        h.Die();

        Assert.Equal(1, h.EngineStopCount);
        Assert.False(h.Coord.IsPaused);
        Assert.DoesNotContain(MovementCoordinator.UserGate, h.Coord.AssertedGates);
    }

    [Fact]
    public void DeathWhileManuallyPaused_ClearsThePause()
    {
        // Dying during a manual pause resets to a clean stop — the stale pause must
        // not outlive the death and block the player's next move.
        using Harness h = new();
        h.Coord.AssertGate(MovementCoordinator.UserGate);
        Assert.True(h.Coord.IsPaused);

        h.Die();

        Assert.Equal(1, h.EngineStopCount);
        Assert.DoesNotContain(MovementCoordinator.UserGate, h.Coord.AssertedGates);
    }

    /// <summary>
    /// The stubbed stopper here never touches the coordinator — matching the
    /// real production shape where all three engines' own Stop(reason)
    /// no-op because each is already idle (e.g. a walker that self-bailed
    /// to Idle chasing a genuinely Lost tracker before death). Death must
    /// still disengage MovementCoordinator.AutomationEngaged directly, the
    /// same unconditional pattern as the UserGate clear right above it —
    /// otherwise PassiveRelocalizer's Stage 2 survives the respawn and
    /// walks the freshly-dead character around the graveyard.
    /// </summary>
    [Fact]
    public void Death_DisengagesAutomation_EvenWhenTheStopperDoesNotTouchTheCoordinator()
    {
        using Harness h = new();
        h.Coord.EngageAutomation();

        h.Die();

        Assert.False(h.Coord.AutomationEngaged);
    }

    [Fact]
    public void Death_LeavesOtherGatesUntouched()
    {
        // The death stop clears only the UserGate — a still-asserted combat gate
        // (its own concern) keeps holding movement until it clears on its own.
        using Harness h = new();
        h.Coord.AssertGate(MovementCoordinator.CombatGate);
        h.Die();

        Assert.DoesNotContain(MovementCoordinator.UserGate, h.Coord.AssertedGates);
        Assert.Contains(MovementCoordinator.CombatGate, h.Coord.AssertedGates);
    }

    [Fact]
    public void GraveyardResync_StillPendingRespawn_SendsCr()
    {
        // Report stock-20260730-194053: after death the respawn room display can be
        // slow, leaving us "lost". If we're still un-anchored when the resync
        // fallback fires, a CR forces the graveyard to re-display so PendingRespawn's
        // candidate search can land it.
        using Harness h = new();
        h.Die();
        Assert.Equal(RoomConfidence.PendingRespawn, h.Tracker.State.Confidence);
        Assert.False(h.SentCarriageReturn);   // armed, not fired yet

        h.Halt.FireGraveyardResyncForTests();

        Assert.True(h.SentCarriageReturn);
    }

    [Fact]
    public void GraveyardResync_NoWireSender_NoThrow()
    {
        // Unbound sender (pre-connect) → the resync is a silent no-op, never a throw.
        using Harness h = new();
        var halt = new PlayerDeathMovementHalt(h.Tracker, h.Coord);   // no SetWireSender
        h.Tracker.NoteDeath(6, "You have 6 lives left.");

        halt.FireGraveyardResyncForTests();   // must not throw

        halt.Dispose();
    }

    [Fact]
    public void Dispose_StopsReactingToDeath()
    {
        using Harness h = new();
        h.Halt.Dispose();
        h.Die();

        Assert.Equal(0, h.EngineStopCount);
        Assert.DoesNotContain(MovementCoordinator.UserGate, h.Coord.AssertedGates);
    }
}
