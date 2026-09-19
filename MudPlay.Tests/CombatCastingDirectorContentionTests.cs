using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Reproduces a reported "won't re-engage after buffing/healing" combat stall by
// wiring CombatManager and CastingDirector together the way AppServices does
// (sharing one CastCoordinator, CastDirector.CastFired -> Combat.NoteBetweenRoundCast)
// instead of testing either engine in isolation.
public sealed class CombatCastingDirectorContentionTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new();
        public PartyState Party { get; } = new();
        public LogService Log { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public CombatManager Combat { get; }
        public CastCoordinator Cast { get; }
        public CastingDirector Director { get; }
        public PlayerState State { get; } = new();
        public List<byte[]> Sent { get; } = new();
        public CombatSettings CombatSettings { get; set; } = new()
        {
            NormalAttackCommand = "a",
            TargetOrder = TargetOrder.Normal,
        };
        public SpellsSettings Spells { get; set; } = new();
        public HealthSettings Health { get; set; } = new();

        public bool AutoCombatEnabled { get; set; } = true;
        public bool AutoNukeEnabled { get; set; } = true;
        public bool AutoHealRestEnabled { get; set; } = true;
        public int Ma { get; set; } = 100;
        public int MaxMa { get; set; } = 100;

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log);
            Cast = new CastCoordinator(Router, Log);
            Cast.SetWireSender(b => Sent.Add(b));
            Combat = new CombatManager(Router, Classifier, Monsters,
                resolveOverlay: _ => new MonsterOverlay(),
                party: Party,
                readSettings: () => CombatSettings,
                isEnabled: () => AutoCombatEnabled,
                readOwnGivenName: () => "MudPlay",
                post: a => a(),
                log: Log);
            Combat.SetWireSender(b => Sent.Add(b));
            Combat.SetAutoNukeGate(() => AutoNukeEnabled);
            Combat.SetCombatSpellCaster(Cast, () => (Ma, MaxMa));

            Director = new CastingDirector(State, Cast,
                readSpells: () => Spells,
                readHealth: () => Health,
                isEnabled: () => AutoHealRestEnabled,
                log: Log);
            // Mirrors AppServices: CastDirector.CastFired += Combat.NoteBetweenRoundCast.
            Director.CastFired += Combat.NoteBetweenRoundCast;
        }

        public void AddMonster(int number, string name)
            => Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}",
                Name: name,
                Links: new[] { new GameDataLink("Monsters", number) }));

        public void Feed(string line)
        {
            LineExtractor.EmittedLine emitted = new(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false);
            Router.Dispatch(emitted);
        }

        // Mirrors AppServices' tick order: Cast.OnCombatTick, then
        // CastDirector.NotifyRoundComplete (frees the round's between-round cast slot),
        // then CastDirector.OnCombatTick (survival casts), then Combat.OnCombatTick.
        public void Tick()
        {
            Cast.OnCombatTick();
            Director.NotifyRoundComplete();
            Director.OnCombatTick();
            Combat.OnCombatTick();
        }

        public IEnumerable<string> AllSent =>
            Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'));

        public void Dispose()
        {
            Combat.Dispose();
            Director.Dispose();
            Cast.Dispose();
            Classifier.Dispose();
        }
    }

    [Fact]
    public void SpellsFirst_RepeatedSelfHealInterrupts_StillLandsAttackSpell()
    {
        using Harness h = new();
        h.CombatSettings.ActionOrder = CombatActionOrder.SpellsFirst;
        h.CombatSettings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 0 };
        h.Spells.MinorHealSpell = "mihe";
        h.Health.MinorHealCombatTrigger = 80;   // heals whenever HP <= 80%.
        h.AddMonster(1, "giant rat");

        h.State.MaxHp = 100;
        h.State.HasPromptData = true;
        h.State.Hp = 100;   // full HP, not in combat yet — no premature heal

        // Engage — SpellsFirst opens on the attack spell.
        h.Feed("Also here: giant rat.");
        Assert.Contains("harm giant rat", h.AllSent);

        h.State.InCombat = true;

        // Simulate 10 rounds of a losing fight: HP ticks down (varied each round
        // so CastingDirector's stale-repeat guard doesn't suppress the heal),
        // the self-heal fires and interrupts the swing (*Combat Off*), and the
        // engine must resume. Real damage isn't modelled — the point is whether
        // the attack spell gets re-announced (via the same-round resume) while a
        // heal fires each round on its own independent slot, not whether the fight
        // is won.
        int hp = 60;
        for (int round = 0; round < 10; round++)
        {
            h.Tick();
            hp = hp switch { > 50 => hp - 7, _ => hp + 3 };   // stays under the 80% trigger, keeps changing
            h.State.Hp = hp;
            h.Feed("*Combat Off*");
        }

        int attackSpellSends = h.AllSent.Count(s => s == "harm giant rat");
        int healSends = h.AllSent.Count(s => s == "mihe");

        Assert.True(healSends > 3, $"expected the self-heal to fire repeatedly, got {healSends}");
        Assert.True(attackSpellSends > 1,
            $"attack spell was re-announced only {attackSpellSends} time(s) across 10 rounds of " +
            $"self-heal interrupts (heals sent: {healSends}) — combat is starved by the heal " +
            "winning every round's single cast slot. Sent: " + string.Join(" | ", h.AllSent));
    }

    // The between-round survival cast and the combat attack are INDEPENDENT slots
    // (GAME_MECHANICS.md): a heal can fire EVERY round, and the attack spell
    // re-announces same-round on the heal's *Combat Off* resume — so across a
    // losing fight the attack keeps going out between heals rather than being
    // starved. Here HP oscillates (so the self-heal's own stale-repeat guard never
    // suppresses it) and a heal is due every round; the invariant is that the
    // attack re-announces between heals (no two heals land back-to-back with no
    // attack resume between them). This is NOT an attack-owed alternation gate on
    // the between-round slot — that coupling was removed; the interleaving comes
    // from the resume, not from the heal sitting out a round.
    [Fact]
    public void SpellsFirst_HealFiresEveryRound_AttackReAnnouncesBetweenHeals()
    {
        using Harness h = new();
        h.CombatSettings.ActionOrder = CombatActionOrder.SpellsFirst;
        h.CombatSettings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 0 };
        h.Spells.MinorHealSpell = "mihe";
        h.Health.MinorHealCombatTrigger = 80;   // heals whenever HP <= 80% — true after almost any hit
        h.AddMonster(1, "giant rat");

        h.State.MaxHp = 100;
        h.State.HasPromptData = true;
        h.State.Hp = 100;

        h.Feed("Also here: giant rat.");
        Assert.Equal("harm giant rat", h.AllSent.Last());
        h.State.InCombat = true;

        int hp = 50;   // parked under the 80% trigger for every round below
        for (int round = 0; round < 8; round++)
        {
            h.Tick();
            hp = hp == 50 ? 55 : 50;   // oscillates so the heal is never a stale repeat
            h.State.Hp = hp;
            h.Feed("*Combat Off*");
        }

        List<string> combatCasts = h.AllSent
            .Where(s => s is "harm giant rat" or "mihe")
            .ToList();

        for (int i = 1; i < combatCasts.Count; i++)
        {
            Assert.False(combatCasts[i - 1] == "mihe" && combatCasts[i] == "mihe",
                "two self-heals fired back to back with no attack spell in between — " +
                "the attack's round was skipped. Sequence: " + string.Join(" | ", combatCasts));
        }
    }
}
