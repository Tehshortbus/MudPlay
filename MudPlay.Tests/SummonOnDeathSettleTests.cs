using System.IO;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Killing a monster whose death spell summons another must hold the walker until
// the room has been re-read, or it steps out and drags the summon along (report
// paradigm-20260729-211336). The kill itself drops the corpse from the roster in
// the same handler chain, which re-fires the room observation — that synthetic
// re-fire is not the re-read, so the hold has to survive it. These tests pin what
// does and doesn't release SummonOnDeathSettle's gate.
public sealed class SummonOnDeathSettleTests
{
    private const int SummonerNumber = 457;
    private const string Summoner = "dwarf warrior";
    private const int PlainNumber = 999;
    private const string Plain = "giant rat";

    private sealed class Harness : IDisposable
    {
        private readonly string _root;

        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new(new ProfileService(), () => null);
        public PlayerState State { get; } = new();
        public LogService Log { get; } = new();
        public MovementCoordinator Coordinator { get; }
        public RoomEntityClassifier Classifier { get; }
        public CombatStateTracker Tracker { get; }
        public MonsterDeathWatcher Watcher { get; }
        public SummonOnDeathSettle Settle { get; }

        public string? Target { get; set; } = Summoner;
        public bool MovementActive { get; set; } = true;
        public List<string> SentRaw { get; } = new();

        // Times the coordinator went from paused to free — each one is the
        // walker being told it may step.
        public int Releases { get; private set; }

        // Captured settle-window callback (the DispatcherTimer stand-in).
        public Action? Scheduled { get; private set; }

        public Harness()
        {
            _root = Path.Combine(Path.GetTempPath(),
                "mudplay-summon-settle-tests-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_root, "alpha"));
            File.WriteAllText(Path.Combine(_root, "alpha", "Monsters.json"), $$"""
                [
                  { "Number": {{SummonerNumber}}, "Name": "{{Summoner}}", "DeathSpell": 550 },
                  { "Number": {{PlainNumber}},    "Name": "{{Plain}}",    "DeathSpell": 0 }
                ]
                """);
            File.WriteAllText(Path.Combine(_root, "alpha", "Spells.json"), """
                [
                  { "Number": 550, "Name": "summon brain eater", "Abil-0": 12, "AbilVal-0": 459 }
                ]
                """);
            GameDataCache cache = new(_root);
            cache.SwitchSet("alpha");

            DefaultPatterns.Seed(Router);
            AddMonster(SummonerNumber, Summoner);
            AddMonster(PlainNumber, Plain);
            AddMonster(459, "brain eater");

            Coordinator = new MovementCoordinator(Log);
            Coordinator.PauseStateChanged += paused => { if (!paused) Releases++; };
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log);
            // Same construction order as AppServices: the Combat gate's tracker
            // hears a room observation before the settle does.
            Tracker = new CombatStateTracker(
                Router, Coordinator, Classifier, Monsters, State,
                () => true,
                resolveOverlay: _ => new MonsterOverlay(),
                log: Log);
            Watcher = new MonsterDeathWatcher(Router, Log);
            Settle = new SummonOnDeathSettle(
                Watcher, Classifier, Coordinator, new MonsterDeathSummonIndex(cache),
                currentTargetName: () => Target,
                movementActive: () => MovementActive,
                log: Log,
                scheduleOverride: (_, act) => Scheduled = act);
            Settle.SetWireSender(bytes => SentRaw.Add(System.Text.Encoding.Latin1.GetString(bytes)));
            // The roster resync AppServices wires AFTER the settle: the kill is
            // attributed to the engaged target and dropped from the roster.
            Watcher.MonsterDied += _ =>
            {
                if (Target is { } dead) Classifier.RemoveDeadEntity(dead);
            };
        }

        private void AddMonster(int number, string name) =>
            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}",
                Name: name,
                Links: new[] { new GameDataLink("Monsters", number) }));

        public void Feed(string line) =>
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        public void Kill()
        {
            Feed("You gain 9 experience.");
            Feed("*Combat Off*");
        }

        public bool SettleHeld =>
            Coordinator.IsGateAsserted(MovementCoordinator.SummonDeathSettleGate);

        public void Dispose()
        {
            Settle.Dispose();
            Watcher.Dispose();
            Tracker.Dispose();
            Classifier.Dispose();
            try { Directory.Delete(_root, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    [Fact]
    public void SummonerKill_HoldsThroughTheKillsOwnRosterRemoval()
    {
        using Harness h = new();
        h.Feed($"Also here: {Summoner}.");
        Assert.True(h.Coordinator.IsGateAsserted(MovementCoordinator.CombatGate));

        h.Kill();

        Assert.Equal(new[] { "\r" }, h.SentRaw);
        RoomEntitiesObservation roster = Assert.NotNull(h.Classifier.Current);
        Assert.Empty(roster.Entities);
        Assert.Equal(RoomObservationSource.Death, roster.Source);
        Assert.False(h.Coordinator.IsGateAsserted(MovementCoordinator.CombatGate));
        Assert.True(h.SettleHeld);
        Assert.Equal(0, h.Releases);
    }

    // A room spell's kill names nobody, and the engaged target is only the monster
    // the round was anchored to. With a summoner among the kinds the room listed, the
    // re-scan and its hold are worth having whichever of them died.
    [Fact]
    public void RoomSpellKill_WithASummonerAmongTheListedKinds_Holds()
    {
        using Harness h = new();
        h.Feed($"Also here: {Plain}, {Summoner}.");
        h.Target = Plain;

        h.Watcher.NoteRoomSpellKill(9, new[]
        {
            new MonsterDeathIdentity(PlainNumber, Plain),
            new MonsterDeathIdentity(SummonerNumber, Summoner),
        });

        Assert.Equal(new[] { "\r" }, h.SentRaw);
        Assert.True(h.SettleHeld);
    }

    [Fact]
    public void RoomSpellKill_WithNoSummonerListed_DoesNotHold()
    {
        using Harness h = new();
        h.Feed($"Also here: {Plain}, {Plain}.");
        h.Target = Summoner;   // a stale anchor must not be read for this death

        h.Watcher.NoteRoomSpellKill(9, new[] { new MonsterDeathIdentity(PlainNumber, Plain) });

        Assert.Empty(h.SentRaw);
        Assert.False(h.SettleHeld);
    }

    [Fact]
    public void SyntheticRosterRefires_DoNotRelease()
    {
        using Harness h = new();
        h.Feed($"Also here: {Summoner}, {Plain}.");
        h.Kill();
        Assert.True(h.SettleHeld);

        h.Classifier.RemoveDepartedEntity(Plain);
        Assert.True(h.SettleHeld);

        h.Classifier.AppendArrivalEntity(h.Classifier.Classify(Plain), "A giant rat scurries in.");
        Assert.True(h.SettleHeld);

        h.Classifier.NoteRoomChanged();
        Assert.True(h.SettleHeld);
    }

    [Fact]
    public void RedisplayWithTheSummon_HandsTheHoldToCombat()
    {
        using Harness h = new();
        h.Feed($"Also here: {Summoner}.");
        h.Kill();

        h.Feed("Also here: brain eater.");

        Assert.False(h.SettleHeld);
        Assert.True(h.Coordinator.IsGateAsserted(MovementCoordinator.CombatGate));
        Assert.Equal(0, h.Releases);
    }

    [Fact]
    public void EmptyRedisplay_ReleasesWhenTheDisplayFinishes()
    {
        // An empty room prints no "Also here:", so the classifier never fires.
        using Harness h = new();
        h.Feed($"Also here: {Summoner}.");
        h.Kill();

        h.Settle.NoteRoomDisplayed();

        Assert.False(h.SettleHeld);
        Assert.Equal(1, h.Releases);
    }

    [Fact]
    public void NoRedisplay_ReleasesWhenTheWindowElapses()
    {
        using Harness h = new();
        h.Feed($"Also here: {Summoner}.");
        h.Kill();

        Action elapse = Assert.IsType<Action>(h.Scheduled);
        elapse();

        Assert.False(h.SettleHeld);
        Assert.Equal(1, h.Releases);
    }

    [Fact]
    public void KillThatSummonsNothing_NeverHolds()
    {
        using Harness h = new() { Target = Plain };
        h.Feed($"Also here: {Plain}.");

        h.Kill();

        Assert.False(h.SettleHeld);
        Assert.Empty(h.SentRaw);
        Assert.Equal(1, h.Releases);
    }

    [Fact]
    public void HandFoughtKill_SendsNoCarriageReturn()
    {
        using Harness h = new() { MovementActive = false };
        h.Feed($"Also here: {Summoner}.");

        h.Kill();

        Assert.False(h.SettleHeld);
        Assert.Empty(h.SentRaw);
    }
}
