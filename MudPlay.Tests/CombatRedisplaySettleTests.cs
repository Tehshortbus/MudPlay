using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// A loop through aggro monsters can render a room empty and confirm the in-flight
// move a beat BEFORE a pursuing hostile's leap-in / attack line reaches the room
// — so the loop steps past the fight while the mob is still swinging (the
// "skipping past hostile targets" / "movement sent while in combat" reports).
// CombatManager already detects that moment (a combat line with no engageable in
// view + no current target → CR re-display + RoomAppearsEmptyDuringCombat).
// CombatRedisplaySettle asserts a short-lived gate on that signal to hold the loop
// until the re-display resolves. These tests pin that it (a) asserts on a
// combat line in an apparently-empty room, (b) releases on the next room
// observation, (c) self-clears when the window elapses, and (d) stays silent when
// the room already holds an engageable (no skip risk there).
public sealed class CombatRedisplaySettleTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new(new ProfileService(), () => null);
        public PartyState Party { get; } = new();
        public LogService Log { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public CombatManager Combat { get; }
        public MovementCoordinator Coordinator { get; } = new();
        public CombatRedisplaySettle Settle { get; }
        public Dictionary<int, MonsterOverlay> Overlays { get; } = new();

        // Captured settle-window callback (the DispatcherTimer stand-in). Invoke it
        // to simulate the timeout firing.
        public Action? Scheduled { get; private set; }

        public CombatSettings Settings { get; } = new()
        {
            NormalAttackCommand = "a",
            TargetOrder = TargetOrder.Normal,
        };

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log);
            Combat = new CombatManager(Router, Classifier, Monsters,
                resolveOverlay: n => Overlays.TryGetValue(n, out MonsterOverlay? o)
                                     ? o : new MonsterOverlay(),
                party: Party,
                readSettings: () => Settings,
                isEnabled: () => true,
                readOwnGivenName: () => "MudPlay",
                post: a => a(),
                log: Log);
            Combat.SetWireSender(_ => { });
            Settle = new CombatRedisplaySettle(
                Combat, Classifier, Coordinator, Log,
                scheduleOverride: (_, act) => Scheduled = act);
        }

        public void AddMonster(int number, string name)
        {
            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}",
                Name: name,
                Links: new[] { new GameDataLink("Monsters", number) }));
        }

        public void Feed(string line)
        {
            LineExtractor.EmittedLine emitted = new(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false);
            Router.Dispatch(emitted);
        }

        public bool GateAsserted =>
            Coordinator.IsGateAsserted(MovementCoordinator.CombatRedisplaySettleGate);

        public void Dispose()
        {
            Settle.Dispose();
            Combat.Dispose();
            Classifier.Dispose();
        }
    }

    [Fact]
    public void CombatLineInApparentlyEmptyRoom_AssertsSettleGate()
    {
        using Harness h = new();
        h.AddMonster(1, "giant toad");

        // No room observation yet — classifier shows empty, no target. A mob swing
        // means a hostile is here our view lost: CombatManager fires the CR + the
        // RoomAppearsEmptyDuringCombat signal the settle watches.
        h.Feed("The giant toad lashes at you but misses!");

        Assert.True(h.GateAsserted);
        Assert.NotNull(h.Scheduled);
    }

    [Fact]
    public void NextRoomObservation_ReleasesSettleGate()
    {
        using Harness h = new();
        h.AddMonster(1, "giant toad");

        h.Feed("The giant toad lashes at you but misses!");
        Assert.True(h.GateAsserted);

        // The CR re-display lands as a fresh room render (EntitiesObserved). The
        // settle releases on it — whatever the render shows, our brief hold is done
        // (a revealed hostile hands off to the Combat gate; an empty room steps on).
        h.Feed("Also here: giant toad.");

        Assert.False(h.GateAsserted);
    }

    [Fact]
    public void SettleWindowElapsed_ClearsGate()
    {
        using Harness h = new();
        h.AddMonster(1, "giant toad");

        h.Feed("The giant toad lashes at you but misses!");
        Assert.True(h.GateAsserted);

        // No re-display ever comes — the timeout fallback releases the hold so the
        // loop isn't stalled forever.
        h.Scheduled!.Invoke();

        Assert.False(h.GateAsserted);
    }

    [Fact]
    public void CombatLineWithEngageableInRoom_DoesNotAssert()
    {
        using Harness h = new();
        h.AddMonster(1, "giant toad");

        // Room already shows the hostile — CombatManager engages it and never fires
        // the empty-room signal, so there's no skip risk and no settle.
        h.Feed("Also here: giant toad.");
        h.Feed("The giant toad lashes at you but misses!");

        Assert.False(h.GateAsserted);
    }
}
