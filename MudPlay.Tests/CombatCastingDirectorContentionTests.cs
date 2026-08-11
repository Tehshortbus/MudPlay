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
            // Mirrors AppServices: CastDirector.CastFired += Combat.NoteBetweenRoundCast,
            // CastDirector.SetAttackOwedGate(() => Combat.IsSpellAttackOwed).
            Director.CastFired += Combat.NoteBetweenRoundCast;
            Director.SetAttackOwedGate(() => Combat.IsSpellAttackOwed);
        }

        public void AddMonster(int number, string name)
            => Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}",
                Name: name,
                HitYou: Array.Empty<string>(),
                HitOther: Array.Empty<string>(),
                DeathLine: new[] { $"The {name} dies." },
                ArmorBlockYou: Array.Empty<string>(),
                ArmorBlockOther: Array.Empty<string>(),
                DodgeYou: Array.Empty<string>(),
                DodgeOther: Array.Empty<string>(),
                MissYou: Array.Empty<string>(),
                MissOther: Array.Empty<string>(),
                FlavorPrefixes: Array.Empty<string>(),
                AllowNoPrefix: true,
                Links: new[] { new GameDataLink("Monsters", number) }));

        public void Feed(string line)
        {
            LineExtractor.EmittedLine emitted = new(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false);
            Router.Dispatch(emitted);
        }

        // Mirrors AppServices' tick order: Cast.OnCombatTick, then
        // CastDirector.OnCombatTick (survival casts), then Combat.OnCombatTick.
        public void Tick()
        {
            Cast.OnCombatTick();
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
        // the attack spell EVER gets re-announced across many rounds while a
        // heal keeps winning the round's cast slot, not whether the fight is won.
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

    // Reproduces the exact live transcript: "mihe, mihe, mihe, mihe" with no
    // "harm" in between, while HP recovers each round (so the self-heal's own
    // stale-repeat guard never suppresses it — this isn't that). The game allows
    // one cast per round; a survival cast spending a round the attack spell was
    // owed must be followed by that attack, not another survival cast. The cadence
    // is a fixed alternation (attack, heal-or-buff, attack, heal-or-buff, ...),
    // not something that relaxes just because HP is still below the heal trigger —
    // it always will be, immediately after ANY hit lands, for as long as nothing
    // is fighting back.
    [Fact]
    public void SpellsFirst_HealFiresEveryRound_AttackSpellAlternatesStrictly()
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
