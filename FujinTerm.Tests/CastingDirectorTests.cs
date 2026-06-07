using FujinTerm.Game;
using FujinTerm.Game.Spells;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.D — <see cref="CastingDirector"/> three-tier decision flow:
/// life-threat immediate, routine-tick gating, and the master enable.
/// </summary>
public sealed class CastingDirectorTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public PlayerState State { get; } = new();
        public CastCoordinator Cast { get; }
        public CastingDirector Director { get; }
        public List<string> CastsSent { get; } = new();
        public SpellsSettings Spells { get; set; } = new();
        public HealthSettings Health { get; set; } = new();
        public bool AutoHealRestEnabled { get; set; } = true;

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Cast = new CastCoordinator(Router, Log);
            Cast.SetWireSender(_ => { /* swallow */ });
            Cast.CastSent += CastsSent.Add;
            Director = new CastingDirector(State, Cast,
                readSpells: () => Spells,
                readHealth: () => Health,
                isEnabled: () => AutoHealRestEnabled,
                log: Log);
        }

        /// <summary>Mirror PromptParser's write order so HasPromptData
        /// flips last and the engine doesn't see a 0/0 race.</summary>
        public void SetPrompt(int hp, int maxHp, int ma = 0, int maxMa = 0,
                              bool inCombat = false,
                              PlayerPosition position = PlayerPosition.Standing)
        {
            State.Hp = hp;
            State.MaxHp = maxHp;
            State.Ma = ma;
            State.MaxMa = maxMa;
            State.Position = position;
            State.InCombat = inCombat;
            State.HasPromptData = true;
        }

        public void Dispose()
        {
            Director.Dispose();
            Cast.Dispose();
        }
    }

    // ----- Tier 1: life-threat ----------------------------------------

    [Fact]
    public void LifeThreat_CastsMajorHeal_OnHpChange()
    {
        using Harness h = new();
        h.Spells.MajorHealSpell = "fullheal";
        h.Health.MajorHealCombatTrigger = 40;

        h.SetPrompt(hp: 30, maxHp: 100, inCombat: true);    // 30% < 40%

        Assert.Single(h.CastsSent);
        Assert.Equal("c fullheal", h.CastsSent[0]);
    }

    [Fact]
    public void LifeThreat_FallsBackToMinor_WhenNoMajorConfigured()
    {
        using Harness h = new();
        h.Spells.MinorHealSpell = "heal";        // no major configured
        h.Health.MajorHealCombatTrigger = 40;

        h.SetPrompt(hp: 30, maxHp: 100, inCombat: true);

        Assert.Single(h.CastsSent);
        Assert.Equal("c heal", h.CastsSent[0]);
    }

    [Fact]
    public void LifeThreat_NoSpellConfigured_NoCast()
    {
        using Harness h = new();
        // Neither MinorHealSpell nor MajorHealSpell set.
        h.Health.MajorHealCombatTrigger = 40;

        h.SetPrompt(hp: 30, maxHp: 100, inCombat: true);

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void LifeThreat_AboveThreshold_NoCast()
    {
        using Harness h = new();
        h.Spells.MajorHealSpell = "fullheal";
        h.Health.MajorHealCombatTrigger = 40;
        h.Health.MinorHealCombatTrigger = 70;

        // 80% — above major-threat but also above minor combat trigger.
        h.SetPrompt(hp: 80, maxHp: 100, inCombat: true);
        Assert.Empty(h.CastsSent);
    }

    // ----- Tier 3: routine in-combat heal -----------------------------

    [Fact]
    public void RoutineCombat_CastsMinorHeal_OnTick()
    {
        using Harness h = new();
        h.Spells.MinorHealSpell = "heal";
        h.Health.MinorHealCombatTrigger = 70;
        h.Health.MajorHealCombatTrigger = 40;

        h.SetPrompt(hp: 65, maxHp: 100, inCombat: true);    // 65% < 70%
        // Hp PropertyChanged is non-tick-driven — Tier 3 only fires on tick.
        Assert.Empty(h.CastsSent);

        h.Director.OnCombatTick();
        Assert.Single(h.CastsSent);
        Assert.Equal("c heal", h.CastsSent[0]);
    }

    [Fact]
    public void RoutineCombat_NoCast_WhenAboveThreshold()
    {
        using Harness h = new();
        h.Spells.MinorHealSpell = "heal";
        h.Health.MinorHealCombatTrigger = 70;

        h.SetPrompt(hp: 90, maxHp: 100, inCombat: true);
        h.Director.OnCombatTick();
        Assert.Empty(h.CastsSent);
    }

    // ----- Tier 3: routine rest-time heal -----------------------------

    [Fact]
    public void RoutineRest_CastsMinorHeal_WhenResting()
    {
        using Harness h = new();
        h.Spells.MinorHealSpell = "heal";
        h.Health.HealRestTrigger = 80;

        h.SetPrompt(hp: 70, maxHp: 100,
            inCombat: false, position: PlayerPosition.Resting);
        h.Director.OnCombatTick();

        Assert.Single(h.CastsSent);
        Assert.Equal("c heal", h.CastsSent[0]);
    }

    [Fact]
    public void RoutineRest_NoCast_WhenStanding()
    {
        // HP is low and we're out of combat, but we're not resting —
        // walking between rooms shouldn't auto-heal. Tier 1 still
        // fires in life-threat range but that's a different trigger.
        using Harness h = new();
        h.Spells.MinorHealSpell = "heal";
        h.Health.HealRestTrigger = 80;
        h.Health.MajorHealCombatTrigger = 40;

        h.SetPrompt(hp: 70, maxHp: 100, inCombat: false,
            position: PlayerPosition.Standing);
        h.Director.OnCombatTick();

        Assert.Empty(h.CastsSent);
    }

    // ----- gating -----------------------------------------------------

    [Fact]
    public void Disabled_NoCast()
    {
        using Harness h = new() { AutoHealRestEnabled = false };
        h.Spells.MajorHealSpell = "fullheal";
        h.Health.MajorHealCombatTrigger = 40;

        h.SetPrompt(hp: 20, maxHp: 100, inCombat: true);

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void NoPromptData_NoCast()
    {
        using Harness h = new();
        h.Spells.MajorHealSpell = "fullheal";
        // Set values WITHOUT flipping HasPromptData.
        h.State.MaxHp = 100;
        h.State.Hp = 20;

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void Dead_NoCast()
    {
        using Harness h = new();
        h.Spells.MajorHealSpell = "fullheal";
        // Set values WITHOUT flipping HasPromptData (since Hp=0 +
        // HasPromptData=true would also be gated).
        h.State.MaxHp = 100;
        h.State.Hp = 0;
        h.State.HasPromptData = true;

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void CastBlocked_NoCast()
    {
        using Harness h = new();
        h.Spells.MajorHealSpell = "fullheal";
        h.Health.MajorHealCombatTrigger = 40;

        // Force the coordinator into the blocked state via a server
        // failure line.
        h.Router.Dispatch(new LineExtractor.EmittedLine(
            "You do not have enough mana to cast that spell.",
            Array.Empty<CellAttributes>(),
            DateTimeOffset.UtcNow, IsPromptLine: false));
        Assert.True(h.Cast.IsCastBlocked);

        h.SetPrompt(hp: 30, maxHp: 100, inCombat: true);
        Assert.Empty(h.CastsSent);
    }

    // ----- tier interaction -------------------------------------------

    [Fact]
    public void LifeThreat_Wins_OverRoutine()
    {
        // HP below both thresholds — life-threat (major) takes
        // precedence over routine (minor) regardless of tick.
        using Harness h = new();
        h.Spells.MajorHealSpell = "fullheal";
        h.Spells.MinorHealSpell = "heal";
        h.Health.MajorHealCombatTrigger = 40;
        h.Health.MinorHealCombatTrigger = 70;

        h.SetPrompt(hp: 30, maxHp: 100, inCombat: true);
        // PropertyChanged on Hp fired Tier 1 — no need to tick.

        Assert.Single(h.CastsSent);
        Assert.Equal("c fullheal", h.CastsSent[0]);
    }

    [Fact]
    public void RoutineHeal_RespectsCooldownAcrossTwoTicks()
    {
        // Two consecutive ticks — second should be gated by the
        // CastCoordinator's recent-cast cooldown.
        using Harness h = new();
        h.Spells.MinorHealSpell = "heal";
        h.Health.MinorHealCombatTrigger = 70;

        h.SetPrompt(hp: 60, maxHp: 100, inCombat: true);

        h.Director.OnCombatTick();
        Assert.Single(h.CastsSent);

        // Coordinator's tick also fires from outside in real wiring;
        // skip that here so the recent-cast cooldown still gates.
        h.Director.OnCombatTick();
        Assert.Single(h.CastsSent);
    }
}
