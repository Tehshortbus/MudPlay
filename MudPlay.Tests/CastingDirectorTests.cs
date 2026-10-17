using MudPlay.Game;
using MudPlay.Game.Conditions;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// CastingDirector's decisions: life-threat heals at once, the routine tick's
// gates, buffs and party buffs, cures, and the master enable.
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
        public bool AutoBlessEnabled { get; set; } = true;

        // Cast-code → required mana. Empty by default, so the lookup
        // returns null (unknown ⇒ no affordability block) and the legacy tests
        // behave exactly as before. Populate to exercise the mana gate.
        public Dictionary<string, int> ManaCosts { get; } =
            new(StringComparer.OrdinalIgnoreCase);

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
            Director.SetManaCostLookup(
                code => ManaCosts.TryGetValue(code, out int c) ? c : null);
            Director.SetAutoBlessGate(() => AutoBlessEnabled);
        }

        // Mirror PromptParser's write order so HasPromptData
        // flips last and the engine doesn't see a 0/0 race.
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
        Assert.Equal("fullheal", h.CastsSent[0]);
    }

    [Fact]
    public void LifeThreat_FallsBackToMinor_WhenNoMajorConfigured()
    {
        using Harness h = new();
        h.Spells.MinorHealSpell = "heal";        // no major configured
        h.Health.MajorHealCombatTrigger = 40;

        h.SetPrompt(hp: 30, maxHp: 100, inCombat: true);

        Assert.Single(h.CastsSent);
        Assert.Equal("heal", h.CastsSent[0]);
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
    public void RoutineCombat_CastsMinorHeal()
    {
        // HP below MinorHealCombatTrigger while in combat → Minor heal
        // candidate is ready, fires on the prompt-driven evaluation
        // (no tick required — the cooldown layer handles cadence).
        using Harness h = new();
        h.Spells.MinorHealSpell = "heal";
        h.Health.MinorHealCombatTrigger = 70;
        h.Health.MajorHealCombatTrigger = 40;

        h.SetPrompt(hp: 65, maxHp: 100, inCombat: true);    // 65% < 70%

        Assert.Single(h.CastsSent);
        Assert.Equal("heal", h.CastsSent[0]);
    }

    [Fact]
    public void MinorHeal_PrefersHpRegenHoT_OverSingleTargetInRoutineBand()
    {
        // HP trips the minor trigger but stays above the major (life-threat)
        // trigger → cast the HP-regen HoT first instead of the instant heal.
        using Harness h = new();
        h.Spells.MinorHealSpell = "heal";
        h.Spells.HpRegenSpell = "regen";
        h.Health.MinorHealCombatTrigger = 70;
        h.Health.MajorHealCombatTrigger = 40;

        h.SetPrompt(hp: 60, maxHp: 100, inCombat: true);    // 40 < 60 <= 70

        Assert.Single(h.CastsSent);
        Assert.Equal("regen", h.CastsSent[0]);
    }

    [Fact]
    public void MinorHeal_LifeThreatBand_UsesInstantHeal_NotHoT()
    {
        // Below the major trigger the HoT is NOT substituted — a slow HoT that
        // heals a round later is the wrong call when HP is critical.
        using Harness h = new();
        h.Spells.MinorHealSpell = "heal";
        h.Spells.HpRegenSpell = "regen";
        h.Health.MinorHealCombatTrigger = 70;
        h.Health.MajorHealCombatTrigger = 40;

        h.SetPrompt(hp: 30, maxHp: 100, inCombat: true);    // 30 <= 40 → life-threat

        Assert.Single(h.CastsSent);
        Assert.Equal("heal", h.CastsSent[0]);
    }

    [Fact]
    public void MajorHeal_TakesOverBelowMajorTrigger_NotMinor()
    {
        // Report paradigm-20260819-121247: below the major trigger the engine kept
        // firing minor (walked first) and never reached major — the player died.
        // With both configured + major affordable, major must take over in its band.
        using Harness h = new();
        h.Spells.MinorHealSpell = "mihe";
        h.Spells.MajorHealSpell = "mahe";
        h.Health.MinorHealCombatTrigger = 80;
        h.Health.MajorHealCombatTrigger = 70;

        h.SetPrompt(hp: 40, maxHp: 100, ma: 100, maxMa: 100, inCombat: true);  // 40 <= 70 → major band

        Assert.Single(h.CastsSent);
        Assert.Equal("mahe", h.CastsSent[0]);
    }

    [Fact]
    public void MinorHeal_FallsBackInMajorBand_WhenMajorUnaffordable()
    {
        // In the major band but can't pay for the major heal → minor still fires
        // (the yield is affordability-gated), rather than healing nothing.
        using Harness h = new();
        h.Spells.MinorHealSpell = "mihe";
        h.Spells.MajorHealSpell = "mahe";
        h.Health.MinorHealCombatTrigger = 80;
        h.Health.MajorHealCombatTrigger = 70;
        h.ManaCosts["mahe"] = 60;   // major costs more than the pool below
        h.ManaCosts["mihe"] = 10;

        h.SetPrompt(hp: 40, maxHp: 100, ma: 30, maxMa: 100, inCombat: true);  // ma 30 < 60 → can't afford major

        Assert.Single(h.CastsSent);
        Assert.Equal("mihe", h.CastsSent[0]);
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
        Assert.Equal("heal", h.CastsSent[0]);
    }

    // In a room whose own spell does damage no rest is started (user, 2026-10-09:
    // "we should heal but not actively try to rest in a room like this, because
    // it'll kill us if we dont have heals"). The rest-time heal is cast only while
    // resting, so it has to go out standing while that rest is owed.
    [Fact]
    public void RoutineRest_CastsMinorHealStanding_WhileTheRestIsDeferredForARoomSpell()
    {
        using Harness h = new();
        bool deferred = true;
        h.Director.SetRestDeferredGate(() => deferred);
        h.Spells.MinorHealSpell = "heal";
        h.Health.HealRestTrigger = 80;

        h.SetPrompt(hp: 70, maxHp: 100, inCombat: false, position: PlayerPosition.Standing);
        h.Director.OnCombatTick();
        Assert.Equal(new[] { "heal" }, h.CastsSent);

        // Above the heal trigger nothing is cast, deferred rest or not.
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();
        h.SetPrompt(hp: 85, maxHp: 100, inCombat: false, position: PlayerPosition.Standing);
        h.Director.OnCombatTick();
        Assert.Empty(h.CastsSent);

        // Out of the room, or healed past the rest trigger: back to no heal mid-walk.
        deferred = false;
        h.SetPrompt(hp: 70, maxHp: 100, inCombat: false, position: PlayerPosition.Standing);
        h.Director.OnCombatTick();
        Assert.Empty(h.CastsSent);
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
        // A prompt reading 0 HP: dead, and nothing is cast on a corpse.
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
    public void MajorTakesPrecedence_InMajorBand_EvenAtDefaultPriority()
    {
        // Default priority walks Minor (3) before Major (4), but severity wins:
        // once HP is in the major band the minor slot YIELDS to major, so major
        // fires without the user having to re-order priorities (report
        // paradigm-20260819-121247). HP=30% is below BOTH triggers (Minor 70%,
        // Major 40%).
        using Harness h = new();
        h.Spells.MajorHealSpell = "fullheal";
        h.Spells.MinorHealSpell = "heal";
        h.Health.MajorHealCombatTrigger = 40;
        h.Health.MinorHealCombatTrigger = 70;

        h.SetPrompt(hp: 30, maxHp: 100, ma: 100, maxMa: 100, inCombat: true);

        Assert.Single(h.CastsSent);
        Assert.Equal("fullheal", h.CastsSent[0]);
    }

    [Fact]
    public void CustomPriority_MajorBeforeMinor_LifeThreatFiresMajor()
    {
        // User reordered priorities: Major=3, Minor=4. Now Major
        // wins when both candidates qualify.
        using Harness h = new();
        h.Spells.MajorHealSpell = "fullheal";
        h.Spells.MinorHealSpell = "heal";
        h.Spells.PriorityMajorSelfHeal = 3;
        h.Spells.PriorityMinorSelfHeal = 4;
        h.Health.MajorHealCombatTrigger = 40;
        h.Health.MinorHealCombatTrigger = 70;

        h.SetPrompt(hp: 30, maxHp: 100, inCombat: true);

        Assert.Single(h.CastsSent);
        Assert.Equal("fullheal", h.CastsSent[0]);
    }

    // ----- mana affordability gate ------------------------------------

    [Fact]
    public void InsufficientMana_SkipsHeal_NoCast()
    {
        // Major heal would fire (HP 30% < 40% threshold) but costs 50 mana
        // and we only have 10 — don't even attempt it.
        using Harness h = new();
        h.Spells.MajorHealSpell = "fullheal";
        h.Health.MajorHealCombatTrigger = 40;
        h.ManaCosts["fullheal"] = 50;

        h.SetPrompt(hp: 30, maxHp: 100, ma: 10, maxMa: 100, inCombat: true);

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void SufficientMana_CastsHeal()
    {
        // Same heal, enough mana to pay for it → fires.
        using Harness h = new();
        h.Spells.MajorHealSpell = "fullheal";
        h.Health.MajorHealCombatTrigger = 40;
        h.ManaCosts["fullheal"] = 50;

        h.SetPrompt(hp: 30, maxHp: 100, ma: 60, maxMa: 100, inCombat: true);

        Assert.Single(h.CastsSent);
        Assert.Equal("fullheal", h.CastsSent[0]);
    }

    [Fact]
    public void UnaffordableHigherPriority_FallsToCheaperLowerPriority()
    {
        // User ranks Major above Minor. At 30% HP both qualify, but Major
        // ("fullheal", 50) is unaffordable with 20 mana — skip-and-continue
        // lets the cheaper Minor ("heal", 10) fire instead.
        using Harness h = new();
        h.Spells.MajorHealSpell = "fullheal";
        h.Spells.MinorHealSpell = "heal";
        h.Spells.PriorityMajorSelfHeal = 3;
        h.Spells.PriorityMinorSelfHeal = 4;
        h.Health.MajorHealCombatTrigger = 40;
        h.Health.MinorHealCombatTrigger = 70;
        h.ManaCosts["fullheal"] = 50;
        h.ManaCosts["heal"] = 10;

        h.SetPrompt(hp: 30, maxHp: 100, ma: 20, maxMa: 100, inCombat: true);

        Assert.Single(h.CastsSent);
        Assert.Equal("heal", h.CastsSent[0]);
    }

    [Fact]
    public void UnknownCost_DoesNotBlockCast()
    {
        // No cost registered for the spell → lookup returns null → the gate
        // never blocks, preserving pre-mana-gate behaviour.
        using Harness h = new();
        h.Spells.MajorHealSpell = "fullheal";
        h.Health.MajorHealCombatTrigger = 40;

        h.SetPrompt(hp: 30, maxHp: 100, ma: 0, maxMa: 100, inCombat: true);

        Assert.Single(h.CastsSent);
        Assert.Equal("fullheal", h.CastsSent[0]);
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

    // ----- heal mana-floor gate (HealIfAboveMa*) --------------------

    [Fact]
    public void HealManaFloor_Resting_BelowFloor_SuppressesHeal()
    {
        // Resting heal would fire (HP 70 < HealRestTrigger 80) but MA sits
        // at 30% — below the 50% rest floor — so the heal is held to let the
        // pool regenerate.
        using Harness h = new();
        h.Spells.MinorHealSpell = "heal";
        h.Health.HealRestTrigger = 80;
        h.Health.HealIfAboveMaResting = 50;

        h.SetPrompt(hp: 70, maxHp: 100, ma: 30, maxMa: 100,
            inCombat: false, position: PlayerPosition.Resting);
        h.Director.OnCombatTick();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void HealManaFloor_Resting_AboveFloor_CastsHeal()
    {
        // Same setup with MA at 80% — clears the 50% floor → heal fires.
        using Harness h = new();
        h.Spells.MinorHealSpell = "heal";
        h.Health.HealRestTrigger = 80;
        h.Health.HealIfAboveMaResting = 50;

        h.SetPrompt(hp: 70, maxHp: 100, ma: 80, maxMa: 100,
            inCombat: false, position: PlayerPosition.Resting);
        h.Director.OnCombatTick();

        Assert.Single(h.CastsSent);
        Assert.Equal("heal", h.CastsSent[0]);
    }

    [Fact]
    public void HealManaFloor_Combat_ZeroFloor_IgnoresRestFloor()
    {
        // In combat with the default combat floor (0) — the rest floor (50)
        // does NOT apply, so a low MA pool (10%) still heals.
        using Harness h = new();
        h.Spells.MinorHealSpell = "heal";
        h.Health.MinorHealCombatTrigger = 70;
        h.Health.HealIfAboveMaResting = 50;
        h.Health.HealIfAboveMaCombat = 0;

        h.SetPrompt(hp: 65, maxHp: 100, ma: 10, maxMa: 100, inCombat: true);

        Assert.Single(h.CastsSent);
        Assert.Equal("heal", h.CastsSent[0]);
    }

    [Fact]
    public void HealManaFloor_Combat_BelowFloor_SuppressesHeal()
    {
        // A non-zero combat floor (60%) gates combat heals too — MA 30% is
        // below it, so the routine combat heal is held.
        using Harness h = new();
        h.Spells.MinorHealSpell = "heal";
        h.Health.MinorHealCombatTrigger = 70;
        h.Health.HealIfAboveMaCombat = 60;

        h.SetPrompt(hp: 65, maxHp: 100, ma: 30, maxMa: 100, inCombat: true);

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void HealManaFloor_AbsoluteMode_ComparesRawMa()
    {
        // Absolute MA mode: the floor is a raw MA value, not a percent.
        // MA 30 < floor 40 → suppressed.
        using Harness h = new();
        h.Spells.MinorHealSpell = "heal";
        h.Health.HealRestTrigger = 80;
        h.Health.MaThresholdMode = ThresholdMode.Absolute;
        h.Health.HealIfAboveMaResting = 40;

        h.SetPrompt(hp: 70, maxHp: 100, ma: 30, maxMa: 100,
            inCombat: false, position: PlayerPosition.Resting);
        h.Director.OnCombatTick();
        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void HealManaFloor_UnknownPool_DoesNotBlock()
    {
        // Percentage mode but MaxMa is 0 (no prompt MA data / no pool) — the
        // gate can't evaluate a percent, so it never blocks the safety path.
        using Harness h = new();
        h.Spells.MinorHealSpell = "heal";
        h.Health.HealRestTrigger = 80;
        h.Health.HealIfAboveMaResting = 50;

        h.SetPrompt(hp: 70, maxHp: 100, ma: 0, maxMa: 0,
            inCombat: false, position: PlayerPosition.Resting);
        h.Director.OnCombatTick();

        Assert.Single(h.CastsSent);
        Assert.Equal("heal", h.CastsSent[0]);
    }

    // ----- Emergency self-heal ----------------------------------------

    [Fact]
    public void EmergencyHeal_OverridesMajorAndMinor_AtDefaultPriority()
    {
        // HP is in EVERY band at once (below minor, major, and emergency
        // triggers) — Emergency's own spell must win, not Major or Minor,
        // because Emergency defaults to priority slot 1 (leads the queue).
        using Harness h = new();
        h.Spells.MinorHealSpell = "heal";
        h.Spells.MajorHealSpell = "fullheal";
        h.Spells.EmergencyHealSpell = "lastresort";
        h.Health.MinorHealCombatTrigger = 70;
        h.Health.MajorHealCombatTrigger = 40;
        h.Health.EmergencyHealTrigger = 20;

        h.SetPrompt(hp: 15, maxHp: 100, ma: 100, maxMa: 100, inCombat: true);

        Assert.Single(h.CastsSent);
        Assert.Equal("lastresort", h.CastsSent[0]);
    }

    [Fact]
    public void EmergencyHeal_IsReorderable_DemotedBelowMajor_MajorFiresFirst()
    {
        // Emergency is now an ordinary priority row, not a hardcoded always-lead.
        // Rank it below Major and Major wins even in the emergency band — proving
        // the position is genuinely user-controlled.
        using Harness h = new();
        h.Spells.EmergencyHealSpell = "lastresort";
        h.Spells.MajorHealSpell = "fullheal";
        h.Spells.MinorHealSpell = "heal";
        h.Spells.PriorityEmergencyHeal = 9;   // demoted to the back
        h.Spells.PriorityMajorSelfHeal = 1;   // Major promoted to lead
        h.Health.MajorHealCombatTrigger = 40;
        h.Health.EmergencyHealTrigger = 20;

        h.SetPrompt(hp: 15, maxHp: 100, ma: 100, maxMa: 100, inCombat: true);

        Assert.Single(h.CastsSent);
        Assert.Equal("fullheal", h.CastsSent[0]);
    }

    [Fact]
    public void EmergencyHeal_WinsTiebreak_WhenPriorityCollidesWithMajorSelfHeal()
    {
        // Report paradigm-20260915-093211: a profile whose MajorSelfHeal rank
        // was set to slot 1 before Emergency existed still has that value after
        // upgrading, colliding with Emergency's own default slot 1. The tiebreak
        // (SpellCategory's declaration order, not the raw priority ints) must
        // still resolve to Emergency, not silently fall through to Major.
        using Harness h = new();
        h.Spells.MajorHealSpell = "fullheal";
        h.Spells.EmergencyHealSpell = "lastresort";
        h.Spells.PriorityMajorSelfHeal = h.Spells.PriorityEmergencyHeal;   // collision
        h.Health.MajorHealCombatTrigger = 40;
        h.Health.EmergencyHealTrigger = 20;

        h.SetPrompt(hp: 15, maxHp: 100, ma: 100, maxMa: 100, inCombat: true);

        Assert.Single(h.CastsSent);
        Assert.Equal("lastresort", h.CastsSent[0]);
    }

    [Fact]
    public void EmergencyHeal_FallsBackToMajor_WhenEmergencyNotConfigured()
    {
        using Harness h = new();
        h.Spells.MinorHealSpell = "heal";
        h.Spells.MajorHealSpell = "fullheal";       // no EmergencyHealSpell set
        h.Health.EmergencyHealTrigger = 20;

        h.SetPrompt(hp: 15, maxHp: 100, inCombat: true);

        Assert.Single(h.CastsSent);
        Assert.Equal("fullheal", h.CastsSent[0]);
    }

    [Fact]
    public void EmergencyHeal_FallsBackToMinor_WhenNeitherEmergencyNorMajorConfigured()
    {
        using Harness h = new();
        h.Spells.MinorHealSpell = "heal";           // no Emergency, no Major
        h.Health.EmergencyHealTrigger = 20;

        h.SetPrompt(hp: 15, maxHp: 100, inCombat: true);

        Assert.Single(h.CastsSent);
        Assert.Equal("heal", h.CastsSent[0]);
    }

    [Fact]
    public void EmergencyHeal_AboveThreshold_NoCast()
    {
        using Harness h = new();
        h.Spells.EmergencyHealSpell = "lastresort";
        h.Health.EmergencyHealTrigger = 20;

        h.SetPrompt(hp: 25, maxHp: 100, inCombat: true);   // 25% > 20% trigger

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void EmergencyHeal_IgnoresManaFloor_UnlikeMinorAndMajor()
    {
        // The same MA-floor setup that HealManaFloor_Combat_BelowFloor_SuppressesHeal
        // proves suppresses Minor — Emergency must NOT be gated by it. An
        // emergency spends whatever mana is left rather than conserving the
        // pool for a "later" that might not come.
        using Harness h = new();
        h.Spells.EmergencyHealSpell = "lastresort";
        h.Health.EmergencyHealTrigger = 20;
        h.Health.HealIfAboveMaCombat = 60;

        h.SetPrompt(hp: 15, maxHp: 100, ma: 5, maxMa: 100, inCombat: true);   // MA 5% << 60% floor

        Assert.Single(h.CastsSent);
        Assert.Equal("lastresort", h.CastsSent[0]);
    }

    [Fact]
    public void EmergencyHeal_FiresOutsideCombatAndRest_UnlikeMinor()
    {
        // Minor heal's own gate refuses to fire mid-walk (InCombat false AND
        // not Resting) — Emergency has no such restriction, it fires in ANY
        // state. Standing (not resting, not in combat) is exactly the state
        // Minor would refuse.
        using Harness h = new();
        h.Spells.EmergencyHealSpell = "lastresort";
        h.Health.EmergencyHealTrigger = 20;

        h.SetPrompt(hp: 15, maxHp: 100, inCombat: false, position: PlayerPosition.Standing);
        h.Director.OnCombatTick();

        Assert.Single(h.CastsSent);
        Assert.Equal("lastresort", h.CastsSent[0]);
    }

    [Fact]
    public void EmergencyHeal_RespectsHardAffordabilityGate()
    {
        // Emergency bypasses the CONSERVATION floor (HealIfAboveMa*) but still
        // can't cast a spell it genuinely can't pay for — the game would just
        // refuse it. The generic post-Pick mana-cost check in Evaluate applies
        // to every category alike, Emergency included.
        using Harness h = new();
        h.Spells.EmergencyHealSpell = "lastresort";
        h.Health.EmergencyHealTrigger = 20;
        h.ManaCosts["lastresort"] = 50;

        h.SetPrompt(hp: 15, maxHp: 100, ma: 10, maxMa: 100, inCombat: true);   // can't afford 50

        Assert.Empty(h.CastsSent);
    }

    // ----- Tier 2 cures (game-data Messages driven) -----------------

    private sealed class CureHarness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public PlayerState State { get; } = new();
        public MessageStore Messages { get; } = new();
        public CastCoordinator Cast { get; }
        public ConditionTracker Conditions { get; }
        // The game's output, fed the way the client receives it.
        public ServerFeed Server { get; } = new();
        public CastingDirector Director { get; }
        public List<string> CastsSent { get; } = new();
        public List<string> SelfBuffCast { get; } = new();
        public SpellsSettings Spells { get; set; } = new();
        public HealthSettings Health { get; set; } = new();
        public bool AutoBlessEnabled { get; set; } = true;
        public bool AutoHealRestEnabled { get; set; } = true;

        // Reported to the director as the combat-tick source. When true, an
        // CastingDirector.OnCombatTick pass treats HP as unconfirmed for
        // the round and holds the non-heal categories (cure / buff / debuff).
        public bool CombatTickDamageDriven { get; set; }

        // Runs on an HP change BEFORE the director's own handler — the app's
        // party self row is subscribed to PlayerState first and re-enters Evaluate
        // through its HpPercent change.
        public Action? BeforeDirectorSeesHp { get; set; }

        // Settled passes the director queued (delay, callback). Only
        // captured after EnableBurstSettle — the burst-settle hold is
        // off without a scheduler, so the rest of the suite keeps its reactive
        // same-pass behaviour.
        public List<(TimeSpan Delay, Action Run)> SettledPasses { get; } = new();

        public void EnableBurstSettle() =>
            Director.SetSettledPassScheduler((delay, run) => SettledPasses.Add((delay, run)));

        // Advance the clock past the queued settle delay and run the
        // settled pass, as the one-shot timer would.
        public void RunSettledPass()
        {
            (TimeSpan delay, Action run) = SettledPasses[^1];
            SettledPasses.Clear();
            Now += delay;
            run();
        }

        // Extra unified-list buffs (party / member / whole-party) beyond the
        // self-bless the tests set via Spells. Rarely used here.
        public BuffSettings PartyBuffs { get; } = new();

        // When true the triggered-rest gate reports an active recovery
        // rest (HP/MA below rest-if-below), so the bless "while resting" gate
        // engages. A bare Position=Resting does NOT set this — idle resting still
        // buffs.
        public bool TriggeredRest { get; set; }

        // When true the buff-strip-room gate reports the current room
        // removes buffs on entry, so the Buffing category is suppressed.
        public bool BuffStripRoom { get; set; }

        // When true the sneak-maintenance defer gate reports we're a
        // stealth runner walking combat-off through an occupied room, so the
        // Buffing / Curing categories are held for the next empty room (unless
        // resting / meditating).
        public bool DeferMaintenanceForStealth { get; set; }

        // Test clock — buff-expiry math reads this so tests can
        // advance time deterministically.
        public DateTime Now { get; set; } =
            new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Buff cast-code → (caster template, duration seconds).
        // Populate to give a self-buff a duration timer; an unmapped code
        // resolves to null (no timer ⇒ always due ⇒ re-attempt each pass).
        // In this harness the confirmed condition's Name doubles as the
        // buff short, so this keys on the condition Name.
        public Dictionary<string, (string Caster, long Duration)> BuffInfo { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        // Buff short → the shorts its spell REMOVES (RemovesSpell). Lets a
        // test model a clobber (chan removes gbls) so the landing-clears-victim path
        // runs.
        public Dictionary<string, string[]> Removes { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public CureHarness()
        {
            DefaultPatterns.Seed(Router);
            Cast = new CastCoordinator(Router, Log);
            Cast.SetWireSender(_ => { });
            Cast.CastSent += CastsSent.Add;
            Conditions = new ConditionTracker(Messages, Log);
            Conditions.AttachLineExtractor(Server.Lines);
            State.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PlayerState.Hp)) BeforeDirectorSeesHp?.Invoke();
            };
            Director = new CastingDirector(State, Cast, Conditions,
                readSpells: () => Spells,
                readHealth: () => Health,
                isEnabled: () => AutoHealRestEnabled,
                log: Log,
                now: () => Now);
            Director.SetAutoBlessGate(() => AutoBlessEnabled);
            Director.SetTriggeredRestGate(() => TriggeredRest);
            Director.SetBuffStripRoomGate(() => BuffStripRoom);
            Director.SetStealthMaintenanceDeferGate(() => DeferMaintenanceForStealth);
            Director.SetCombatTickSource(() => CombatTickDamageDriven);
            Director.SetPartyBuffSource(() => PartyBuffs);
            // Self-buff confirmation: the condition's Name is the buff short
            // in this harness, so map a fired record back to its Name and
            // resolve its duration from BuffInfo.
            Director.SetBuffDurationSources(
                code => BuffInfo.TryGetValue(code, out (string Caster, long Duration) info)
                    ? (info.Caster, info.Duration)
                    : null,
                record => record.Name,
                removesShortsFor: code =>
                    Removes.TryGetValue(code, out string[]? v) ? v : System.Array.Empty<string>());
            // Capture the reroll sink so a test can assert a self-buff CAST is
            // reported to the mana-regen reroll engine (fired from the send path).
            Director.SetSelfBuffCastSink(SelfBuffCast.Add);
            // Healthy baseline so Tier-1 doesn't fire over the cure path.
            State.MaxHp = 200;
            State.Hp = 200;
            State.HasPromptData = true;
        }

        // The buffs' own conditions, set on every slot in the list and on each one
        // SelfBuff adds after. Many tests give every buff the same mana floor or rest
        // and combat switches; the Buffs tab sets them per slot.
        private int _manaFloor = 70;
        private bool _whileResting, _duringCombat;
        public int ManaFloor { get => _manaFloor; set { _manaFloor = value; foreach (BuffSlot b in PartyBuffs.Slots) b.BlessIfAboveMa = value; } }
        public bool WhileResting { get => _whileResting; set { _whileResting = value; foreach (BuffSlot b in PartyBuffs.Slots) b.BlessWhileResting = value; } }
        public bool DuringCombat { get => _duringCombat; set { _duringCombat = value; foreach (BuffSlot b in PartyBuffs.Slots) b.BlessDuringCombat = value; } }

        // A buff cast on ourselves, added to the list as the Buffs tab adds one.
        public BuffSlot SelfBuff(string spell)
        {
            BuffSlot slot = new()
            {
                Spell = spell, CastOnSelf = true,
                BlessIfAboveMa = _manaFloor, BlessWhileResting = _whileResting, BlessDuringCombat = _duringCombat,
            };
            PartyBuffs.Slots.Add(slot);
            return slot;
        }

        public void RecordCondition(string name, MessageFlags flags,
                                    string applied, string endsWith = "")
        {
            Messages.Messages.Add(new MessageRecord(
                Id: MessageRecord.ComputeId(name, "", "", "", applied, endsWith),
                Name: name,
                Flags: flags,
                RawFlagsHex: (ushort)flags,
                CasterMessage: string.Empty,
                TargetMessage: string.Empty,
                WitnessMessage: string.Empty,
                AppliedMessage: applied,
                AppliedEndsWith: endsWith));
        }

        public void FeedLine(string text) => Server.Line(text);

        public void Dispose()
        {
            Director.Dispose();
            Cast.Dispose();
            Conditions.Dispose();
        }
    }

    [Fact]
    public void Cure_Poisoned_CastsCurePoison()
    {
        using CureHarness h = new();
        h.Spells.CurePoisonSpell = "neutralize";
        h.RecordCondition("Poison", MessageFlags.Poisoned, "poisoned!");

        h.FeedLine("You have been poisoned!");

        Assert.Single(h.CastsSent);
        Assert.Equal("neutralize", h.CastsSent[0]);
    }

    [Fact]
    public void SelfBuffCast_NotifiesTheRerollSink()
    {
        using CureHarness h = new();
        // The reroll sink fires when a self-buff is CAST (from the send path), not on
        // its applied-line confirm — a roll spell confirms via a shared condition that
        // can't be mapped back to it (paradigm-20260830-110918). Configure a mana-regen
        // self-buff and drive a between-round pass: casting it must report to the sink.
        h.SelfBuff("ntap");
        h.BuffInfo["ntap"] = ("You tap into the mana around you.", 300);
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Standing;

        h.Director.Evaluate();

        Assert.Contains("ntap", h.CastsSent);
        Assert.Contains("ntap", h.SelfBuffCast);
    }

    [Fact]
    public void SelfCure_StuckFlag_IsThrottled_NotCastEveryRound()
    {
        // A stuck condition flag (a cure that can't clear the source, or a falsely-latched
        // flag) would re-fire the cure every round and drain mana to zero — reports
        // paradigm-20260922-063422 / -095321. The throttle caps re-casts of the same
        // self-cure to once per SelfCureRetryWindow; a working cure clears the flag first.
        using CureHarness h = new();
        h.Spells.CureBlindnessSpell = "site";
        h.RecordCondition("blind", MessageFlags.Blinded, applied: "You are blinded!");
        h.FeedLine("You are blinded!");        // IsBlinded latches (no wear-off ever comes)

        h.Director.Evaluate();
        Assert.Contains("site", h.CastsSent);
        int afterFirst = h.CastsSent.Count;

        // Same round-ish, still blinded → throttled, no fresh cure cast.
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();
        h.Director.Evaluate();
        Assert.Equal(afterFirst, h.CastsSent.Count);

        // Past the retry window → the cure retries once (in case the first cast failed).
        h.Now = h.Now.AddSeconds(20);
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();
        h.Director.Evaluate();
        Assert.Equal(afterFirst + 1, h.CastsSent.Count);
    }

    [Fact]
    public void Cure_MovementPrevented_CastsCureHolds()
    {
        using CureHarness h = new();
        h.Spells.CureHoldsSpell = "freedom";
        h.RecordCondition("Paralyze", MessageFlags.MovementPrevented, "paralyzed!");

        h.FeedLine("You have been paralyzed!");

        Assert.Single(h.CastsSent);
        Assert.Equal("freedom", h.CastsSent[0]);
    }

    [Fact]
    public void AttackPrevented_HoldsBetweenRoundHeal_ThenFiresOnceItClears()
    {
        // Stun refuses a between-round self-heal too, not just attacks (corrected
        // GAME_MECHANICS.md note): a heal due while stunned must not fire into the
        // block, and must fire immediately once the wear-off lands rather than
        // waiting out a phantom round cooldown.
        using CureHarness h = new();
        h.Spells.MinorHealSpell = "grhe";
        h.Health.MinorHealCombatTrigger = 90;
        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.RecordCondition("Stun", MessageFlags.AttackPrevented,
            "You are stunned!", "You are no longer stunned.");

        h.FeedLine("You are stunned!");
        h.State.Hp = 100;                          // due for a heal while still stunned
        Assert.Empty(h.CastsSent);

        h.FeedLine("You are no longer stunned.");  // clears — heal fires right away
        Assert.Single(h.CastsSent);
        Assert.Equal("grhe", h.CastsSent[0]);
    }

    [Fact]
    public void SendCollidesWithLateStunApply_ReleasesRoundSlot_SoRetryFiresOnceItClears()
    {
        // A between-round heal can be sent an instant before the game's own stun
        // message for the SAME round lands (the tick-driven pass that fires it runs
        // straight off the damage line, before a stun a line later in the same burst
        // has even been parsed — racing past the AttacksPrevented gate above it). The
        // cast is swallowed even though nothing about the send itself looked wrong.
        // The after-the-fact AttackPrevented apply must release the round slot instead
        // of stranding it for the full ~5.5s cooldown.
        using CureHarness h = new();
        h.Spells.MinorHealSpell = "grhe";
        h.Health.MinorHealCombatTrigger = 90;
        h.State.MaxHp = 200;
        h.State.InCombat = true;
        h.RecordCondition("Stun", MessageFlags.AttackPrevented,
            "You are stunned!", "You are no longer stunned.");

        h.State.Hp = 100;                          // due — fires immediately, unstunned
        Assert.Single(h.CastsSent);

        h.FeedLine("You are stunned!");            // lands right after the send, same burst
        h.FeedLine("You are no longer stunned.");  // clears — retry fires right away

        Assert.Equal(2, h.CastsSent.Count);
        Assert.All(h.CastsSent, c => Assert.Equal("grhe", c));
    }

    [Fact]
    public void Cure_Poisoned_MidFight_CastsWhenNoHealIsDue()
    {
        // The default: a round that needs no heal spends its cast on the cure.
        using CureHarness h = new();
        h.Spells.CurePoisonSpell = "cure";
        h.RecordCondition("Poison", MessageFlags.Poisoned, "poisoned!");
        h.State.InCombat = true;

        h.FeedLine("You have been poisoned!");
        h.Director.Evaluate();
        Assert.Equal(new[] { "cure" }, h.CastsSent);
    }

    [Fact]
    public void Cure_Poisoned_AfterCombatTicked_WaitsForTheFightToEnd()
    {
        // Report paradigm-20261004-054304 (05:35:36): cure poison went out with the
        // locust still alive, spending the round's cast; its next bite re-poisons.
        using CureHarness h = new();
        h.Spells.CurePoisonSpell = "cure";
        h.Spells.CurePoisonAfterCombat = true;
        h.RecordCondition("Poison", MessageFlags.Poisoned, "poisoned!");
        h.State.InCombat = true;

        h.FeedLine("You have been poisoned!");
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);

        h.State.InCombat = false;                 // room clear — the fight is over
        Assert.Equal(new[] { "cure" }, h.CastsSent);
    }

    [Fact]
    public void Cure_KnockedDown_AfterCombatTicked_WaitsForTheFightToEnd()
    {
        // Same report (05:38:46): curp cast mid-fight after the grimhound's trample
        // knocked us down.
        using CureHarness h = new();
        h.Spells.CureHoldsSpell = "curp";
        h.Spells.CureHoldsAfterCombat = true;
        h.RecordCondition("knockdown", MessageFlags.MovementPrevented,
            "You are knocked off your feet, and land with a heavy thump!");
        h.State.InCombat = true;

        h.FeedLine("You are knocked off your feet, and land with a heavy thump!");
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);

        h.State.InCombat = false;
        Assert.Equal(new[] { "curp" }, h.CastsSent);
    }

    [Fact]
    public void Cure_Diseased_AfterCombatTicked_WaitsForTheFightToEnd()
    {
        using CureHarness h = new();
        h.Spells.CureDiseaseSpell = "cdes";
        h.Spells.CureDiseaseAfterCombat = true;
        h.RecordCondition("Disease", MessageFlags.Diseased, "diseased!");
        h.State.InCombat = true;

        h.FeedLine("You have been diseased!");
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);

        h.State.InCombat = false;
        Assert.Equal(new[] { "cdes" }, h.CastsSent);
    }

    [Fact]
    public void Cure_HeldMidFight_LetsTheHealBelowItFire()
    {
        // A held cure ranked above the heal must not hold the heal behind it.
        using CureHarness h = new();
        h.Spells.CurePoisonSpell = "cure";
        h.Spells.CurePoisonAfterCombat = true;
        h.Spells.MinorHealSpell = "grhe";
        h.Spells.PriorityCuring = 1;
        h.Health.MinorHealCombatTrigger = 70;
        h.RecordCondition("Poison", MessageFlags.Poisoned, "poisoned!");
        h.State.InCombat = true;
        h.FeedLine("You have been poisoned!");

        h.State.Hp = 120;                         // 60%: the heal is due
        Assert.Equal(new[] { "grhe" }, h.CastsSent);
    }

    [Fact]
    public void Cure_NoSpellConfigured_NoCast()
    {
        using CureHarness h = new();
        // CurePoisonSpell is null/empty.
        h.RecordCondition("Poison", MessageFlags.Poisoned, "poisoned!");

        h.FeedLine("You have been poisoned!");

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void Cure_PriorityOrder_MovementBeatsPoison()
    {
        // Both conditions are already active when we evaluate (the
        // tracker already saw both lines + ActiveFlags = paralyze |
        // poison). PickCure walks the priority list and chooses
        // CureHoldsSpell because movement-prevented is the most
        // disabling. Real-world race protection (cooldown) is tested
        // elsewhere; this isolates the priority decision.
        using CureHarness h = new();
        h.Spells.CureHoldsSpell = "freedom";
        h.Spells.CurePoisonSpell = "neutralize";
        h.RecordCondition("Paralyze", MessageFlags.MovementPrevented, "paralyzed!");
        h.RecordCondition("Poison",   MessageFlags.Poisoned,          "poisoned!");

        // Pre-load both conditions on the tracker WITHOUT triggering
        // an immediate cure cast — feed the applied lines, then
        // bounce the coordinator's cooldown so the next evaluation
        // is free to fire.
        h.FeedLine("You have been poisoned!");
        h.FeedLine("You have been paralyzed!");
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();               // clear recent-cast cooldown
        h.Director.NotifyRoundComplete();    // new round frees the one-per-round between-round slot

        // Trigger a fresh evaluation (combat tick path).
        h.Director.OnCombatTick();

        Assert.Single(h.CastsSent);
        Assert.Equal("freedom", h.CastsSent[0]);
    }

    [Fact]
    public void BetweenRoundSlot_StaysSpentAcrossCombatOffFlicker()
    {
        // Report paradigm-20260916-074131: the one-per-round between-round slot must
        // survive the brief *Combat Off* flicker between kills (InCombat=false), so a
        // freshly-arrived monster's pre-attack debuff doesn't re-fire into an
        // already-spent round and draw "already cast this round" — whose block latch
        // then delays the coupled attack a whole round. The slot used to be
        // InCombat-gated, masking it false in exactly that flicker.
        using Harness h = new();
        h.State.InCombat = true;
        h.Director.MarkBetweenRoundSlotUsed();
        Assert.True(h.Director.BetweenRoundSlotUsed);

        h.State.InCombat = false;                       // *Combat Off* between kills
        Assert.True(h.Director.BetweenRoundSlotUsed);    // still spent — was masked false before

        h.Director.NotifyRoundComplete();               // the real round tick frees it
        Assert.False(h.Director.BetweenRoundSlotUsed);
    }

    // ----- Buffing (bless slot walk) ---------------------------------

    [Fact]
    public void Buff_OutOfCombat_FiresFirstUnactiveSlot()
    {
        using CureHarness h = new();
        h.SelfBuff("bless");
        h.SelfBuff("haste");
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Standing;

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("bless", h.CastsSent[0]);
    }

    // Out of combat the combat tick doesn't free-run, so the between-round loop is
    // driven off the 1 s heartbeat instead — OnIdleHeartbeat runs the same decision
    // pass so idle buffs queue up from login instead of trickling in.
    [Fact]
    public void OnIdleHeartbeat_OutOfCombat_DrivesBetweenRoundLoop()
    {
        using CureHarness h = new();
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Standing;
        h.CastsSent.Clear();
        h.SelfBuff("bless");   // configured after state — the setup cascade can't have cast it

        h.Director.OnIdleHeartbeat();

        Assert.Contains("bless", h.CastsSent);
    }

    // Disconnected: the whole loop pauses — a due buff must not cast (the send would
    // no-op but TryCast would still arm a phantom recast timer). Reconnect resumes it.
    [Fact]
    public void Disconnected_PausesLoop_ResumesWhenReconnected()
    {
        using CureHarness h = new();
        h.Director.SetConnectedGate(() => false);   // link down
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Standing;
        h.CastsSent.Clear();
        h.SelfBuff("bless");

        h.Director.OnIdleHeartbeat();
        Assert.Empty(h.CastsSent);      // disconnected → no cast, no timer armed

        h.Director.SetConnectedGate(() => true);   // link restored
        h.Director.OnIdleHeartbeat();
        Assert.Contains("bless", h.CastsSent);      // resumes on reconnect
    }

    // In combat the combat tick owns the cadence, so the idle heartbeat must be a
    // no-op (else a round would be double-evaluated). The OnCombatTick sanity check
    // proves the buff WAS due — only the in-combat gate held it.
    [Fact]
    public void OnIdleHeartbeat_InCombat_NoOp_LeavesCadenceToCombatTick()
    {
        using CureHarness h = new();
        h.State.InCombat = true;
        h.DuringCombat = true;   // buffs allowed in combat — only the idle gate can stop this
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.Position = PlayerPosition.Standing;
        h.CastsSent.Clear();
        h.SelfBuff("bless");

        h.Director.OnIdleHeartbeat();
        Assert.Empty(h.CastsSent);          // idle heartbeat is out-of-combat only

        h.Director.OnCombatTick();
        Assert.Contains("bless", h.CastsSent);   // the combat tick DOES cast it (it was due)
    }

    [Fact]
    public void Buff_InCombat_DuringCombatOff_NoCast()
    {
        // Default: self-bless is out-of-combat only, so a live fight blocks it.
        // Set InCombat FIRST so the Ma property-change cascade evaluates with
        // the gate already in place, not through the default out-of-combat window.
        using CureHarness h = new();
        h.State.InCombat = true;
        h.SelfBuff("bless");
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void Buff_InCombat_DuringCombatOn_Casts()
    {
        // Opt-in: with the slot's own "during combat" the same fight allows the recast.
        using CureHarness h = new();
        h.State.InCombat = true;
        h.DuringCombat = true;
        h.SelfBuff("bless");
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("bless", h.CastsSent[0]);
    }

    // ----- Burst-settle guard: pick on the final HP, not a partial read ----
    // A round's hits land as a burst and Paradigm redraws the statline after every
    // line, so mid-burst HP is a partial read that keeps falling. The director holds
    // every between-round cast until HP settles (a damage-line tick or an HP drop
    // opens the window), except an Emergency heal already due on the partial read.
    // AutoBless/AutoHealRest are toggled off during State setup so the reactive
    // Evaluate those assignments trigger doesn't fire before the pass under test.

    [Fact]
    public void Buff_OnDamageDrivenCombatTick_HeldUntilSettled()
    {
        using CureHarness h = new();
        h.EnableBurstSettle();
        h.AutoBlessEnabled = false;              // no buff during setup's reactive passes
        h.State.InCombat = true;
        h.DuringCombat = true;
        h.SelfBuff("bless");
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;                          // HP healthy (200/200) → no heal contends
        h.AutoBlessEnabled = true;                // now eligible, not yet evaluated
        h.CombatTickDamageDriven = true;

        h.Director.OnCombatTick();                // damage-driven tick — burst landing
        Assert.Empty(h.CastsSent);                // buff held

        h.RunSettledPass();                       // burst went quiet, HP never moved
        Assert.Equal(new[] { "bless" }, h.CastsSent);
    }

    [Fact]
    public void Buff_OnTimerFallbackCombatTick_Casts()
    {
        // Same setup, but a timer-fallback tick (not damage-driven) is HP-fresh, so
        // the buff isn't held for HP to settle. It is a round nobody saw, though, so
        // it goes a moment after the projected boundary, not on it.
        using CureHarness h = new();
        h.EnableBurstSettle();
        h.AutoBlessEnabled = false;
        h.State.InCombat = true;
        h.DuringCombat = true;
        h.SelfBuff("bless");
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.AutoBlessEnabled = true;
        h.CombatTickDamageDriven = false;

        h.Director.OnCombatTick();
        Assert.Empty(h.CastsSent);
        Assert.Equal(TimeSpan.FromMilliseconds(200), Assert.Single(h.SettledPasses).Delay);

        h.RunSettledPass();
        Assert.Single(h.CastsSent);
        Assert.Equal("bless", h.CastsSent[0]);
    }

    // Report paradigm-20261007-141844: a tick long after anything placed the round
    // grid is only the timer counting. It frees no slot, so a cast made since the
    // last real boundary isn't followed by another on the client's say-so.
    [Fact]
    public void UnplacedTick_DoesNotFreeTheBetweenRoundSlot()
    {
        using CureHarness h = new();
        bool placed = false;
        h.Director.SetCombatTickPlacement(() => placed);
        h.Director.MarkBetweenRoundSlotUsed();

        h.Director.NotifyRoundComplete();
        Assert.True(h.Director.BetweenRoundSlotUsed);

        placed = true;
        h.Director.NotifyRoundComplete();
        Assert.False(h.Director.BetweenRoundSlotUsed);
    }

    // Report paradigm-20261009-120757, 12:07:51 to 12:07:56, the director on a real
    // TickEngine wired as AppServices wires them. A heal went out just after the
    // round at 12:07:51.486. The room's heat hit at 12:07:53.451 and was read as a
    // new round: the slot came free, the heal went again and drew `You have already
    // cast a spell this round!`. It belongs to the round the game starts at about
    // 12:07:56.5.
    [Fact]
    public void RoomHeatBetweenRounds_FreesNoCastSlot_TheHealWaitsForTheRealRound()
    {
        using CureHarness h = new();
        h.EnableBurstSettle();
        DateTimeOffset start = new(2026, 10, 9, 12, 7, 51, 486, TimeSpan.Zero);
        h.Now = start.UtcDateTime;
        ManualTimers polls = new();
        using TickEngine tick = new(h.Router, () => new DateTimeOffset(h.Now, TimeSpan.Zero), schedule: polls.Arm);
        tick.CombatTickElapsed += () => h.Cast.OnCombatTick(tick.LastCombatTickWasPlaced);
        tick.CombatTickElapsed += h.Director.NotifyRoundComplete;
        h.Director.SetCombatTickSource(() => tick.LastCombatTickWasDamageDriven);
        h.Director.SetCombatTickPlacement(() => tick.LastCombatTickWasPlaced);
        tick.CombatTickElapsed += h.Director.OnCombatTick;

        h.Spells.MinorHealSpell = "grhe";
        h.Health.MinorHealCombatTrigger = 80;
        h.AutoHealRestEnabled = false;                  // no heal off the setup's own HP writes
        h.State.InCombat = true;
        h.State.MaxHp = 522;
        h.State.Hp = 418;
        h.State.HasPromptData = true;
        h.AutoHealRestEnabled = true;

        // 12:07:51.486, the round: a party member's hit, then ours taken.
        h.Router.Dispatch(RoundLine("Forged punches giant hellhound for 57 damage!"));
        h.State.Hp = 397;
        Assert.Empty(h.CastsSent);                      // held while the burst lands
        h.RunSettledPass();
        Assert.Equal(new[] { "grhe" }, h.CastsSent);
        h.State.Hp = 432;                               // "healing 35 damage!"

        // 12:07:53.451, the heat, 1.965 s into the round.
        h.Now = start.UtcDateTime.AddSeconds(1.965);
        h.Router.Dispatch(RoundLine("You are seared by the flames for 28 damage!"));
        h.State.Hp = 408;
        h.Now = h.Now.AddSeconds(0.45);                 // HP has settled
        h.Director.Evaluate();
        Assert.Equal(new[] { "grhe" }, h.CastsSent);    // the round's cast is spent
        Assert.True(h.Director.BetweenRoundSlotUsed);

        // The next round, projected from the one seen.
        h.Now = start.UtcDateTime + TickEngine.CombatTickInterval;
        polls.RunAll();
        h.RunSettledPass();
        Assert.Equal(new[] { "grhe", "grhe" }, h.CastsSent);
    }

    private static LineExtractor.EmittedLine RoundLine(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    // Report paradigm-20260928-131549 settings: 433 max HP, minor < 85%, major < 75%,
    // emergency < 50% (216), grhe / gdhe / mgra, Emergency on priority slot 1.
    private static CureHarness PriestHarness()
    {
        CureHarness h = new();
        h.EnableBurstSettle();
        h.AutoHealRestEnabled = false;
        h.State.InCombat = true;
        h.Spells.MinorHealSpell = "grhe";
        h.Spells.MajorHealSpell = "gdhe";
        h.Spells.EmergencyHealSpell = "mgra";
        h.Health.MinorHealCombatTrigger = 85;
        h.Health.MajorHealCombatTrigger = 75;
        h.Health.EmergencyHealTrigger = 50;
        h.State.MaxMa = 747;
        h.State.Ma = 700;
        h.State.MaxHp = 433;
        h.State.Hp = 411;
        h.AutoHealRestEnabled = true;
        h.CombatTickDamageDriven = true;
        return h;
    }

    [Fact]
    public void MajorHeal_NotPickedOnPartialHp_EmergencyFiresWhenBurstCrossesItsTrigger()
    {
        // Incident replay (13:14:22): the tick fired at a partial HP=236 (54%) and
        // picked gdhe, spending the round's slot; the burst finished at 72 (17%) and
        // the Emergency heal was locked out for a full round.
        using CureHarness h = PriestHarness();
        h.State.Hp = 236;

        h.Director.OnCombatTick();                // first hit line — HP still partial
        Assert.Empty(h.CastsSent);                // no Major heal on the partial read

        h.State.Hp = 213;                         // statline redraw: 49% < 50% emergency
        Assert.Equal(new[] { "mgra" }, h.CastsSent);

        h.State.Hp = 72;                          // rest of the burst
        h.RunSettledPass();
        Assert.Equal(new[] { "mgra" }, h.CastsSent);   // one cast per round, still mgra
    }

    [Fact]
    public void SettledPass_PicksTierOnFinalHp_OneCastPerRound()
    {
        // Incident replay (13:14:07): grhe fired at a partial 362, then again at 350
        // ("already cast this round"); the burst actually ended at 264 (61%), a Major
        // heal. Hold through the redraws, then one gdhe on the settled HP.
        using CureHarness h = PriestHarness();

        h.State.Hp = 362;                         // redraw after the first hit
        h.Director.OnCombatTick();                // damage-line tick
        h.State.Hp = 350;
        h.State.Hp = 261;
        Assert.Empty(h.CastsSent);                // nothing picked mid-burst

        h.RunSettledPass();
        Assert.Equal(new[] { "gdhe" }, h.CastsSent);
    }

    [Fact]
    public void SettledPass_RequeuesWhileHpKeepsFalling()
    {
        // A drop landing after the settled pass was queued extends the window: the
        // pass finds HP still settling and re-queues rather than picking early.
        using CureHarness h = PriestHarness();

        h.State.Hp = 330;                         // 76%: a Minor heal, held mid-burst
        Assert.Single(h.SettledPasses);
        (TimeSpan delay, Action run) = h.SettledPasses[0];
        h.SettledPasses.Clear();

        h.Now += delay - TimeSpan.FromMilliseconds(100);
        h.State.Hp = 300;                         // late hit: 69% → major, window restarts
        h.Now += TimeSpan.FromMilliseconds(100);
        run();                                    // first timer fires inside the new window
        Assert.Empty(h.CastsSent);
        Assert.Single(h.SettledPasses);           // re-queued for the remainder

        h.RunSettledPass();
        Assert.Equal(new[] { "gdhe" }, h.CastsSent);
    }

    [Fact]
    public void EmergencyHeal_MidBurst_WaitsWhenUserRankedADueCastAboveIt()
    {
        // Emergency is a reorderable row. Mid-burst it may only jump the settled pass
        // when nothing due is ranked above it — a user-promoted Major heal keeps its
        // place and fires on the settled HP instead.
        using CureHarness h = PriestHarness();
        h.Spells.PriorityMajorSelfHeal = 1;
        h.Spells.PriorityEmergencyHeal = 2;

        h.Director.OnCombatTick();
        h.State.Hp = 150;                         // 35%: Emergency and Major both due
        Assert.Empty(h.CastsSent);

        h.RunSettledPass();
        Assert.Equal(new[] { "gdhe" }, h.CastsSent);
    }

    [Fact]
    public void StaleDamageTick_HoldsBuff_ThenSettledPassDrivesHeal()
    {
        // Report paradigm-20260904-214056 replay: a due armour buff on a
        // damage-driven tick reads HP=254 (stale-healthy), so it must be HELD; the
        // burst lands the real HP=117 and the settled pass fires the heal into the
        // slot the buff correctly left open.
        using CureHarness h = new();
        h.EnableBurstSettle();
        h.AutoBlessEnabled = false;
        h.AutoHealRestEnabled = true;
        h.State.InCombat = true;
        h.DuringCombat = true;
        h.SelfBuff("uarm");
        h.ManaFloor = 50;
        h.Spells.MajorHealSpell = "grhe";
        h.Health.MajorHealCombatTrigger = 75;     // 75% of 257 ≈ 192
        h.State.MaxMa = 330;
        h.State.Ma = 300;
        h.State.MaxHp = 257;
        h.State.Hp = 254;                          // stale-healthy: 254 > 192, no heal due
        h.AutoBlessEnabled = true;
        h.CombatTickDamageDriven = true;

        h.Director.OnCombatTick();
        Assert.Empty(h.CastsSent);                 // buff held, heal not due on stale read

        h.State.Hp = 117;                          // 117 < 192 → major heal due
        Assert.Empty(h.CastsSent);                 // still mid-burst

        h.RunSettledPass();
        Assert.Single(h.CastsSent);
        Assert.Equal("grhe", h.CastsSent[0]);      // the heal, not the buff
    }

    // Report paradigm-20261004-054304 settings: 520 max HP, minor < 70%, major < 65%,
    // emergency < 50% (260), grhe / gdhe / mgra, Emergency on priority slot 1.
    private static CureHarness ReportPriestHarness()
    {
        CureHarness h = PriestHarness();
        h.Health.MinorHealCombatTrigger = 70;
        h.Health.MajorHealCombatTrigger = 65;
        h.State.MaxMa = 755;
        h.State.MaxHp = 520;
        h.State.Hp = 413;
        h.SettledPasses.Clear();
        return h;
    }

    [Fact]
    public void MajorHeal_NotPickedOnPartialHp_WhenAnotherHpSubscriberEvaluatesFirst()
    {
        // Incident replay (05:42:51): the party self row saw HP=273 first and its
        // HpPercent change ran a pass before the director had stamped the drop, so gdhe
        // went out on the partial read. The burst ended at 160 (31%) and the mgra sent
        // after it drew "You have already cast a spell this round!".
        using CureHarness h = ReportPriestHarness();
        h.BeforeDirectorSeesHp = () => h.Director.Evaluate();

        h.State.Hp = 273;                         // mummy's hit: 52%, a partial read
        Assert.Empty(h.CastsSent);

        h.State.Hp = 160;                         // tentacle's hit: 31% < 50%
        Assert.Equal(new[] { "mgra" }, h.CastsSent);
    }

    [Fact]
    public void DamageTick_ReadAfterTheBurstsPrompts_KeepsTheSlotSpent()
    {
        // Same incident: the round's damage-line tick is processed after the burst's
        // prompts, so it lands just after the heal those prompts fired. Freeing the
        // slot there let a second heal into the refused "already cast" round.
        using CureHarness h = ReportPriestHarness();

        h.State.Hp = 160;                         // emergency due, fires at once
        Assert.Equal(new[] { "mgra" }, h.CastsSent);

        h.Cast.OnCombatTick();                    // this burst's own tick
        h.Director.NotifyRoundComplete();
        h.Director.OnCombatTick();
        h.RunSettledPass();                       // gdhe is due at 31%, but the slot is spent
        Assert.Equal(new[] { "mgra" }, h.CastsSent);
    }

    [Fact]
    public void DamageTick_NextRound_FreesTheSlot()
    {
        using CureHarness h = ReportPriestHarness();

        h.State.Hp = 160;
        Assert.Equal(new[] { "mgra" }, h.CastsSent);

        h.Now += TimeSpan.FromSeconds(4.6);       // the next round's first damage line
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();
        h.State.Hp = 300;                         // 58% after the heal: a Major heal
        Assert.Equal(new[] { "mgra", "gdhe" }, h.CastsSent);
    }

    [Fact]
    public void Buff_IdleResting_CastsRegardlessOfWhileRestingFlag()
    {
        // Idle resting (Position=Resting but NOT a triggered recovery rest) is not
        // gated by "bless while resting" — the normal cadence buffs here even with
        // the flag off (its default). Only a TRIGGERED rest engages that gate.
        using CureHarness h = new();
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Resting;
        h.TriggeredRest = false;                 // idle rest, not a recovery rest
        h.WhileResting = false;  // the new default
        h.SelfBuff("bless");
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("bless", h.CastsSent[0]);
    }

    [Fact]
    public void Buff_TriggeredRest_WhileRestingOff_NoCast()
    {
        // A triggered recovery rest (HP/MA fell below rest-if-below) with the
        // "while resting" override off holds the buff — recovery comes first.
        using CureHarness h = new();
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Resting;
        h.TriggeredRest = true;                  // active recovery rest
        h.WhileResting = false;
        h.SelfBuff("bless");
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void Buff_TriggeredRest_WhileRestingOn_Casts()
    {
        // With the override on, a triggered recovery rest still buffs.
        using CureHarness h = new();
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Resting;
        h.TriggeredRest = true;
        h.WhileResting = true;   // opt-in override
        h.SelfBuff("bless");
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("bless", h.CastsSent[0]);
    }

    // ----- Each buff carries its own mana floor and rest / combat switches -----

    [Fact]
    public void Buff_ManaFloorIsPerBuff_LowFloorCastsWhileHighFloorWaits()
    {
        // Two buffs both down at 60% mana: the one waiting for 80% is passed over,
        // the one happy at 40% casts — even though it sits lower in the list.
        using CureHarness h = new();
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = "bigb", CastOnSelf = true, BlessIfAboveMa = 80 });
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = "smal", CastOnSelf = true, BlessIfAboveMa = 40 });
        h.State.MaxMa = 100;
        h.State.Ma = 60;

        h.Director.Evaluate();

        Assert.Equal(new[] { "smal" }, h.CastsSent);
    }

    // ----- Priority buffs: their own rank in the spell-type priority -----

    [Fact]
    public void PriorityBuff_CastsAtItsOwnRank_AheadOfOrdinaryBuffs()
    {
        // The ordinary buff is listed first, yet the starred one goes out first:
        // its category ranks ahead of Buffing by default.
        using CureHarness h = new();
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = "plai", CastOnSelf = true });
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = "star", CastOnSelf = true, PriorityBuff = true });
        h.State.MaxMa = 100;
        h.State.Ma = 100;

        h.Director.Evaluate();

        Assert.Equal(new[] { "star" }, h.CastsSent);
    }

    [Fact]
    public void PriorityBuff_FollowsWhereTheUserRanksItsCategory()
    {
        using CureHarness h = new();
        h.Spells.PriorityBuffing = 5;
        h.Spells.PriorityPriorityBuffs = 9;
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = "star", CastOnSelf = true, PriorityBuff = true });
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = "plai", CastOnSelf = true });
        h.State.MaxMa = 100;
        h.State.Ma = 100;

        h.Director.Evaluate();

        Assert.Equal(new[] { "plai" }, h.CastsSent);
    }

    [Fact]
    public void Buff_ManaFloorIsPerBuff_HighFloorCastsOnceManaReachesIt()
    {
        using CureHarness h = new();
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = "bigb", CastOnSelf = true, BlessIfAboveMa = 80 });
        h.State.MaxMa = 100;
        h.State.Ma = 79;
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);

        h.State.Ma = 80;
        h.Director.Evaluate();

        Assert.Equal(new[] { "bigb" }, h.CastsSent);
    }

    [Fact]
    public void Buff_ManaFloorIsPerBuff_AbsoluteModeReadsTheSlotValueAsMana()
    {
        // With Health thresholds set to absolute values the slot's number is mana
        // points, not a percent: 150 of a 400 pool is under a 200 floor.
        using CureHarness h = new();
        h.Health.MaThresholdMode = ThresholdMode.Absolute;
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = "bigb", CastOnSelf = true, BlessIfAboveMa = 200 });
        h.State.MaxMa = 400;
        h.State.Ma = 150;
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);

        h.State.Ma = 200;
        h.Director.Evaluate();

        Assert.Equal(new[] { "bigb" }, h.CastsSent);
    }

    [Fact]
    public void Buff_CombatSwitchIsPerBuff_OnlyTheTickedBuffCastsInAFight()
    {
        using CureHarness h = new();
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = "calm", CastOnSelf = true, BlessIfAboveMa = 0 });
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = "figh", CastOnSelf = true, BlessIfAboveMa = 0, BlessDuringCombat = true });
        h.State.InCombat = true;
        h.State.MaxMa = 100;
        h.State.Ma = 100;

        h.Director.Evaluate();

        Assert.Equal(new[] { "figh" }, h.CastsSent);
    }

    [Fact]
    public void Buff_RestSwitchIsPerBuff_OnlyTheTickedBuffCastsInARecoveryRest()
    {
        using CureHarness h = new();
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = "wait", CastOnSelf = true, BlessIfAboveMa = 0 });
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = "rest", CastOnSelf = true, BlessIfAboveMa = 0, BlessWhileResting = true });
        h.State.Position = PlayerPosition.Resting;
        h.TriggeredRest = true;
        h.State.MaxMa = 100;
        h.State.Ma = 100;

        h.Director.Evaluate();

        Assert.Equal(new[] { "rest" }, h.CastsSent);
    }

    [Fact]
    public void Buff_NewSlotDefaults_WaitForHalfManaAndHoldInCombat()
    {
        // A buff added fresh waits for half mana, and holds in a fight and in a
        // recovery rest.
        BuffSlot fresh = new();
        Assert.Equal(50, fresh.BlessIfAboveMa);
        Assert.False(fresh.BlessWhileResting);
        Assert.False(fresh.BlessDuringCombat);

        using CureHarness h = new();
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = "bles", CastOnSelf = true });
        h.State.MaxMa = 100;
        h.State.Ma = 49;
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);

        h.State.Ma = 50;
        h.Director.Evaluate();
        Assert.Equal(new[] { "bles" }, h.CastsSent);
    }

    [Fact]
    public void ManaRegenReroll_WaitsOutAFightItsBuffMayNotCastIn_ThenFires()
    {
        // A reroll staged mid-fight for a buff without "cast during combat" is held,
        // not dropped: it goes out once the fight ends.
        using CureHarness h = new();
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = "mreg", BlessIfAboveMa = 0 });
        h.State.InCombat = true;
        h.State.MaxMa = 100;
        h.State.Ma = 100;

        h.Director.RequestManaRegenReroll("mreg");
        Assert.Empty(h.CastsSent);

        h.State.InCombat = false;
        h.Director.Evaluate();

        Assert.Equal(new[] { "mreg" }, h.CastsSent);
    }

    [Fact]
    public void ManaRegenReroll_UnderItsBuffsManaFloor_IsDropped()
    {
        using CureHarness h = new();
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = "mreg", BlessIfAboveMa = 90 });
        h.State.MaxMa = 100;
        h.State.Ma = 50;

        h.Director.RequestManaRegenReroll("mreg");
        h.State.Ma = 100;
        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void Buff_AutoHealRestOff_AutoBlessOn_StillCasts()
    {
        // Auto-bless is controlled by the Auto-Bless toggle and nothing else:
        // with Auto-Rest/Heal off but Auto-Bless on, buffing still runs.
        using CureHarness h = new();
        h.AutoHealRestEnabled = false;           // Auto-Rest/Heal master off
        h.AutoBlessEnabled = true;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Standing;
        h.SelfBuff("bless");
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("bless", h.CastsSent[0]);
    }

    [Fact]
    public void Buff_AutoBlessOff_NoCast()
    {
        using CureHarness h = new();
        h.AutoBlessEnabled = false;          // Auto-Bless engine disabled
        h.SelfBuff("bless");
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Standing;

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void Buff_BuffStripRoom_NoCast()
    {
        // The current room casts a buff-removal spell on entry, so re-blessing
        // here just burns mana — the Buffing category is suppressed.
        using CureHarness h = new();
        h.BuffStripRoom = true;
        h.SelfBuff("bless");
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Standing;

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void Buff_BuffStripRoom_StillHeals()
    {
        // The buff-strip gate is buff-only: a life-threat heal must still fire in
        // a room that removes buffs.
        using CureHarness h = new();
        h.BuffStripRoom = true;
        h.Spells.MajorHealSpell = "fullheal";
        h.Health.MajorHealCombatTrigger = 40;
        h.SelfBuff("bless");
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = true;
        h.State.MaxHp = 200;
        h.State.Hp = 60;                 // 30% < 40% major trigger

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("fullheal", h.CastsSent[0]);
    }

    [Fact]
    public void Buff_SkipsActiveBuff_PicksNext()
    {
        using CureHarness h = new();
        h.SelfBuff("bless");
        h.SelfBuff("haste");
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;

        // bless confirmed (self AppliedMessage) → 300s timer → not due.
        h.BuffInfo["bless"] = (string.Empty, 300);
        h.RecordCondition("bless", MessageFlags.None,
            applied: "You are blessed!", endsWith: "Your blessing fades.");
        h.FeedLine("You are blessed!");
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();   // new round frees the one-per-round between-round slot

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("haste", h.CastsSent[0]);
    }

    [Fact]
    public void Buff_AllActive_NoCast()
    {
        using CureHarness h = new();
        h.SelfBuff("bless");
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;

        h.BuffInfo["bless"] = (string.Empty, 300);
        h.RecordCondition("bless", MessageFlags.None,
            applied: "You are blessed!", endsWith: "Your blessing fades.");
        h.FeedLine("You are blessed!");
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void Buff_SelfDurationRecast_OnlyWithinExpiryWindow()
    {
        // Cast → confirm (300s timer) → no recast mid-duration → recast once
        // inside the 15s-of-expiry window.
        using CureHarness h = new();
        h.SelfBuff("bless");
        h.BuffInfo["bless"] = (string.Empty, 300);
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.RecordCondition("bless", MessageFlags.None,
            applied: "You are blessed!", endsWith: "Your blessing fades.");

        // First cast (no timer yet ⇒ due).
        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
        Assert.Equal("bless", h.CastsSent[0]);

        // Confirm our cast → 300s timer starts at Now.
        h.FeedLine("You are blessed!");
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();

        // Well inside the duration → not due → no recast.
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);

        // Advance to 10s before expiry (≤ RecastMarginSec) → due → recast.
        h.Now = h.Now.AddSeconds(290);
        h.Cast.OnCombatTick();
        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
        Assert.Equal("bless", h.CastsSent[0]);
    }

    [Fact]
    public void Buff_ClobberingCast_ClearsRemovedBuffTimer()
    {
        // A cast that REMOVES another buff (chan removes gbls) clears the removed
        // buff's timer the moment it lands, so a stripped buff doesn't keep reading
        // as up (report paradigm-20260910-001023). Deterministic off RemovesSpell,
        // not the ambiguous shared wear-off line.
        using CureHarness h = new();
        h.ManaFloor = 0;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Standing;

        h.SelfBuff("gbls");
        h.SelfBuff("chan");
        h.BuffInfo["gbls"] = (string.Empty, 300);
        h.BuffInfo["chan"] = (string.Empty, 500);
        h.Removes["chan"] = new[] { "gbls" };   // chan strips gbls
        h.RecordCondition("gbls", MessageFlags.None, applied: "You feel greatly blessed!");
        h.RecordCondition("chan", MessageFlags.None, applied: "You hear a chant!");

        // Cast + confirm gbls → its timer is up.
        h.Director.Evaluate();
        h.FeedLine("You feel greatly blessed!");
        Assert.Contains(h.Director.SnapshotActiveBuffs(), b => b.Short == "gbls");

        // Past the 400ms applied-burst window so chan's confirm isn't treated as a
        // sibling of gbls's just-landed burst (still well inside gbls's 300s timer).
        h.Now = h.Now.AddSeconds(10);

        // Cast + confirm chan → it strips gbls, so gbls's timer must clear.
        h.Cast.OnCombatTick();
        h.Director.Evaluate();
        h.FeedLine("You hear a chant!");

        var active = h.Director.SnapshotActiveBuffs();
        Assert.Contains(active, b => b.Short == "chan");
        Assert.DoesNotContain(active, b => b.Short == "gbls");
    }

    [Fact]
    public void Buff_ConditionEnded_ClearsTimer_RecastsImmediately()
    {
        // Server-confirmed early wear-off drops the timer so the wear-off line's
        // own re-evaluation recasts immediately — once — without waiting out the
        // stale clock. The optimistic on-send timer then blocks a same-round
        // double.
        using CureHarness h = new();
        h.SelfBuff("bless");
        h.BuffInfo["bless"] = (string.Empty, 300);
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.RecordCondition("bless", MessageFlags.None,
            applied: "You are blessed!", endsWith: "Your blessing fades.");

        h.Director.Evaluate();                 // first cast
        h.FeedLine("You are blessed!");        // 300s timer
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();      // new round frees the between-round slot

        h.Director.Evaluate();                 // mid-duration → no recast
        Assert.Empty(h.CastsSent);

        h.FeedLine("Your blessing fades.");    // wear-off clears timer → recasts now
        Assert.Single(h.CastsSent);
        Assert.Equal("bless", h.CastsSent[0]);

        // The recast re-armed the optimistic timer, so a stale re-evaluation this
        // round can't fire a second bless (slot freed here so the TIMER is what blocks).
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();
        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
    }

    [Fact]
    public void Buff_NegativeMargin_WearOff_DefersRecastByBuffer_NotImmediate()
    {
        // A NEGATIVE recast margin means "recast |margin|s AFTER wear-off" (to spread out
        // mana use). Unlike the immediate-recast case above, the wear-off line must NOT
        // drop the timer — doing so would recast the instant it lands, collapsing the
        // buffer. The timer stays and IsRecastDue holds off until the buffer elapses.
        using CureHarness h = new();
        h.SelfBuff("bless");
        h.PartyBuffs.Slots.Single(s => s.Spell == "bless").RecastMarginSec = -30;   // recast 30s AFTER it wears off
        h.BuffInfo["bless"] = (string.Empty, 300);
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.RecordCondition("bless", MessageFlags.None,
            applied: "You are blessed!", endsWith: "Your blessing fades.");

        h.Director.Evaluate();                 // first cast
        h.FeedLine("You are blessed!");        // 300s timer, -30s margin
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();

        // Reach natural expiry and take the wear-off line — recast must NOT fire yet.
        h.Now = h.Now.AddSeconds(300);
        h.FeedLine("Your blessing fades.");    // wear-off — buffer keeps the timer
        Assert.Empty(h.CastsSent);

        // 15s into the 30s buffer → still waiting.
        h.Now = h.Now.AddSeconds(15);
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);

        // 30s past expiry → buffer elapsed → recast now.
        h.Now = h.Now.AddSeconds(15);
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();
        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
        Assert.Equal("bless", h.CastsSent[0]);
    }

    [Fact]
    public void Buff_MarginEditedWhileUp_ReflectsInSnapshot_WithoutRecast()
    {
        // Editing a slot's recast margin while the buff is already up must take effect on
        // the LIVE timer — the watchdog snapshot (which drives the bar) reads the current
        // config, not the value snapshotted at cast time. Flipping positive → negative
        // redraws the red buffer immediately instead of waiting for the next cast.
        using CureHarness h = new();
        h.SelfBuff("bless");
        h.PartyBuffs.Slots.Single(s => s.Spell == "bless").RecastMarginSec = 15;    // positive to start
        h.BuffInfo["bless"] = (string.Empty, 300);
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.RecordCondition("bless", MessageFlags.None,
            applied: "You are blessed!", endsWith: "Your blessing fades.");

        h.Director.Evaluate();                 // cast
        h.FeedLine("You are blessed!");        // 300s timer, +15 margin
        h.CastsSent.Clear();

        Assert.Equal(15, h.Director.SnapshotActiveBuffs().Single(b => b.Short == "bless").MarginSec);

        // Edit the slot to a negative buffer — no recast, just a config change.
        h.PartyBuffs.Slots.Single(s => s.Spell == "bless").RecastMarginSec = -30;
        Assert.Equal(-30, h.Director.SnapshotActiveBuffs().Single(b => b.Short == "bless").MarginSec);
        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void Buff_SelfPerSlotMargin_RecastsAtConfiguredLead_NotTheDefault()
    {
        // A slot with a 30s recast lead re-casts 30s before expiry — earlier than
        // the 15s default would.
        using CureHarness h = new();
        h.SelfBuff("bless");
        h.PartyBuffs.Slots.Single(s => s.Spell == "bless").RecastMarginSec = 30;
        h.BuffInfo["bless"] = (string.Empty, 300);
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.RecordCondition("bless", MessageFlags.None,
            applied: "You are blessed!", endsWith: "Your blessing fades.");

        h.Director.Evaluate();                 // first cast
        h.FeedLine("You are blessed!");        // 300s timer, 30s lead
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();

        // 40s before expiry → past neither lead → not due.
        h.Now = h.Now.AddSeconds(260);
        h.Cast.OnCombatTick();
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);

        // 25s before expiry → inside the 30s lead (the 15s default would still be
        // waiting) → due.
        h.Now = h.Now.AddSeconds(15);
        h.Cast.OnCombatTick();
        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
        Assert.Equal("bless", h.CastsSent[0]);
    }

    [Fact]
    public void Buff_SelfPerSlotMarginZero_WaitsForActualExpiry()
    {
        // A 0 lead means "don't recast until the tracked timer actually runs out"
        // — the 15s default window must NOT trigger an early recast.
        using CureHarness h = new();
        h.SelfBuff("bless");
        h.PartyBuffs.Slots.Single(s => s.Spell == "bless").RecastMarginSec = 0;
        h.BuffInfo["bless"] = (string.Empty, 300);
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.RecordCondition("bless", MessageFlags.None,
            applied: "You are blessed!", endsWith: "Your blessing fades.");

        h.Director.Evaluate();
        h.FeedLine("You are blessed!");        // 300s timer, 0s lead
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();

        // 5s before expiry → the default 15s window WOULD recast; a 0 lead holds.
        h.Now = h.Now.AddSeconds(295);
        h.Cast.OnCombatTick();
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);

        // Tracked timer runs out → now due.
        h.Now = h.Now.AddSeconds(5);
        h.Cast.OnCombatTick();
        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
        Assert.Equal("bless", h.CastsSent[0]);
    }

    [Fact]
    public void Buff_SelfPerSlotMarginZero_WearOffMessageRecastsImmediately()
    {
        // The other half of the 0-lead contract: a server wear-off message drops
        // the timer and recasts at once, well before the tracked expiry.
        using CureHarness h = new();
        h.SelfBuff("bless");
        h.PartyBuffs.Slots.Single(s => s.Spell == "bless").RecastMarginSec = 0;
        h.BuffInfo["bless"] = (string.Empty, 300);
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.RecordCondition("bless", MessageFlags.None,
            applied: "You are blessed!", endsWith: "Your blessing fades.");

        h.Director.Evaluate();
        h.FeedLine("You are blessed!");        // 300s timer
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();      // new round frees the between-round slot

        h.Director.Evaluate();                 // mid-duration → no recast
        Assert.Empty(h.CastsSent);

        h.FeedLine("Your blessing fades.");    // early wear-off → recast now
        Assert.Single(h.CastsSent);
        Assert.Equal("bless", h.CastsSent[0]);
    }

    [Fact]
    public void SelfHeal_StaleRepeat_SuppressedWhenPoolUnchanged()
    {
        // Repro of the swan double-cast: a combat tick wipes the coordinator's
        // one-cast-per-round cooldown, so a second evaluation on the SAME (not-
        // yet-server-reflected) HP/MA would re-issue the identical heal. The
        // stale guard suppresses the byte-for-byte repeat.
        using Harness h = new();
        h.Spells.MinorHealSpell = "swan";
        h.Health.MinorHealCombatTrigger = 70;
        h.Health.MajorHealCombatTrigger = 40;

        h.SetPrompt(hp: 65, maxHp: 100, inCombat: true);   // casts swan once
        Assert.Single(h.CastsSent);
        Assert.Equal("swan", h.CastsSent[0]);

        h.Cast.OnCombatTick();                 // wipe cooldown (root-cause window)
        h.Director.NotifyRoundComplete();      // new round → between-round slot free
        h.Director.Evaluate();                 // unchanged pool → stale-guard suppresses
        Assert.Single(h.CastsSent);

        // Once HP actually drops, the pool moved → a fresh heal is free to fire.
        h.SetPrompt(hp: 55, maxHp: 100, inCombat: true);
        Assert.Equal(2, h.CastsSent.Count);
        Assert.Equal("swan", h.CastsSent[1]);
    }

    [Fact]
    public void SelfBuff_OptimisticTimer_SuppressesSameRoundRecast()
    {
        // Repro of the tige double-cast: without an on-send recast clock, a
        // combat tick wiping the cooldown lets a second evaluation re-issue the
        // buff before its AppliedMessage confirms — draining kai and drawing a
        // "not enough kai" error. The optimistic timer closes that window.
        using CureHarness h = new();
        h.SelfBuff("tige");
        h.BuffInfo["tige"] = (string.Empty, 300);
        h.ManaFloor = 70;          // 70% of 5 kai → floor 4
        h.State.MaxMa = 5;
        h.State.Ma = 5;
        h.State.InCombat = false;
        h.RecordCondition("tige", MessageFlags.None,
            applied: "You feel invigorated.", endsWith: "Your vigor fades.");

        h.Director.Evaluate();                 // first cast — optimistic timer arms
        Assert.Single(h.CastsSent);
        Assert.Equal("tige", h.CastsSent[0]);

        // Stale re-evaluation (kai spend not yet reflected) must NOT re-cast.
        h.Cast.OnCombatTick();
        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
    }

    [Fact]
    public void SelfBuff_Fizzle_ClearsOptimisticTimer_RecastsNextRound()
    {
        // Repro of the bless uptime hole: the on-send optimistic timer marks the
        // buff "active" for its whole assumed duration, but when the cast FIZZLES
        // (never lands) that timer must be dropped so the next round re-attempts —
        // otherwise a single fizzle leaves the buff DOWN for ~90s.
        using CureHarness h = new();
        h.SelfBuff("bles");
        h.BuffInfo["bles"] = (string.Empty, 300);   // long duration → phantom timer would suppress
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.RecordCondition("bles", MessageFlags.None,
            applied: "You are blessed!", endsWith: "Your blessing fades.");

        h.Director.Evaluate();                 // first cast — optimistic 300s timer arms
        Assert.Single(h.CastsSent);
        Assert.Equal("bles", h.CastsSent[0]);

        // Server fizzles the cast (through the router → CastCoordinator → CastFailed).
        h.Router.Dispatch(new LineExtractor.EmittedLine(
            "You attempt to cast bless, but fail.", Array.Empty<CellAttributes>(),
            DateTimeOffset.UtcNow, IsPromptLine: false));

        // Next round: the phantom timer is gone, so the buff re-attempts instead of
        // sitting "active" for the full 300s.
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();      // new round frees the between-round slot
        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
        Assert.Equal("bles", h.CastsSent[0]);
    }

    [Fact]
    public void SelfBuff_Landed_SurvivesLaterCastFailure()
    {
        // Once a buff confirms (AppliedMessage), its real duration timer is
        // authoritative — a later unrelated cast failure must NOT clear it and cause
        // a needless recast inside the still-valid duration.
        using CureHarness h = new();
        h.SelfBuff("bles");
        h.BuffInfo["bles"] = (string.Empty, 300);
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.RecordCondition("bles", MessageFlags.None,
            applied: "You are blessed!", endsWith: "Your blessing fades.");

        h.Director.Evaluate();                 // cast
        h.FeedLine("You are blessed!");        // confirm → 300s timer, pending marker cleared
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();

        // A later (heal) cast fizzles — must not clear the confirmed bless timer.
        h.Router.Dispatch(new LineExtractor.EmittedLine(
            "You attempt to cast heal, but fail.", Array.Empty<CellAttributes>(),
            DateTimeOffset.UtcNow, IsPromptLine: false));

        h.Cast.OnCombatTick();
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);             // still active → no recast
    }

    [Fact]
    public void BetweenRound_OneCastPerRound_SuppressesSecondUntilRoundBoundary()
    {
        // The game allows a single 0-energy between-round cast per combat round, so
        // once we've cast one, further between-round casts THIS round are suppressed
        // (sending them just draws "already cast this round" and they don't fire — the
        // mageshield storm's root, report paradigm-20260816-101702). The slot frees at
        // the round boundary (NotifyRoundComplete), letting the next buff fire.
        using CureHarness h = new();
        h.DuringCombat = true;   // opt in to blessing mid-fight
        h.SelfBuff("mshi");
        h.SelfBuff("armr");
        h.BuffInfo["mshi"] = (string.Empty, 300);
        h.BuffInfo["armr"] = (string.Empty, 300);
        h.ManaFloor = 0;
        h.State.MaxMa = 100;
        h.State.Ma = 100;
        h.State.InCombat = true;

        // Setup's state-change evaluations fired a buff or two out of combat; reset to
        // a clean, in-combat starting point so the controlled round sequence is exact.
        h.Director.ResetBuffTracking();
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();

        h.Director.Evaluate();                 // round 1 — first between-round cast (mshi)
        Assert.Single(h.CastsSent);
        Assert.Equal("mshi", h.CastsSent[0]);

        // Same round: armr is due but the round's single slot is spent → suppressed
        // (OnCombatTick clears the coordinator cooldown, so only the round gate holds).
        h.Cast.OnCombatTick();
        h.Director.Evaluate();
        Assert.Single(h.CastsSent);

        // Round boundary frees the slot → the next between-round cast (armr) fires.
        h.Director.NotifyRoundComplete();
        h.Cast.OnCombatTick();
        h.Director.Evaluate();
        Assert.Equal(2, h.CastsSent.Count);
        Assert.Equal("armr", h.CastsSent[1]);
    }

    [Fact]
    public void SelfBuff_AlreadyCastThisRound_DropsOptimisticTimer_ReAttempts()
    {
        // "You have already cast a spell this round!" means the round's between-round
        // slot was already spent, so the buff we just sent did NOT cast. Its optimistic
        // recast timer must be dropped (like a fizzle) so it re-attempts next round,
        // rather than sitting "active" un-cast for its whole assumed duration.
        using CureHarness h = new();
        h.SelfBuff("mshi");
        h.BuffInfo["mshi"] = (string.Empty, 300);
        h.ManaFloor = 0;
        h.State.MaxMa = 100;
        h.State.Ma = 100;
        h.State.InCombat = false;   // the per-round cap applies in or out of combat now
        h.RecordCondition("mshi", MessageFlags.None,
            applied: "You feel protected!", endsWith: "Your mageshield shimmers and fades.");

        h.Director.Evaluate();                 // cast — optimistic 300s timer arms
        Assert.Single(h.CastsSent);

        // Server: the slot was already used → this cast did NOT fire.
        h.Router.Dispatch(new LineExtractor.EmittedLine(
            "You have already cast a spell this round!", Array.Empty<CellAttributes>(),
            DateTimeOffset.UtcNow, IsPromptLine: false));

        // Timer dropped → it re-attempts rather than sitting phantom-active for 300s.
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();      // new round frees the between-round slot
        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
        Assert.Equal("mshi", h.CastsSent[0]);
    }

    [Fact]
    public void SelfBuff_AlreadyCastThisRound_ForACommandTheUserTyped_KeepsItsTimer()
    {
        // The buff went out and landed; the user then typed an item use into the same
        // round and the game refused that. The refusal names no spell, and the command
        // echoed ahead of it is not the buff's: the buff's timer must stand.
        using CureHarness h = new();
        h.SelfBuff("mshi");
        h.BuffInfo["mshi"] = (string.Empty, 300);
        h.ManaFloor = 0;
        h.State.MaxMa = 100;
        h.State.Ma = 100;
        h.State.InCombat = false;
        h.RecordCondition("mshi", MessageFlags.None,
            applied: "You feel protected!", endsWith: "Your mageshield shimmers and fades.");

        h.Director.Evaluate();
        Assert.Single(h.CastsSent);

        DateTimeOffset at = DateTimeOffset.UtcNow;
        h.Router.Dispatch(new LineExtractor.EmittedLine(
            "[HP=609/MA=330]:", Array.Empty<CellAttributes>(), at, IsPromptLine: true));
        h.Router.Dispatch(new LineExtractor.EmittedLine(
            "use coin", Array.Empty<CellAttributes>(), at, IsPromptLine: false));
        h.Router.Dispatch(new LineExtractor.EmittedLine(
            "You have already cast a spell this round!", Array.Empty<CellAttributes>(),
            DateTimeOffset.UtcNow, IsPromptLine: false));

        h.CastsSent.Clear();
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void SnapshotActiveBuffs_ReflectsArmedTimers_ClearedByReset()
    {
        // The Buff Watchdog reads live timers through this snapshot — each armed
        // buff surfaces as (Target="" self, Short, Until, MarginSec, TotalSec).
        using CureHarness h = new();
        h.SelfBuff("bles");
        h.BuffInfo["bles"] = (string.Empty, 300);
        h.ManaFloor = 0;
        h.State.MaxMa = 100;
        h.State.Ma = 100;
        h.State.InCombat = false;

        h.Director.Evaluate();                 // casts bles → arms a 300s optimistic timer
        Game.Spells.ActiveBuffTimer e = Assert.Single(h.Director.SnapshotActiveBuffs());
        Assert.Equal(string.Empty, e.Target);
        Assert.Equal("bles", e.Short);
        Assert.Equal(300, e.TotalSec);

        h.Director.ResetBuffTracking();        // disconnect/death clears all timers
        Assert.Empty(h.Director.SnapshotActiveBuffs());
    }

    [Fact]
    public void UnrelatedCastRejection_DoesNotDropPendingSelfBuffTimer()
    {
        // Report paradigm-20260824-233439 ("spamming vlwa"): CastCoordinator is
        // shared with CombatManager's attack-spell cascade, and the server's "You
        // have already cast a spell this round!" line never names which cast it's
        // rejecting. A collision from an UNRELATED cast (an attack-spell resume,
        // simulated here directly via Cast.TryCast) must not be mistaken for the
        // pending self-buff failing — otherwise a buff that already landed gets its
        // timer dropped and re-casts again within seconds, over and over.
        using CureHarness h = new();
        h.SelfBuff("bles");
        h.BuffInfo["bles"] = (string.Empty, 300);
        h.ManaFloor = 0;
        h.State.MaxMa = 100;
        h.State.Ma = 100;
        h.State.InCombat = false;

        h.Director.Evaluate();                 // casts bles → arms a 300s optimistic timer,
                                                // still pending (no applied-line confirm yet)
        Assert.Single(h.Director.SnapshotActiveBuffs());

        // An unrelated attack-spell cast goes out and loses the round's slot — the
        // same race an interrupt-resume's bypassRecastInterval:true send hits in
        // production.
        Assert.True(h.Cast.TryCast("turn", "shade",
            bypassRoundCooldown: true, bypassRecastInterval: true));
        h.Router.Dispatch(new LineExtractor.EmittedLine(
            "You have already cast a spell this round!", Array.Empty<CellAttributes>(),
            DateTimeOffset.UtcNow, IsPromptLine: false));

        // bles's timer must survive — the rejection was about "turn", not "bles".
        Game.Spells.ActiveBuffTimer e = Assert.Single(h.Director.SnapshotActiveBuffs());
        Assert.Equal("bles", e.Short);
    }

    [Fact]
    public void StaleRejection_DoesNotDropPendingSelfBuffTimer()
    {
        // Report paradigm-20260827-130111: prev's pending marker dangled (its applied
        // line never cleared it), and long after, the user's manual `mahe` heals at a
        // dying party member drew "You have already cast a spell this round!".
        // CastCoordinator still held prev as its last OWN cast, so the spell-less
        // rejection matched the pending buff and dropped its LIVE timer — forcing a 5x
        // recast cascade while 75-150s still remained. A rejection this far past the
        // arm can't be about our own send, so the buff's timer must survive.
        using CureHarness h = new();
        h.SelfBuff("bles");
        h.BuffInfo["bles"] = (string.Empty, 300);
        h.ManaFloor = 0;
        h.State.MaxMa = 100;
        h.State.Ma = 100;
        h.State.InCombat = false;

        h.Director.Evaluate();                 // casts bles → arms a 300s optimistic timer
        Assert.Single(h.Director.SnapshotActiveBuffs());

        // 30s later (well past the rejection window), an unrelated manual cast spends
        // the round. CastCoordinator still names bles (our last own cast), so the
        // rejection carries "bles" and passes the name guard — but the marker is stale.
        h.Now = h.Now.AddSeconds(30);
        h.Router.Dispatch(new LineExtractor.EmittedLine(
            "You have already cast a spell this round!", Array.Empty<CellAttributes>(),
            DateTimeOffset.UtcNow, IsPromptLine: false));

        // bles's timer survives — the stale rejection wasn't about our send.
        Game.Spells.ActiveBuffTimer e = Assert.Single(h.Director.SnapshotActiveBuffs());
        Assert.Equal("bles", e.Short);
    }

    [Fact]
    public void NoteManualBuffCast_ArmsTimerByCastCode_WithSlotMargin()
    {
        // A hand-typed buff cast code arms its recast timer anchored on the code — so the
        // Buff Watchdog + recast engine track a manual cast the same as an engine one.
        using CureHarness h = new();
        h.SelfBuff("bles");
        h.PartyBuffs.Slots.Single(s => s.Spell == "bles").RecastMarginSec = 20;   // the slot's configured recast lead
        h.BuffInfo["bles"] = (string.Empty, 300);

        h.Director.NoteManualBuffCast("bles");

        Game.Spells.ActiveBuffTimer e = Assert.Single(h.Director.SnapshotActiveBuffs());
        Assert.Equal("bles", e.Short);
        Assert.Equal(300, e.TotalSec);
        Assert.Equal(20, e.MarginSec);
        Assert.Equal(h.Now.AddSeconds(300), e.Until);
    }

    [Fact]
    public void NoteManualBuffCast_NonBuffCode_DoesNotArm()
    {
        // A combat / instant cast code (no resolved duration) must not get a recast timer.
        using CureHarness h = new();
        h.Director.NoteManualBuffCast("lbol");     // not in BuffInfo → no duration
        Assert.Empty(h.Director.SnapshotActiveBuffs());
    }

    [Fact]
    public void PauseResume_KeepsSelfTimerOnBriefReconnect()
    {
        // paradigm-20260908-205555: a hangup + reconnect must NOT throw the self-buff
        // timers away and force a full rebuff — the buff persists server-side through the
        // drop, so a brief gap keeps it armed (client does not recast). (Self timers used
        // to be cleared wholesale here.)
        using CureHarness h = new();
        h.SelfBuff("bles");
        h.BuffInfo["bles"] = (string.Empty, 300);   // 5-minute self buff
        h.Director.NoteManualBuffCast("bles");     // a self timer
        Assert.Single(h.Director.SnapshotActiveBuffs());

        h.Director.PauseBuffTimers();              // disconnect
        h.Now = h.Now.AddSeconds(45);              // brief hang, « 300s
        h.Director.ResumeBuffTimers();             // reconnect

        Game.Spells.ActiveBuffTimer kept = Assert.Single(h.Director.SnapshotActiveBuffs());
        Assert.Equal(string.Empty, kept.Target);   // self timer survived the drop
    }

    [Fact]
    public void PauseResume_GapLongerThanTheBuff_LeavesItTheTimeItHadLeft()
    {
        // Time out of the game is not counted against a buff: "buffs survive
        // disconnects, relogs", and "as soon as we've disconnected all timers need to
        // be paused until we're back in the game, then they all resume" (user,
        // 2026-10-10). Until that ruling this test pinned the opposite: a gap longer
        // than the buff dropped its timer on the way back in, and it was recast.
        using CureHarness h = new();
        h.SelfBuff("bles");
        h.BuffInfo["bles"] = (string.Empty, 300);
        h.Director.NoteManualBuffCast("bles");
        h.Now = h.Now.AddSeconds(100);             // 200 s left
        DateTime left = h.Now;

        h.Director.PauseBuffTimers();
        h.Now = h.Now.AddSeconds(400);             // longer out of the game than the buff lasts
        h.Director.ResumeBuffTimers();

        Game.Spells.ActiveBuffTimer kept = Assert.Single(h.Director.SnapshotActiveBuffs());
        Assert.Equal(200, (kept.Until - h.Now).TotalSeconds);
        Assert.Equal(left.AddSeconds(600), kept.Until);
    }

    // What a timer has left, by the clock the Buff Watchdog reads it against: the
    // moment the timers stopped while they stand still, else now.
    private static double SecondsLeft(CastingDirector director, DateTime now, string target, string code)
    {
        foreach (Game.Spells.ActiveBuffTimer t in director.SnapshotActiveBuffs())
            if (t.Target == target && t.Short == code)
                return (t.Until - (director.PausedAtUtc ?? now)).TotalSeconds;
        return double.NaN;
    }

    // Our own timer stands still while we are out. A party member's does not: the
    // member may have stayed in the game, where its buff ran down the whole time
    // (user, 2026-08-28), so its timer keeps its real expiry, and one that passed
    // while we were away is dropped and the buff cast again. The 2026-10-10 ruling
    // does not reach members' timers: "if the party member is still in the game when
    // we arent, its better to assume their timers keep ticking" (user, 2026-10-10).
    [Fact]
    public void PauseResume_OurOwnTimerStandsStill_APartyMembersRunsOn()
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.BuffInfo["mysh"] = (string.Empty, 200);
        h.AddMember("Raijin");
        h.Director.Evaluate();
        h.Confirm("You cast bless on Raijin!");
        h.Director.NoteManualBuffCast("mysh");
        h.Now = h.Now.AddSeconds(50);
        DateTime membersExpiry = h.Director.SnapshotActiveBuffs().Single(t => t.Target == "raijin").Until;

        h.Director.PauseBuffTimers();
        h.Now = h.Now.AddSeconds(100);
        Assert.Equal(150, SecondsLeft(h.Director, h.Now, "", "mysh"));
        (TimeSpan Away, string[] Moved)? resumed = null;
        h.Director.TimersResumed += (away, moved) => resumed = (away, moved.ToArray());
        h.Director.ResumeBuffTimers();

        Assert.Equal(150, SecondsLeft(h.Director, h.Now, "", "mysh"));
        Assert.Equal(membersExpiry, h.Director.SnapshotActiveBuffs().Single(t => t.Target == "raijin").Until);
        Assert.Equal(150, SecondsLeft(h.Director, h.Now, "raijin", "bles"));   // 250 at the pause, less the 100 away
        Assert.Equal(TimeSpan.FromSeconds(100), resumed!.Value.Away);
        Assert.Equal(new[] { "mysh" }, resumed.Value.Moved);
    }

    // A member's buff that ran out while we were away is dropped on the way back in,
    // and cast again; our own, with the same time on it, is not.
    [Fact]
    public void PauseResume_APartyMembersTimerThatRanOutWhileAway_IsDropped()
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.BuffInfo["mysh"] = (string.Empty, 300);
        h.AddMember("Raijin");
        h.Director.Evaluate();
        h.Confirm("You cast bless on Raijin!");
        h.Director.NoteManualBuffCast("mysh");

        h.Director.PauseBuffTimers();
        h.Now = h.Now.AddSeconds(1800);
        h.Director.ResumeBuffTimers();

        ActiveBuffTimer ours = Assert.Single(h.Director.SnapshotActiveBuffs());
        Assert.Equal(string.Empty, ours.Target);
        Assert.Equal(300, SecondsLeft(h.Director, h.Now, "", "mysh"));
    }

    // ----- a whole-party buff carried over time out of the game -------------
    //
    // One cast lands on us and on every member, and it has the one timer, keyed to
    // us. Carried over time out of the game that timer stood still, while the
    // members' buffs ran on: "if the party member is still in the game when we
    // arent, its better to assume their timers keep ticking" (user, 2026-10-10). So
    // the carried timer covers us and no member, and in a party the buff is recast
    // by the expiry the cast had with no time away.

    // In a party with chan cast `beforeLeaving` seconds ago (it lasts 300 s), no
    // recast lead, and the cast pass free to cast again.
    private static PartyBlessHarness InAPartyWithChanUp(int beforeLeaving)
    {
        PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddWholePartySlot("chan").RecastMarginSec = 0;
        h.BuffInfo["chan"] = (string.Empty, 300);
        h.AddMember("Raijin");
        h.Director.Evaluate();
        Assert.Equal(new[] { "chan" }, h.CastsSent);
        h.CastsSent.Clear();
        h.Now = h.Now.AddSeconds(beforeLeaving);
        return h;
    }

    private static List<string> CastsAfter(PartyBlessHarness h, int seconds)
    {
        h.Now = h.Now.AddSeconds(seconds);
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();
        h.Director.Evaluate();
        return h.CastsSent;
    }

    [Fact]
    public void WholePartyBuff_CarriedOverAPause_IsRecastForThePartyByTheClockThatDidNotStop()
    {
        using PartyBlessHarness h = InAPartyWithChanUp(beforeLeaving: 100);   // 200 s left on everyone
        DateTime castAt = h.Now.AddSeconds(-100);

        h.Director.PauseBuffTimers();
        h.Now = h.Now.AddSeconds(150);
        h.Director.ResumeBuffTimers();

        // Ours has the 200 s it had; the members' is down to 50 s.
        ActiveBuffTimer carried = Assert.Single(h.Director.SnapshotActiveBuffs());
        Assert.Equal(200, (carried.Until - h.Now).TotalSeconds);
        Assert.Equal(castAt.AddSeconds(300), carried.RanOnUntil);
        Assert.Contains(h.Log.Snapshot(), e => e.Message.Contains(
            "Whole-party buffs among them cover only us: in a party each is recast by the time it had left with no time away (chan 50 s)."));

        Assert.Empty(CastsAfter(h, 40));           // 10 s left on the members: not yet
        // Run out on the members, with 150 s still on our own bar. Going by that bar
        // left them without it for those 150 s.
        Assert.Equal(new[] { "chan" }, CastsAfter(h, 10));

        // The recast is a cast like any other: seen, not carried, and it covers the party.
        ActiveBuffTimer recast = Assert.Single(h.Director.SnapshotActiveBuffs());
        Assert.Null(recast.RanOnUntil);
        Assert.Equal(300, (recast.Until - h.Now).TotalSeconds);
        h.CastsSent.Clear();
        Assert.Empty(CastsAfter(h, 299));
    }

    // Away for longer than the buff had left: it is gone from the members on the way
    // back in, and cast at once, though our own bar has all it had.
    [Fact]
    public void WholePartyBuff_AwayLongerThanItHadLeft_IsRecastForThePartyOnTheWayBackIn()
    {
        using PartyBlessHarness h = InAPartyWithChanUp(beforeLeaving: 100);

        h.Director.PauseBuffTimers();
        h.Now = h.Now.AddSeconds(3600);
        h.Director.ResumeBuffTimers();

        Assert.Equal(200, SecondsLeft(h.Director, h.Now, "", "chan"));
        Assert.Equal(new[] { "chan" }, CastsAfter(h, 0));
    }

    // Alone, there is nobody whose buff ran on: the buff goes by our own timer, which
    // stood still.
    [Fact]
    public void WholePartyBuff_CarriedOverAPause_Alone_GoesByOurOwnTimer()
    {
        using PartyBlessHarness h = InAPartyWithChanUp(beforeLeaving: 100);

        h.Director.PauseBuffTimers();
        h.Now = h.Now.AddSeconds(3600);
        h.Party.Members.Clear();                   // the party did not survive the drop
        h.Party.IsInParty = false;
        h.Director.ResumeBuffTimers();

        Assert.Empty(CastsAfter(h, 199));
        Assert.Equal(new[] { "chan" }, CastsAfter(h, 1));
    }

    // A party joined later is covered by nobody's memory of the old cast: once the
    // cast has run out by the clock that did not stop, the buff is theirs to get.
    [Fact]
    public void WholePartyBuff_CarriedOverAPause_APartyJoinedLater_IsNotCountedAsCovered()
    {
        using PartyBlessHarness h = InAPartyWithChanUp(beforeLeaving: 100);
        h.Director.PauseBuffTimers();
        h.Now = h.Now.AddSeconds(3600);
        h.Party.Members.Clear();
        h.Party.IsInParty = false;
        h.Director.ResumeBuffTimers();
        Assert.Empty(CastsAfter(h, 20));           // alone, 180 s left on us

        h.AddMember("Raijin");

        Assert.Equal(new[] { "chan" }, CastsAfter(h, 0));
    }

    // The same after a restart: the timer put back from the profile is ours, and the
    // members' ran on from the moment the earlier session left the game.
    [Theory]
    [InlineData(30, 169, false)]    // a quick restart, 170 s left on the members: not yet
    [InlineData(30, 170, true)]     // ... and run out on them 170 s on, 30 s before our own
    [InlineData(7200, 0, true)]     // a restart two hours later: gone from them already
    public void WholePartyBuff_PutBackFromTheProfile_IsRecastForThePartyByTheClockThatDidNotStop(
        int awaySec, int inGameSec, bool recast)
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddWholePartySlot("chan").RecastMarginSec = 0;
        h.BuffInfo["chan"] = (string.Empty, 300);
        h.AddMember("Raijin");
        DateTime leftTheGame = h.Now.AddSeconds(-awaySec);

        h.Director.RestoreSelfTimers(new[]
        {
            new SavedBuffTimer
            {
                Short = "chan", RemainingSec = 200, MarginSec = 0, TotalSec = 300,
                RanOnUntilUtc = leftTheGame.AddSeconds(200),
            },
        });
        h.Director.ResumeBuffTimers();

        ActiveBuffTimer ours = Assert.Single(h.Director.SnapshotActiveBuffs());
        Assert.True(ours.Restored);
        Assert.Equal(200, (ours.Until - h.Now).TotalSeconds);
        Assert.Equal(leftTheGame.AddSeconds(200), ours.RanOnUntil);

        // inGameSec is under the 200 s our own timer has, so only the members' clock
        // can be what sends this.
        Assert.Equal(recast ? new[] { "chan" } : Array.Empty<string>(), CastsAfter(h, inGameSec));
    }

    // The clock that did not stop is counted from the cast through any number of
    // stretches away, and goes into the profile with the timer: a pause and then a
    // restart must not start it again from the second time the game was left.
    [Fact]
    public void WholePartyBuff_TheClockThatDidNotStop_IsKeptThroughAPause_AndSavedWithTheTimer()
    {
        using PartyBlessHarness h = InAPartyWithChanUp(beforeLeaving: 100);
        DateTime castAt = h.Now.AddSeconds(-100);
        h.Director.PauseBuffTimers();
        h.Now = h.Now.AddSeconds(60);
        h.Director.ResumeBuffTimers();
        h.Now = h.Now.AddSeconds(20);              // 180 s left on us, 120 s on the members

        h.Director.PauseBuffTimers();              // out again, and this time the client closes
        SavedBuffTimer saved = Assert.Single(h.Director.SnapshotSelfTimersForSave());

        Assert.Equal(180, saved.RemainingSec);
        Assert.Equal(castAt.AddSeconds(300), saved.RanOnUntilUtc);
    }

    // A self buff has nobody else on it: carried, it goes by our own timer whether
    // we are in a party or not.
    [Fact]
    public void SelfBuff_CarriedOverAPause_InAParty_GoesByOurOwnTimer()
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = "mysh", CastOnSelf = true, RecastMarginSec = 0 });
        h.BuffInfo["mysh"] = (string.Empty, 300);
        h.AddMember("Raijin");
        h.Director.Evaluate();
        Assert.Equal(new[] { "mysh" }, h.CastsSent);
        h.CastsSent.Clear();
        h.Now = h.Now.AddSeconds(100);

        h.Director.PauseBuffTimers();
        h.Now = h.Now.AddSeconds(3600);
        h.Director.ResumeBuffTimers();

        Assert.Empty(CastsAfter(h, 199));
        Assert.Equal(new[] { "mysh" }, CastsAfter(h, 1));
    }

    // A second drop before any prompt is the same stretch away: the first pause
    // stands, and all of the time since it is given back.
    [Fact]
    public void PauseTwice_ResumeOnce_GivesBackTheWholeStretch()
    {
        using CureHarness h = new();
        h.SelfBuff("bles");
        h.BuffInfo["bles"] = (string.Empty, 300);
        h.Director.NoteManualBuffCast("bles");

        h.Director.PauseBuffTimers();
        h.Now = h.Now.AddSeconds(500);
        h.Director.PauseBuffTimers();
        h.Now = h.Now.AddSeconds(500);
        h.Director.ResumeBuffTimers();

        Assert.Equal(300, SecondsLeft(h.Director, h.Now, "", "bles"));
    }

    // A buff whose line shows on the way back in, ahead of the first prompt, started
    // then: the time away is not added to it on top.
    [Fact]
    public void PauseResume_ABuffRaisedWhileStandingStill_IsNotMovedOn()
    {
        using CureHarness h = new();
        h.SelfBuff("bles");
        h.SelfBuff("mysh");
        h.BuffInfo["bles"] = (string.Empty, 300);
        h.BuffInfo["mysh"] = (string.Empty, 200);
        h.Director.NoteManualBuffCast("bles");

        h.Director.PauseBuffTimers();
        h.Now = h.Now.AddSeconds(600);
        h.Director.NoteManualBuffCast("mysh");
        h.Director.ResumeBuffTimers();

        Assert.Equal(300, SecondsLeft(h.Director, h.Now, "", "bles"));
        Assert.Equal(200, SecondsLeft(h.Director, h.Now, "", "mysh"));
    }

    // An item-cast buff's timer is one of them: after a stretch away longer than it
    // lasts the item is not used again until its own time has run.
    [Fact]
    public void PauseResume_AnItemCastBuffKeepsItsTime()
    {
        using CureHarness h = new();
        const string token = "#emerald tipped crozier";
        h.SelfBuff(token);
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Standing;
        int executed = 0;
        h.Director.SetItemCastSource(
            durationOf: t => t == token ? 600L : null,
            execute: _ => { executed++; return true; });
        h.Director.Evaluate();
        Assert.Equal(1, executed);

        h.Director.PauseBuffTimers();
        h.Now = h.Now.AddSeconds(5000);
        h.Director.ResumeBuffTimers();
        h.Cast.OnCombatTick();
        h.Director.Evaluate();
        Assert.Equal(1, executed);                 // still up: 600 s left

        h.Cast.OnCombatTick();
        h.Now = h.Now.AddSeconds(601);
        h.Director.Evaluate();
        Assert.Equal(2, executed);
    }

    // So is a draw item's card, and the timer still names the card.
    [Fact]
    public void PauseResume_ADrawnCardKeepsItsTime()
    {
        using PartyBlessHarness h = DeckHarness(out Func<int> uses);
        h.Director.Evaluate();
        h.Director.NoteDrawOutcome(CardRecord("card-priest", 966));

        h.Director.PauseBuffTimers();
        h.Now = h.Now.AddSeconds(5000);
        h.Director.ResumeBuffTimers();
        NextRound(h);

        Assert.Equal(1, uses());
        Game.Spells.ActiveBuffTimer card = Assert.Single(h.Director.SnapshotActiveBuffs());
        Assert.Equal("card-priest", card.Outcome);
    }

    // What a profile is saved with: the timers on ourselves, each with what it has
    // left. A party member's is left out (after a restart there is no knowing the
    // member was out of the game for the same stretch).
    [Fact]
    public void SnapshotForSave_KeepsOurOwnTimers_WithWhatEachHasLeft()
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.BuffInfo["mysh"] = (string.Empty, 200);
        h.AddMember("Raijin");
        h.Director.Evaluate();
        h.Confirm("You cast bless on Raijin!");
        h.Director.NoteManualBuffCast("mysh");
        h.Now = h.Now.AddSeconds(50);
        h.Director.PauseBuffTimers();
        h.Now = h.Now.AddSeconds(900);             // standing still: still 150 s

        SavedBuffTimer saved = Assert.Single(h.Director.SnapshotSelfTimersForSave());

        Assert.Equal("mysh", saved.Short);
        Assert.Equal(150, saved.RemainingSec);
        Assert.Equal(200, saved.TotalSec);
    }

    // Put back on a load, the timers stand still and nothing is cast for them or for
    // anything else until the game is entered; then each runs its remaining time, and
    // the buff is cast again when that has gone.
    [Fact]
    public void RestoredTimers_StandStillUntilTheGameIsEntered_ThenRunTheirRemainingTime()
    {
        using CureHarness h = new();
        h.AutoBlessEnabled = false;
        h.SelfBuff("bless");
        h.PartyBuffs.Slots.Single(s => s.Spell == "bless").RecastMarginSec = 0;
        h.BuffInfo["bless"] = (string.Empty, 300);
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Standing;
        h.AutoBlessEnabled = true;

        Assert.Equal(1, h.Director.RestoreSelfTimers(new[]
        {
            new SavedBuffTimer { Short = "bless", RemainingSec = 120, MarginSec = 0, TotalSec = 300 },
        }));
        Assert.Equal(h.Now, h.Director.PausedAtUtc);

        h.Now = h.Now.AddSeconds(7200);            // two hours before the game is entered
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);
        Assert.Equal(120, SecondsLeft(h.Director, h.Now, "", "bless"));

        h.Director.ResumeBuffTimers();
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);                 // 120 s left: not due
        h.Now = h.Now.AddSeconds(121);
        h.Director.Evaluate();
        Assert.Equal(new[] { "bless" }, h.CastsSent);
    }

    // A wear-off line ends only a buff the condition tracker holds as applied, and a
    // client started since the cast holds nothing. So the buff put back from a profile
    // is latched again when its timer runs on, and its wear-off line, with no applied
    // line before it, ends the timer. Without that the timer ran to its end over a
    // buff that had gone.
    [Fact]
    public void RestoredTimer_IsEndedByItsWearOffLineAlone()
    {
        using CureHarness h = new();
        h.AutoBlessEnabled = false;
        h.SelfBuff("bless");
        h.BuffInfo["bless"] = (string.Empty, 300);
        h.RecordCondition("bless", MessageFlags.None,
            applied: "You are blessed!", endsWith: "Your blessing fades.");
        h.Director.RestoreSelfTimers(new[]
        {
            new SavedBuffTimer { Short = "bless", RemainingSec = 120, MarginSec = 0, TotalSec = 300 },
        });
        h.Director.ResumeBuffTimers();

        h.FeedLine("Your blessing fades.");

        Assert.Empty(h.Director.SnapshotActiveBuffs());
    }

    // Within one run the same: the disconnect handler clears the condition tracker
    // (right for ailments), the buff stays on the character, and its wear-off line
    // after the reconnect still ends its timer.
    [Fact]
    public void TimerCarriedAcrossADisconnect_IsEndedByItsWearOffLine()
    {
        using CureHarness h = new();
        h.AutoBlessEnabled = false;
        h.SelfBuff("bless");
        h.BuffInfo["bless"] = (string.Empty, 300);
        h.RecordCondition("bless", MessageFlags.None,
            applied: "You are blessed!", endsWith: "Your blessing fades.");
        h.FeedLine("You are blessed!");
        Assert.Single(h.Director.SnapshotActiveBuffs());
        h.Now = h.Now.AddSeconds(100);

        h.Conditions.ClearAll("disconnect");       // MainWindowViewModel's disconnect handler
        h.Director.PauseBuffTimers();
        h.Now = h.Now.AddSeconds(3600);
        h.Director.ResumeBuffTimers();
        Assert.Equal(200, SecondsLeft(h.Director, h.Now, "", "bless"));
        h.Now = h.Now.AddSeconds(50);
        h.FeedLine("Your blessing fades.");

        Assert.Empty(h.Director.SnapshotActiveBuffs());
    }

    // A buff that shares its wear-off text with the carried one, and was not on, is
    // not latched with it: only the buff whose timer was carried is.
    [Fact]
    public void CarriedTimer_LatchesOnlyItsOwnBuff()
    {
        using CureHarness h = new();
        h.AutoBlessEnabled = false;
        h.SelfBuff("bless");
        h.BuffInfo["bless"] = (string.Empty, 300);
        h.RecordCondition("bless", MessageFlags.None, applied: "You feel lucky!", endsWith: "Your luck fades.");
        h.RecordCondition("chant", MessageFlags.None, applied: "You feel lucky!", endsWith: "Your luck fades.");
        h.Director.RestoreSelfTimers(new[]
        {
            new SavedBuffTimer { Short = "bless", RemainingSec = 120, MarginSec = 0, TotalSec = 300 },
        });

        h.Director.ResumeBuffTimers();

        Assert.True(h.Conditions.IsActiveByName("bless"));
        Assert.False(h.Conditions.IsActiveByName("chant"));
    }

    // A draw item's card is carried the same way: its wear-off line after a
    // disconnect makes the slot due again.
    [Fact]
    public void DrawnCardCarriedAcrossADisconnect_IsEndedByItsWearOffLine()
    {
        using CureHarness h = new();
        h.AutoBlessEnabled = false;
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = Deck, CastOnSelf = true, RecastMarginSec = 15 });
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Standing;
        int uses = 0;
        h.Director.SetItemCastSource(durationOf: _ => null, execute: _ => { uses++; return true; });
        h.Director.SetItemDrawSource(token => token == Deck ? new[] { (966, "card-priest", 720L) } : null);
        h.RecordCondition("card-priest", MessageFlags.None,
            applied: "You draw the priest.", endsWith: "The priest's favour leaves you.");
        h.AutoBlessEnabled = true;
        h.Director.Evaluate();
        Assert.Equal(1, uses);
        h.FeedLine("You draw the priest.");
        Assert.Equal("card-priest", Assert.Single(h.Director.SnapshotActiveBuffs()).Outcome);

        h.Conditions.ClearAll("disconnect");
        h.Director.PauseBuffTimers();
        h.Now = h.Now.AddSeconds(3600);
        h.Director.ResumeBuffTimers();
        Assert.Single(h.Director.SnapshotActiveBuffs());

        h.FeedLine("The priest's favour leaves you.");

        Assert.Empty(h.Director.SnapshotActiveBuffs());
    }

    // A death the client did not see, learned of after it is back in the game, wiped
    // the buffs that were on when the game was left and none raised since: exactly
    // the carried timers go.
    [Fact]
    public void ClearCarried_TakesTheCarriedTimers_AndLeavesTheOnesRaisedSince()
    {
        using CureHarness h = new();
        h.AutoBlessEnabled = false;
        h.SelfBuff("bles");
        h.SelfBuff("mysh");
        h.SelfBuff("prot");
        h.BuffInfo["bles"] = (string.Empty, 300);
        h.BuffInfo["mysh"] = (string.Empty, 200);
        h.BuffInfo["prot"] = (string.Empty, 400);
        h.Director.RestoreSelfTimers(new[]
        {
            new SavedBuffTimer { Short = "bles", RemainingSec = 120, MarginSec = 0, TotalSec = 300 },
            new SavedBuffTimer { Short = "mysh", RemainingSec = 90, MarginSec = 0, TotalSec = 200 },
        });
        h.Director.ResumeBuffTimers();
        Assert.All(h.Director.SnapshotActiveBuffs(), t => Assert.True(t.Restored));
        h.Director.NoteManualBuffCast("mysh");     // raised again since
        h.Director.NoteManualBuffCast("prot");     // new since

        Assert.Equal(1, h.Director.ClearCarriedSelfTimers("a death found late"));

        Assert.Equal(new[] { "mysh", "prot" },
            h.Director.SnapshotActiveBuffs().Select(t => t.Short).OrderBy(s => s).ToArray());
        Assert.All(h.Director.SnapshotActiveBuffs(), t => Assert.False(t.Restored));
        Assert.Equal(0, h.Director.ClearCarriedSelfTimers("again"));
    }

    // A death found at the login (a hang-up death) wipes what was put back.
    [Fact]
    public void RestoredTimers_AreWipedByADeath()
    {
        using CureHarness h = new();
        h.Director.RestoreSelfTimers(new[]
        {
            new SavedBuffTimer { Short = "bless", RemainingSec = 120, MarginSec = 0, TotalSec = 300 },
        });

        h.Director.ClearSelfBuffTracking();

        Assert.Empty(h.Director.SnapshotActiveBuffs());
        Assert.Empty(h.Director.SnapshotSelfTimersForSave());
    }

    [Fact]
    public void PausedAtUtc_ReflectsFreezeState()
    {
        // The Buff Watchdog reads PausedAtUtc to freeze its display at the drop instant
        // (its heartbeat is a wall clock that keeps ticking while disconnected).
        using CureHarness h = new();
        h.SelfBuff("bles");
        h.BuffInfo["bles"] = (string.Empty, 300);
        h.Director.NoteManualBuffCast("bles");
        Assert.Null(h.Director.PausedAtUtc);          // running

        h.Director.PauseBuffTimers();
        Assert.Equal(h.Now, h.Director.PausedAtUtc);  // frozen at the drop instant

        h.Now = h.Now.AddSeconds(20);
        h.Director.ResumeBuffTimers();
        Assert.Null(h.Director.PausedAtUtc);           // running again
    }

    [Fact]
    public void PauseBuffTimers_NoTimers_StaysUnfrozen()
    {
        // Nothing armed ⇒ nothing to freeze, so the watchdog display keeps live time.
        using CureHarness h = new();
        h.Director.PauseBuffTimers();
        Assert.Null(h.Director.PausedAtUtc);
    }

    [Fact]
    public void Suspend_HoldsBuffsUntilFirstInGamePrompt()
    {
        // A disconnect (PauseBuffTimers) latches the loop off — the cast must stay
        // silent even after the socket reconnects, until the first in-game prompt
        // (ResumeBuffTimers) lifts the latch, so buffs don't fire onto the BBS
        // login/menu during re-entry (report paradigm-20260908-053448).
        using CureHarness h = new();
        h.AutoBlessEnabled = false;               // no cast during setup's reactive passes
        h.SelfBuff("bless");
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Standing;
        h.AutoBlessEnabled = true;                // eligible, not yet evaluated

        h.Director.PauseBuffTimers();             // disconnect
        h.Director.Evaluate();                    // 1s heartbeat while at the login/menu
        Assert.Empty(h.CastsSent);                // suspended — nothing onto the login prompt

        h.Director.ResumeBuffTimers();            // first in-game prompt clears the latch
        h.Director.Evaluate();
        Assert.Single(h.CastsSent);               // back in the game world — buff fires
        Assert.Equal("bless", h.CastsSent[0]);
    }

    [Fact]
    public void SelfBuff_CoveredByPartyBuff_IsNotCast()
    {
        // In a party, a party-wide buff (chan) that removes bless supersedes the self-cast:
        // the director skips self-casting bles and lets the party buff cover us.
        using CureHarness h = new();
        h.SelfBuff("bles");
        h.BuffInfo["bles"] = (string.Empty, 300);
        h.ManaFloor = 0;
        // Coverage set BEFORE the state changes, so the setup's auto-eval already skips bles.
        h.Director.SetSelfBuffCoverage(() => new Dictionary<string, string> { ["bles"] = "chan" });
        h.State.MaxMa = 100; h.State.Ma = 100; h.State.InCombat = false;

        h.Director.Evaluate();

        Assert.DoesNotContain("bles", h.CastsSent);
        Assert.Equal("chan", h.Director.CurrentSelfBuffCoverage()["bles"]);
    }

    [Fact]
    public void SelfBuff_NotCovered_IsCastNormally()
    {
        // No coverage (solo, or nothing removes it) ⇒ the self-buff casts as usual.
        using CureHarness h = new();
        h.SelfBuff("bles");
        h.BuffInfo["bles"] = (string.Empty, 300);
        h.ManaFloor = 0;
        h.State.MaxMa = 100; h.State.Ma = 100; h.State.InCombat = false;

        h.Director.Evaluate();

        Assert.Contains("bles", h.CastsSent);
    }

    [Fact]
    public void Buff_BlessIfAboveMa_AbsoluteMode_UsesRawMa()
    {
        // In Absolute mode BlessIfAboveMa is a raw kai count, not a percent —
        // a Kai class with a 5-kai pool blesses at "4 kai or more", not "4%".
        using CureHarness h = new();
        h.SelfBuff("tige");
        h.BuffInfo["tige"] = (string.Empty, 300);
        h.Health.MaThresholdMode = ThresholdMode.Absolute;
        h.ManaFloor = 4;
        h.State.MaxMa = 5;
        h.State.InCombat = false;
        h.RecordCondition("tige", MessageFlags.None, applied: "You feel invigorated.");

        h.State.Ma = 3;                        // below the absolute floor
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);

        h.State.Ma = 4;                        // clears the floor
        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
        Assert.Equal("tige", h.CastsSent[0]);
    }

    [Fact]
    public void Buff_BelowMaFloor_NoCast()
    {
        // MA too low — saving for heals.
        using CureHarness h = new();
        h.SelfBuff("bless");
        h.ManaFloor = 70;
        h.State.MaxMa = 100;
        h.State.Ma = 50;
        h.State.InCombat = false;

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    // ----- Sneak-maintenance defer (hold buffs/cures for an empty room) -------

    [Fact]
    public void Buff_SneakMaintenanceDefer_Held()
    {
        // A stealth runner walking combat-off through an occupied room: a due buff
        // is HELD (not cast) so the cast doesn't strip sneak in a room we can't
        // re-sneak in. It fires once the room clears (gate goes false).
        using CureHarness h = new();
        h.DeferMaintenanceForStealth = true;
        h.SelfBuff("bless");
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Standing;

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void Buff_SneakMaintenanceDefer_Off_Casts()
    {
        // Control: with the defer gate off (not a stealth runner / empty room) the
        // same due buff casts normally.
        using CureHarness h = new();
        h.DeferMaintenanceForStealth = false;
        h.SelfBuff("bless");
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Standing;

        h.Director.Evaluate();

        Assert.Equal(new[] { "bless" }, h.CastsSent);
    }

    [Fact]
    public void Buff_SneakKeeping_HoldsEvenWhileResting()
    {
        // The director no longer skips the hold while resting: the gate (SneakGuard)
        // only holds while a sneak is actually kept, which a normal rest has already
        // ended — but a ShadowRest rest keeps it, beside hostiles, and a cast there
        // would give the character away.
        using CureHarness h = new();
        h.DeferMaintenanceForStealth = true;
        h.SelfBuff("bless");
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Resting;

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void EmergencyHeal_SneakKeeping_GoesOutWhileFleeingOnTheGates()
    {
        // Fleeing on the run-if-below gates, the emergency heal isn't held for the
        // sneak; the re-sneak waits for it instead (user, 2026-09-28).
        using CureHarness h = new();
        h.DeferMaintenanceForStealth = true;
        bool gateFleeing = false;
        h.Director.SetEmergencyHealBypassProbe(() => gateFleeing);
        h.Spells.EmergencyHealSpell = "lastresort";
        h.Health.EmergencyHealTrigger = 20;
        h.State.MaxHp = 100;
        h.State.Hp = 15;
        h.State.MaxMa = 100;
        h.State.Ma = 100;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Standing;

        Assert.True(h.Director.IsEmergencyHealDue);
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);

        gateFleeing = true;
        h.Director.Evaluate();
        Assert.Equal(new[] { "lastresort" }, h.CastsSent);
    }

    [Fact]
    public void Cure_SneakMaintenanceDefer_Held()
    {
        // Cures defer too (user chose max sneak preservation): a poison cure is
        // held while sneak-walking an occupied room. Heals are held the same way
        // (user, 2026-09-28); the guard stands down once we're in a fight.
        using CureHarness h = new();
        h.DeferMaintenanceForStealth = true;
        h.State.Position = PlayerPosition.Standing;
        h.Spells.CurePoisonSpell = "neutralize";
        h.RecordCondition("Poison", MessageFlags.Poisoned, "poisoned!");

        h.FeedLine("You have been poisoned!");

        Assert.Empty(h.CastsSent);
    }

    // ----- Draw items (a deck of cards) -------------------------------
    // One use deals one of several buffs at random. A ticked one is held for its own
    // duration; an unticked one is drawn again on the next cycle.

    private static MessageRecord CardRecord(string name, int spell) => new(
        Id: MessageRecord.ComputeId(name, "", "", "", name, ""),
        Name: name, Flags: MessageFlags.None, RawFlagsHex: 0,
        CasterMessage: string.Empty, TargetMessage: string.Empty, WitnessMessage: string.Empty,
        AppliedMessage: name, AppliedEndsWith: string.Empty,
        Links: new[] { new GameDataLink("Spells", spell) });

    private const string Deck = "#deck of cards";

    // One combat round later: the between-round slot is free again.
    private static void NextRound(PartyBlessHarness h)
    {
        h.Now = h.Now.AddSeconds(5);
        h.Cast.OnCombatTick();
        h.Director.Evaluate();
    }

    private static PartyBlessHarness DeckHarness(out Func<int> uses, params int[] rejected)
    {
        PartyBlessHarness h = new();
        BuffSlot slot = new() { Spell = Deck, CastOnSelf = true, RecastMarginSec = 15 };
        slot.RejectedOutcomes.AddRange(rejected);
        h.PartyBuffs.Slots.Add(slot);
        int count = 0;
        h.Director.SetItemCastSource(durationOf: _ => null, execute: _ => { count++; return true; });
        h.Director.SetItemDrawSource(token => token == Deck
            ? new[] { (965, "card-wizard", 720L), (966, "card-priest", 720L) }
            : null);
        uses = () => count;
        return h;
    }

    [Fact]
    public void DrawItem_RedrawsUntilATickedCardLands_ThenHoldsItForItsDuration()
    {
        using PartyBlessHarness h = DeckHarness(out Func<int> uses, rejected: 965);

        h.Director.Evaluate();
        Assert.Equal(1, uses());
        Assert.Empty(h.CastsSent);                        // a use, not a cast

        // The wizard isn't ticked: the next round's slot draws again.
        h.Director.NoteDrawOutcome(CardRecord("card-wizard", 965));
        NextRound(h);
        Assert.Equal(2, uses());

        // The priest is: it is kept.
        h.Director.NoteDrawOutcome(CardRecord("card-priest", 966));
        NextRound(h);
        Assert.Equal(2, uses());

        // Its own duration (720 s) less the slot's recast margin (15 s).
        h.Now = h.Now.AddSeconds(685);
        NextRound(h);
        Assert.Equal(2, uses());
        h.Now = h.Now.AddSeconds(15);
        NextRound(h);
        Assert.Equal(3, uses());
    }

    // The other realm's card record shares the Id, so the one that fires may link a
    // different spell number: the card is still known by its name.
    [Fact]
    public void DrawItem_ACardIsKnownByNameWhenItsRecordLinksTheOtherRealmsSpell()
    {
        using PartyBlessHarness h = DeckHarness(out Func<int> uses);
        h.Director.Evaluate();

        h.Director.NoteDrawOutcome(CardRecord("card-priest", 491));
        NextRound(h);
        NextRound(h);
        Assert.Equal(1, uses());                          // held
    }

    [Fact]
    public void DrawItem_NoCardSeen_DrawsAgainOnceTheSettleWindowIsOver()
    {
        using PartyBlessHarness h = DeckHarness(out Func<int> uses);
        h.Director.Evaluate();

        NextRound(h);
        Assert.Equal(1, uses());                          // still waiting for the card

        NextRound(h);
        Assert.Equal(2, uses());
    }

    [Fact]
    public void DrawItem_TheCardWearingOff_MakesTheSlotDue()
    {
        using PartyBlessHarness h = DeckHarness(out Func<int> uses);
        h.Director.Evaluate();
        h.Director.NoteDrawOutcome(CardRecord("card-priest", 966));

        NextRound(h);
        Assert.Equal(1, uses());

        h.Director.NoteDrawEnded(CardRecord("card-priest", 966));
        NextRound(h);
        Assert.Equal(2, uses());
    }

    // Stock's deck answers "Nothing happens." while a card is up, so it can't choose:
    // whatever it deals is kept, an unticked card included, and no recast lead sends
    // a use before the card has worn off (user, 2026-10-02).
    [Fact]
    public void DrawItem_ThatCantRedraw_KeepsWhateverItDeals_UntilItHasWornOff()
    {
        using PartyBlessHarness h = DeckHarness(out Func<int> uses, rejected: 965);
        h.Director.SetItemDrawSource(
            token => token == Deck ? new[] { (965, "card-wizard", 720L), (966, "card-priest", 720L) } : null,
            canRedraw: _ => false);

        h.Director.Evaluate();
        h.Director.NoteDrawOutcome(CardRecord("card-wizard", 965));   // unticked, kept all the same
        NextRound(h);
        Assert.Equal(1, uses());
        Assert.Equal(0, Assert.Single(h.Director.SnapshotActiveBuffs()).MarginSec);

        // Inside the slot's 15 s recast lead: still held.
        h.Now = h.Now.AddSeconds(705);
        NextRound(h);
        Assert.Equal(1, uses());

        h.Now = h.Now.AddSeconds(15);
        NextRound(h);
        Assert.Equal(2, uses());
    }

    // No card after a use on such an item most likely means a card was still up, and
    // the refused use still cost a charge. It waits three minutes before the next.
    [Fact]
    public void DrawItem_ThatCantRedraw_NoCardSeen_WaitsBeforeTryingAgain()
    {
        using PartyBlessHarness h = DeckHarness(out Func<int> uses);
        h.Director.SetItemDrawSource(
            token => token == Deck ? new[] { (965, "card-wizard", 720L), (966, "card-priest", 720L) } : null,
            canRedraw: _ => false);
        h.Director.Evaluate();

        for (int i = 0; i < 35; i++) NextRound(h);        // 175 s
        Assert.Equal(1, uses());

        for (int i = 0; i < 2; i++) NextRound(h);         // past the three minutes
        Assert.Equal(2, uses());
    }

    // A slot that keeps several cards doesn't say which one is up: the timer carries
    // the card's name for the Buff Watchdog row.
    [Fact]
    public void DrawItem_TheTimerNamesTheCardThatLanded_UntilItWearsOffOrIsDrawnOver()
    {
        using PartyBlessHarness h = DeckHarness(out Func<int> _);
        h.Director.Evaluate();
        h.Director.NoteDrawOutcome(CardRecord("card-priest", 966));

        Assert.Equal("card-priest", Assert.Single(h.Director.SnapshotActiveBuffs()).Outcome);

        h.Director.NoteDrawEnded(CardRecord("card-priest", 966));
        NextRound(h);                                     // draws again
        h.Director.NoteDrawOutcome(CardRecord("card-wizard", 965));

        Assert.Equal("card-wizard", Assert.Single(h.Director.SnapshotActiveBuffs()).Outcome);
    }

    // A re-draw can deal the card that was already up (user, 2026-10-02). The line
    // tracker latches a record as applied until its wear-off, so without a release at
    // each draw the same card would land unseen and the slot would draw for ever.
    [Fact]
    public void DrawItem_TheSameCardAgain_IsStillRecognised()
    {
        using CureHarness h = new();
        h.Messages.Messages.Add(new MessageRecord(
            Id: "card-priest-id", Name: "card-priest", Flags: MessageFlags.None, RawFlagsHex: 0,
            CasterMessage: string.Empty, TargetMessage: string.Empty, WitnessMessage: string.Empty,
            AppliedMessage: "When the Priest is played",
            AppliedEndsWith: "The gaze of luck is no longer upon you",
            Links: new[] { new GameDataLink("Spells", 966) }));
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = Deck, CastOnSelf = true, RecastMarginSec = 15 });
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Standing;
        int uses = 0;
        h.Director.SetItemCastSource(durationOf: _ => null, execute: _ => { uses++; return true; });
        h.Director.SetItemDrawSource(token => token == Deck ? new[] { (966, "card-priest", 720L) } : null);
        const string priest = "\"When the Priest is played, wisdom and insight are paramount. Faith and";

        h.Director.Evaluate();
        h.FeedLine(priest);
        Assert.Equal(1, uses);

        // The card runs out by the clock with no wear-off line seen: draw again.
        h.Now = h.Now.AddSeconds(730);
        h.Cast.OnCombatTick();
        h.Director.Evaluate();
        Assert.Equal(2, uses);

        // The priest again. It has to count, or the slot is due once the wait ends.
        h.FeedLine(priest);
        h.Now = h.Now.AddSeconds(20);
        h.Cast.OnCombatTick();
        h.Director.Evaluate();
        Assert.Equal(2, uses);
    }

    // ----- Item-cast buffs -------------------------------------------

    [Fact]
    public void ItemCastBuff_FiresSequence_BypassesRawCast_AndKeysRecastByToken()
    {
        using CureHarness h = new();
        const string token = "#emerald tipped crozier";
        h.SelfBuff(token);
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Standing;

        int executed = 0;
        h.Director.SetItemCastSource(
            durationOf: t => t == token ? 600L : null,
            execute: _ => { executed++; return true; });

        h.Director.Evaluate();

        Assert.Equal(1, executed);
        Assert.Empty(h.CastsSent); // item-cast bypasses the raw cast path

        // Recast timer keyed by the token: still active → not re-fired.
        h.Cast.OnCombatTick();     // clear the round cooldown
        h.Director.Evaluate();
        Assert.Equal(1, executed);

        // Past the buff duration → due again.
        h.Cast.OnCombatTick();
        h.Now = h.Now.AddSeconds(601);
        h.Director.Evaluate();
        Assert.Equal(2, executed);
    }

    [Fact]
    public void ItemCastBuff_NonBuffItem_NotFired()
    {
        using CureHarness h = new();
        const string token = "#wand of fire";
        h.SelfBuff(token);
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Standing;

        int executed = 0;
        // A damage wand has no duration → unresolvable → never fired.
        h.Director.SetItemCastSource(
            durationOf: _ => null,
            execute: _ => { executed++; return true; });

        h.Director.Evaluate();

        Assert.Equal(0, executed);
        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void ItemCastBuff_Free_FiresBelowManaFloor()
    {
        using CureHarness h = new();
        const string token = "#emerald tipped crozier";
        h.SelfBuff(token);
        // MA below the bless floor: a mana-drawing buff would be suppressed.
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 10;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Standing;

        int executed = 0;
        h.Director.SetItemCastSource(
            durationOf: t => t == token ? 600L : null,
            execute: _ => { executed++; return true; });
        h.Director.SetItemCastManaCost(_ => 0); // free use-spell

        h.Director.Evaluate();

        // Free item-cast bypasses the mana floor entirely.
        Assert.Equal(1, executed);
    }

    [Fact]
    public void ItemCastBuff_Paid_HeldUntilAffordable()
    {
        using CureHarness h = new();
        const string token = "#shimmering greatsword";
        h.SelfBuff(token);
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Standing;

        int executed = 0;
        h.Director.SetItemCastSource(
            durationOf: t => t == token ? 600L : null,
            execute: _ => { executed++; return true; });
        h.Director.SetItemCastManaCost(_ => 8); // paid use-spell (8 mana)

        // Below the bless floor → held even though 8 mana is affordable.
        h.State.Ma = 10;
        h.Director.Evaluate();
        Assert.Equal(0, executed);

        // Above the floor and enough to pay → fires.
        h.Cast.OnCombatTick(); // clear any round cooldown
        h.State.Ma = 80;
        h.Director.Evaluate();
        Assert.Equal(1, executed);
    }

    // ----- Utility (regen buffs + idle-fallback) --------------------

    [Fact]
    public void Utility_HpRegenSpell_NotCastAsBuff_WhenHpFull()
    {
        // The HP-regen slot is assisted healing, not a downtime buff: at full
        // HP with nothing to heal, the buff path must NOT fire it. (A user who
        // wants it always-up puts it in a Bless slot instead.)
        using CureHarness h = new();
        h.Spells.HpRegenSpell = "trollskin";
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.MaxHp = 200;
        h.State.Hp = 200;                 // full → minor-heal path never trips
        h.State.InCombat = false;

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void Utility_MaRegenSlotAfterBless1To10()
    {
        // Bless1 configured + active → the mana-regen downtime buff is the next
        // non-active self-buff slot (HP-regen is no longer a buff slot).
        using CureHarness h = new();
        h.SelfBuff("bless");
        h.SelfBuff("kindred");
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;

        h.BuffInfo["bless"] = (string.Empty, 300);
        h.RecordCondition("bless", MessageFlags.None,
            applied: "You are blessed!", endsWith: "Your blessing fades.");
        h.FeedLine("You are blessed!");
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();   // new round frees the between-round slot

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("kindred", h.CastsSent[0]);
    }

    [Fact]
    public void Utility_WhenHpFullSpell_FiresInBuffSlot()
    {
        // WhenHpFull is a buff with extra eligibility (HP at max).
        // Out-of-combat, MA above BlessIfAboveMa, HP at max → fires.
        using CureHarness h = new();
        h.SelfBuff("detect").OnlyWhenHpFull = true;
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.MaxHp = 200;
        h.State.Hp = 200;
        h.State.InCombat = false;

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("detect", h.CastsSent[0]);
    }

    [Fact]
    public void Utility_WhenHpFull_InCombat_NoCast()
    {
        // Buff path hard-gates out of combat; the WhenHpFull slot
        // inherits that gate.
        using CureHarness h = new();
        h.State.InCombat = true;
        h.SelfBuff("detect").OnlyWhenHpFull = true;
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void Utility_WhenHpFull_HpBelowMax_NoFire()
    {
        using CureHarness h = new();
        // Drop HP first so subsequent PropertyChangeds never see
        // the HP-at-max + spell-configured combo and don't fire
        // through the buff path before our explicit Evaluate().
        h.State.Hp = 150;
        h.SelfBuff("detect").OnlyWhenHpFull = true;
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;

        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void Utility_WhenHpFullSpell_AlreadyActive_NoCast()
    {
        using CureHarness h = new();
        h.SelfBuff("detect").OnlyWhenHpFull = true;
        h.ManaFloor = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.MaxHp = 200;
        h.State.Hp = 200;

        h.BuffInfo["detect"] = (string.Empty, 300);
        h.RecordCondition("detect", MessageFlags.None,
            applied: "You can see hidden!", endsWith: "Your detection fades.");
        h.FeedLine("You can see hidden!");
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();

        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void MinorHeal_HpRegenHoTActive_FallsThroughToSingleTargetHeal()
    {
        // HoT-first on the trigger, then — once it's confirmed ticking with
        // remaining duration — the minor path drops to the instant single-
        // target heal for the immediate top-up while the HoT works.
        using CureHarness h = new();
        h.Spells.HpRegenSpell = "regen";
        h.Spells.MinorHealSpell = "heal";
        h.Health.MinorHealCombatTrigger = 70;
        h.Health.MajorHealCombatTrigger = 40;
        h.BuffInfo["regen"] = (string.Empty, 300);
        h.RecordCondition("regen", MessageFlags.None,
            applied: "You begin to regenerate.", endsWith: "Your regeneration fades.");

        // Drop into the routine band last so the HoT-first cast fires cleanly.
        h.State.MaxHp = 100;
        h.State.InCombat = true;
        h.State.Hp = 60;
        Assert.Equal("regen", h.CastsSent[^1]);

        // Confirm the HoT landed → 300s timer → no longer recast-due.
        h.FeedLine("You begin to regenerate.");
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();      // new round → between-round slot free

        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
        Assert.Equal("heal", h.CastsSent[0]);
    }

    // ----- Party-heal (single + AOE thresholding) -------------------

    private sealed class PartyHarness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public PlayerState State { get; } = new();
        public PartyState Party { get; } = new();
        public CastCoordinator Cast { get; }
        public CastingDirector Director { get; }
        public List<string> CastsSent { get; } = new();
        public List<byte[]> SentBytes { get; } = new();
        public SpellsSettings Spells { get; set; } = new();
        public HealthSettings Health { get; set; } = new();
        public PartySettings PartySettings { get; set; } = new();
        // The director's clock: the real one unless a test sets its own.
        public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

        public PartyHarness()
        {
            DefaultPatterns.Seed(Router);
            Cast = new CastCoordinator(Router, Log);
            Cast.SetWireSender(b => SentBytes.Add(b));
            Cast.CastSent += CastsSent.Add;
            Director = new CastingDirector(State, Cast,
                conditions: null, party: Party,
                readSpells: () => Spells,
                readHealth: () => Health,
                readPartySettings: () => PartySettings,
                isEnabled: () => true,
                log: Log,
                now: () => Clock());
            State.MaxHp = 200;
            State.Hp = 200;
            State.MaxMa = 100;
            State.Ma = 100;
            State.HasPromptData = true;
        }

        public PartyMember AddMember(string name, int hpPercent, int baselineHp = 100)
        {
            PartyMember m = new()
            {
                Name = name,
                BaselineHp = baselineHp,
                HpPercent = hpPercent,
                BaselineMp = 100,
                MpPercent = 100,
            };
            Party.Members.Add(m);
            Party.IsInParty = true;   // a member present ⇒ in a party (party-heal is party-only)
            return m;
        }

        public void Dispose()
        {
            Director.Dispose();
            Cast.Dispose();
        }
    }

    [Fact]
    public void PartyHeal_OneMemberBelowMinor_SingleCast()
    {
        using PartyHarness h = new();
        h.PartySettings.MinorPartyHealSpell = "heal";
        h.PartySettings.MinorHealMemberThresholdPercent = 70;
        h.AddMember("Tank", hpPercent: 60);

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("heal Tank", h.CastsSent[0]);
    }

    // Report paradigm-20260820-122341 ("party heal fires a round late"): the heal
    // picker reads each member's HpPercent, but that value is refreshed by the `par`
    // poll on its own cadence. Before the member-watch, a member falling below the
    // heal threshold wasn't acted on until the next self-state change or combat tick —
    // a full round of the member sitting hurt. The director now watches each member's
    // HpPercent and re-runs the pipeline the instant it drops, with NO self-state
    // change and NO tick.
    [Fact]
    public void PartyHeal_MemberHpDrops_HealsImmediately_NoTickOrSelfChange()
    {
        using PartyHarness h = new();
        h.PartySettings.MinorPartyHealSpell = "heal";
        h.PartySettings.MinorHealMemberThresholdPercent = 70;
        PartyMember tank = h.AddMember("Tank", hpPercent: 100);   // joined, healthy

        Assert.Empty(h.CastsSent);                                // nothing due yet

        tank.HpPercent = 50;                                      // `par` refresh drops it below 70

        // Driven purely by the member-watch — no Evaluate()/tick/SetPrompt call.
        Assert.Single(h.CastsSent);
        Assert.Equal("heal Tank", h.CastsSent[0]);
    }

    [Fact]
    public void PartyHeal_SelfIsLowest_SkipsSelf_NoSingleTargetCast()
    {
        // The single-target party heal never targets self: some heals can only be cast on
        // OTHERS (e.g. anno / annointed hands) and a self-target sends a bare self-cast the
        // game rejects (report paradigm-20260922-082041); self is covered by the self-heal
        // slots. With only self below threshold and no AOE configured, nothing fires.
        using PartyHarness h = new();
        h.PartySettings.MinorPartyHealSpell = "mihe";
        h.PartySettings.MinorHealMemberThresholdPercent = 70;
        PartyMember self = h.AddMember("Raijin Par", hpPercent: 55);
        self.IsSelf = true;

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyHeal_OtherMemberWithFamilyName_TargetsGivenNameOnly()
    {
        // A member's par-row name is "Given Family" ("Raijin Par").
        // MajorMUD targets a cast by first-name token, so the family word
        // is stripped — cast "mihe Raijin", not "mihe Raijin Par".
        using PartyHarness h = new();
        h.PartySettings.MinorPartyHealSpell = "mihe";
        h.PartySettings.MinorHealMemberThresholdPercent = 70;
        h.AddMember("Raijin Par", hpPercent: 55);

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("mihe Raijin", h.CastsSent[0]);
    }

    [Fact]
    public void PartyHeal_SelfOnlyBelow_NeverFiresPartyHeal_SingleAndAoeConfigured()
    {
        // Regression (report paradigm-20260922-203540): only SELF is below (allies
        // full) — 1 of 3, under AoeMinMembers = 2. Party heals are for MEMBERS only,
        // so a lone self-dip fires neither the single-target NOR the group AOE; the
        // self-heal slots cover self. Both spells configured, neither fires.
        using PartyHarness h = new();
        h.PartySettings.MinorPartyHealSpell = "anno";
        h.PartySettings.MinorPartyHealAoeSpell = "rain";
        h.PartySettings.MinorHealMemberThresholdPercent = 95;
        h.PartySettings.AoeMinMembers = 2;
        PartyMember self = h.AddMember("Cidir", hpPercent: 64);
        self.IsSelf = true;
        h.AddMember("Nineteen", hpPercent: 100);
        h.AddMember("Thresh", hpPercent: 100);

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyHeal_SelfOnlyBelow_NeverFiresPartyHeal_AoeOnlyConfigured()
    {
        // Same lone-self case, but ONLY an AOE party heal is configured. Still
        // nothing fires — self isn't a party-spell target, so the AOE-only fallback
        // must not fire for a lone self-dip either.
        using PartyHarness h = new();
        h.PartySettings.MinorPartyHealAoeSpell = "rain";
        h.PartySettings.MinorHealMemberThresholdPercent = 95;
        h.PartySettings.AoeMinMembers = 2;
        PartyMember self = h.AddMember("Cidir", hpPercent: 64);
        self.IsSelf = true;
        h.AddMember("Nineteen", hpPercent: 100);

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyHeal_OneAllyBelow_AoeOnly_UnderGate_FiresNothing()
    {
        // AOE-only party heal, one ALLY below (self full), UNDER the 2-member gate.
        // The "use party healing when N or more members meet threshold" picker is a
        // HARD gate (N is 2–6): the group AOE never fires below the count, even when
        // it's the only party heal configured. With no single-target to reach the lone
        // ally and the gate unmet, nothing party-side fires (user directive — "2 means
        // two or more, full stop"; supersedes the old under-gate AOE fallback).
        using PartyHarness h = new();
        h.PartySettings.MinorPartyHealAoeSpell = "rain";
        h.PartySettings.MinorHealMemberThresholdPercent = 95;
        h.PartySettings.AoeMinMembers = 2;
        PartyMember self = h.AddMember("Cidir", hpPercent: 100);
        self.IsSelf = true;
        h.AddMember("Nineteen", hpPercent: 80);   // one ally below — under the gate

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyHeal_SelfPlusOneAllyBelow_MeetsGate_FiresAoe()
    {
        // Self counts toward the AOE gate: self + one ally below = 2 of 3, hitting
        // AoeMinMembers = 2 → the group AOE fires (it heals everyone, self included).
        using PartyHarness h = new();
        h.PartySettings.MinorPartyHealSpell = "anno";
        h.PartySettings.MinorPartyHealAoeSpell = "rain";
        h.PartySettings.MinorHealMemberThresholdPercent = 95;
        h.PartySettings.AoeMinMembers = 2;
        PartyMember self = h.AddMember("Cidir", hpPercent: 60);
        self.IsSelf = true;
        h.AddMember("Nineteen", hpPercent: 80);   // one ally below
        h.AddMember("Thresh", hpPercent: 100);

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("rain", h.CastsSent[0]);
    }

    [Fact]
    public void PartyHeal_OneAllyBelow_SingleAndAoeConfigured_FiresSingle_NotAoe()
    {
        // The reported case: single (mahe) AND AOE (mrai) both set, one ally below,
        // self full — under the 2-member gate. The single-target heals the lone ally;
        // the AOE must NOT fire below the count.
        using PartyHarness h = new();
        h.PartySettings.MajorPartyHealSpell = "mahe";
        h.PartySettings.MajorPartyHealAoeSpell = "mrai";
        h.PartySettings.MajorHealMemberThresholdPercent = 75;
        h.PartySettings.AoeMinMembers = 2;
        PartyMember self = h.AddMember("Cidir", hpPercent: 100);
        self.IsSelf = true;
        h.AddMember("Nineteen", hpPercent: 60);   // one ally below 75
        h.AddMember("Thresh", hpPercent: 100);

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.StartsWith("mahe", h.CastsSent[0]);   // single-target the ally, never the AOE mrai
        Assert.DoesNotContain(h.CastsSent, c => c.Contains("mrai"));
    }

    [Fact]
    public void DownedAllyHeal_UsesEmergencyHealSpell_ByName()
    {
        // A downed ally (aided back but off-roster) gets the combat-profile
        // EMERGENCY heal spell by name — the same big heal the caster uses on itself
        // in a pinch — not the party heal (report paradigm-20260922-203540 follow-up).
        using PartyHarness h = new();
        h.Spells.EmergencyHealSpell = "mahe";
        h.PartySettings.MajorPartyHealSpell = "grpmaj";   // the old pick — must NOT win
        h.PartySettings.MinorPartyHealSpell = "grpmin";
        h.AddMember("Cidir", hpPercent: 100);             // party present, healthy
        h.Director.SetDownedAllyProvider(() => new[] { "Nineteen ByNineteen" });

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("mahe Nineteen", h.CastsSent[0]);
    }

    [Fact]
    public void DownedAllyHeal_FallsBackToPartyHeal_WhenNoEmergencyConfigured()
    {
        using PartyHarness h = new();
        // no EmergencyHealSpell configured
        h.PartySettings.MajorPartyHealSpell = "grpmaj";
        h.AddMember("Cidir", hpPercent: 100);
        h.Director.SetDownedAllyProvider(() => new[] { "Nineteen ByNineteen" });

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("grpmaj Nineteen", h.CastsSent[0]);
    }

    [Fact]
    public void PartyHeal_TwoMembersBelow_FiresAoe()
    {
        using PartyHarness h = new();
        h.PartySettings.MinorPartyHealSpell = "heal";
        h.PartySettings.MinorPartyHealAoeSpell = "groupheal";
        h.PartySettings.MinorHealMemberThresholdPercent = 70;
        h.PartySettings.AoeMinMembers = 2;
        h.AddMember("Tank",  hpPercent: 60);
        h.AddMember("Mage",  hpPercent: 50);

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("groupheal", h.CastsSent[0]);
    }

    [Fact]
    public void PartyHeal_NoMembersBelow_NoCast()
    {
        using PartyHarness h = new();
        h.PartySettings.MinorPartyHealSpell = "heal";
        h.PartySettings.MinorHealMemberThresholdPercent = 70;
        h.AddMember("Tank", hpPercent: 90);

        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyHeal_InvitedMemberNotYetJoined_NotHealed()
    {
        // Report 162916 (spam-heal on relog): a re-invited member reads 0% /
        // BaselineHp 0 until the on-join @health exchange runs. That 0% counted
        // as below-threshold and the director spam-cast a heal at the pending
        // invitee every tick. Invited-but-not-joined rows must be skipped until
        // they follow and report real vitals.
        using PartyHarness h = new();
        h.PartySettings.MinorPartyHealSpell = "heal";
        h.PartySettings.MinorHealMemberThresholdPercent = 70;
        h.AddMember("Tank", hpPercent: 90);                    // joined, healthy
        PartyMember invited = h.AddMember("NewGuy", hpPercent: 0, baselineHp: 0);
        invited.IsInvited = true;

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyHeal_InvitedMemberExcludedFromLowestPick()
    {
        // The skip also keeps a 0%-reading invitee out of the lowest-HP target
        // pick, so a genuinely-low joined member is still the one healed — not
        // the pending invitee that hasn't reported vitals yet.
        using PartyHarness h = new();
        h.PartySettings.MinorPartyHealSpell = "heal";
        h.PartySettings.MinorHealMemberThresholdPercent = 70;
        h.AddMember("Tank", hpPercent: 60);                    // joined, hurt
        PartyMember invited = h.AddMember("NewGuy", hpPercent: 0, baselineHp: 0);
        invited.IsInvited = true;

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("heal Tank", h.CastsSent[0]);
    }

    [Fact]
    public void PartyHeal_PicksLowestHp_AsTarget()
    {
        using PartyHarness h = new();
        h.PartySettings.MinorPartyHealSpell = "heal";
        h.PartySettings.MinorHealMemberThresholdPercent = 70;
        h.AddMember("Tank",  hpPercent: 65);
        h.AddMember("Mage",  hpPercent: 30);
        h.AddMember("Druid", hpPercent: 55);

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("heal Mage", h.CastsSent[0]);
    }

    [Fact]
    public void PartyHeal_MajorThreshold_FiresMajorAoe()
    {
        using PartyHarness h = new();
        h.PartySettings.MajorPartyHealSpell = "majorheal";
        h.PartySettings.MajorPartyHealAoeSpell = "majorgroupheal";
        h.PartySettings.MajorHealMemberThresholdPercent = 40;
        h.PartySettings.AoeMinMembers = 2;
        h.AddMember("Tank",  hpPercent: 30);
        h.AddMember("Mage",  hpPercent: 25);
        // Default priority puts MinorPartyHeal (1) above Major (2);
        // disable minor by clearing the spell so major wins.

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("majorgroupheal", h.CastsSent[0]);
    }

    [Fact]
    public void PartyHeal_BelowThresholdSingleConfigured_SinglePrefersAoeAtCount()
    {
        // Both single and AOE configured; only one member below
        // threshold; AoeMinMembers=2 — single fires.
        using PartyHarness h = new();
        h.PartySettings.MinorPartyHealSpell = "heal";
        h.PartySettings.MinorPartyHealAoeSpell = "groupheal";
        h.PartySettings.MinorHealMemberThresholdPercent = 70;
        h.PartySettings.AoeMinMembers = 2;
        h.AddMember("Tank", hpPercent: 60);

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("heal Tank", h.CastsSent[0]);
    }

    // ----- Party-cure (cure an afflicted member by chip) -------------
    // The chip is mirrored from the member's inbound ".@poisoned" say by
    // PartyAilmentTracker; here we set it directly. Same cure-spell config
    // as self-cure; the target string routes the cast to the member.

    [Fact]
    public void PartyCure_MemberPoisoned_CastsCureOnMember()
    {
        using PartyHarness h = new();
        h.Spells.CurePoisonSpell = "neutralize";
        PartyMember tank = h.AddMember("Tank", hpPercent: 100);
        tank.Poisoned = true;

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("neutralize Tank", h.CastsSent[0]);
    }

    [Fact]
    public void PartyCure_AfterCombatTicked_WaitsForTheFightToEnd()
    {
        using PartyHarness h = new();
        h.Spells.CurePoisonSpell = "neutralize";
        h.Spells.CurePoisonAfterCombat = true;
        h.State.InCombat = true;
        PartyMember tank = h.AddMember("Tank", hpPercent: 100);
        tank.Poisoned = true;

        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);

        h.State.InCombat = false;
        Assert.Equal(new[] { "neutralize Tank" }, h.CastsSent);
    }

    [Fact]
    public void PartyCure_AfterCombatTickedForOneCure_StillCastsTheOthersMidFight()
    {
        // Each cure has its own box: a held poison cure doesn't hold the hold cure.
        using PartyHarness h = new();
        h.Spells.CurePoisonSpell = "neutralize";
        h.Spells.CurePoisonAfterCombat = true;
        h.Spells.CureHoldsSpell = "curp";
        h.State.InCombat = true;
        PartyMember tank = h.AddMember("Tank", hpPercent: 100);
        tank.Poisoned = true;
        tank.Held = true;

        h.Director.Evaluate();
        Assert.Equal(new[] { "curp Tank" }, h.CastsSent);
    }

    // Report paradigm-20261003-131851: a cure-poison too weak for the poison left the
    // member's par `P` flag up, so the chip came back after every cast and the cure
    // went out every 5 s until mana ran dry.
    [Fact]
    public void PartyCure_AilmentKeepsComingBack_BacksOff()
    {
        using PartyHarness h = new();
        DateTime now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        h.Clock = () => now;
        h.Spells.CurePoisonSpell = "mgra";
        PartyMember tank = h.AddMember("Tank", hpPercent: 100);
        PartyMember mage = h.AddMember("Mage", hpPercent: 100);
        tank.Poisoned = true;

        void Round(int seconds)
        {
            now = now.AddSeconds(seconds);
            h.Cast.OnCombatTick();
            h.Director.NotifyRoundComplete();
            h.Director.Evaluate();
        }

        h.Director.Evaluate();
        Assert.Equal(new[] { "mgra Tank" }, h.CastsSent);

        // Still poisoned 5 s on (par re-flagged them): held off. A different member's
        // cure isn't.
        mage.Poisoned = true;
        Round(5);
        Assert.Equal(new[] { "mgra Tank", "mgra Mage" }, h.CastsSent);
        mage.Poisoned = false;

        Round(11);                                  // 16 s after the first: retry
        Assert.Equal(3, h.CastsSent.Count);
        Round(16);                                  // second wait is 30 s
        Assert.Equal(3, h.CastsSent.Count);
        Round(15);
        Assert.Equal(4, h.CastsSent.Count);
        Assert.Equal("mgra Tank", h.CastsSent[3]);

        // The chip stays clear long enough to count as cured: the next poison is
        // treated at once.
        tank.Poisoned = false;
        Round(13);
        tank.Poisoned = true;
        Round(1);
        Assert.Equal(5, h.CastsSent.Count);
    }

    // Report paradigm-20260929-230346: a member's @held lit their Held chip but the
    // cure-holds spell was never cast at them; a hold goes first, as it does for us.
    [Fact]
    public void PartyCure_MemberHeld_CastsCureHoldsFirst()
    {
        using PartyHarness h = new();
        h.Spells.CureHoldsSpell = "curp";
        h.Spells.CurePoisonSpell = "neutralize";
        PartyMember leader = h.AddMember("Nineteen", hpPercent: 100);
        leader.Held = true;
        leader.Poisoned = true;

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("curp Nineteen", h.CastsSent[0]);
    }

    [Fact]
    public void PartyCure_MemberDiseased_CastsCureDisease()
    {
        using PartyHarness h = new();
        h.Spells.CureDiseaseSpell = "cure-disease";
        PartyMember mage = h.AddMember("Mage", hpPercent: 100);
        mage.Diseased = true;

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("cure-disease Mage", h.CastsSent[0]);
    }

    [Fact]
    public void PartyCure_MemberBlinded_CastsCureBlindness()
    {
        using PartyHarness h = new();
        h.Spells.CureBlindnessSpell = "cure-blind";
        PartyMember druid = h.AddMember("Druid", hpPercent: 100);
        druid.Blinded = true;

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("cure-blind Druid", h.CastsSent[0]);
    }

    [Fact]
    public void PartyCure_NoCureSpellConfigured_NoCast()
    {
        using PartyHarness h = new();
        // CurePoisonSpell unset — chip set but nothing to cast.
        PartyMember tank = h.AddMember("Tank", hpPercent: 100);
        tank.Poisoned = true;

        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyCure_NoChip_NoCast()
    {
        using PartyHarness h = new();
        h.Spells.CurePoisonSpell = "neutralize";
        h.AddMember("Tank", hpPercent: 100);   // healthy, no ailment chip

        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyCure_SkipsSelf()
    {
        // A self chip is the SELF cure path's job (via ConditionTracker),
        // not the party-cure picker — which skips IsSelf members. With no
        // ConditionTracker wired here, the self chip yields no cast.
        using PartyHarness h = new();
        h.Spells.CurePoisonSpell = "neutralize";
        PartyMember self = h.AddMember("Forged", hpPercent: 100);
        self.IsSelf = true;
        self.Poisoned = true;

        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void Cure_LifeThreatBeatsCure()
    {
        // HP critical AND poisoned — life-threat heal wins over the
        // cure dispatch. Order matters: set HP low FIRST so the cast
        // attempt happens through the life-threat path, not the
        // cure path.
        using CureHarness h = new();
        h.Spells.MajorHealSpell = "fullheal";
        h.Spells.CurePoisonSpell = "neutralize";
        h.Health.MajorHealCombatTrigger = 40;
        h.RecordCondition("Poison", MessageFlags.Poisoned, "poisoned!");

        // Drop HP into life-threat range BEFORE applying the
        // condition so Hp PropertyChanged drives Tier 1 (life-
        // threat) cast.
        h.State.Hp = 30;

        Assert.Single(h.CastsSent);
        Assert.Equal("fullheal", h.CastsSent[0]);
    }

    // ----- Party-bless (class-matched buffs on other members) --------

    private sealed class PartyBlessHarness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public PlayerState State { get; } = new();
        public PartyState Party { get; } = new();
        public CastCoordinator Cast { get; }
        public CastingDirector Director { get; }
        // The game's output, fed the way the client receives it.
        public ServerFeed Server { get; } = new();
        public List<string> CastsSent { get; } = new();
        public SpellsSettings Spells { get; set; } = new();
        public HealthSettings Health { get; set; } = new();
        public PartySettings PartySettings { get; set; } = new();

        // The party-buff plan the director reads (CharacterProfile.PartyBuffs).
        public BuffSettings PartyBuffs { get; } = new();

        // The buffs' own conditions, set on every slot in the list and on each one
        // SelfBuff adds after. Many tests give every buff the same mana floor or rest
        // and combat switches; the Buffs tab sets them per slot.
        private int _manaFloor = 70;
        private bool _whileResting, _duringCombat;
        public int ManaFloor { get => _manaFloor; set { _manaFloor = value; foreach (BuffSlot b in PartyBuffs.Slots) b.BlessIfAboveMa = value; } }
        public bool WhileResting { get => _whileResting; set { _whileResting = value; foreach (BuffSlot b in PartyBuffs.Slots) b.BlessWhileResting = value; } }
        public bool DuringCombat { get => _duringCombat; set { _duringCombat = value; foreach (BuffSlot b in PartyBuffs.Slots) b.BlessDuringCombat = value; } }

        // A buff cast on ourselves, added to the list as the Buffs tab adds one.
        public BuffSlot SelfBuff(string spell)
        {
            BuffSlot slot = new()
            {
                Spell = spell, CastOnSelf = true,
                BlessIfAboveMa = _manaFloor, BlessWhileResting = _whileResting, BlessDuringCombat = _duringCombat,
            };
            PartyBuffs.Slots.Add(slot);
            return slot;
        }

        // Given names currently in the room — the presence gate. AddMember
        // puts a member here by default; drop a name to model an absent member.
        public HashSet<string> InRoom { get; } = new(StringComparer.OrdinalIgnoreCase);

        // Spell shorts that are whole-party (Targets 10/13).
        public HashSet<string> WholeParty { get; } = new(StringComparer.OrdinalIgnoreCase);

        // When true the triggered-rest gate reports an active recovery
        // rest. A bare Position=Resting does NOT set this — idle resting still
        // buffs the party.
        public bool TriggeredRest { get; set; }

        public DateTime Now { get; set; } =
            new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Buff short → (CasterMessage template, duration seconds).
        public Dictionary<string, (string Caster, long Duration)> BuffInfo { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        // Buff short → the shorts its spell removes (RemovesSpell).
        public Dictionary<string, string[]> Removes { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        // Buff short → full spell name, the name the game's negate line carries.
        public Dictionary<string, string> SpellNames { get; } =
            new(StringComparer.OrdinalIgnoreCase) { ["bles"] = "bless" };

        public PartyBlessHarness()
        {
            DefaultPatterns.Seed(Router);
            Cast = new CastCoordinator(Router, Log);
            Cast.SetWireSender(_ => { });
            Cast.CastSent += CastsSent.Add;
            Director = new CastingDirector(State, Cast,
                conditions: null, party: Party,
                readSpells: () => Spells,
                readHealth: () => Health,
                readPartySettings: () => PartySettings,
                isEnabled: () => true,
                log: Log,
                now: () => Now);
            Director.AttachLineExtractor(Server.Lines);
            Director.SetTriggeredRestGate(() => TriggeredRest);
            Director.SetBuffDurationSources(
                code => BuffInfo.TryGetValue(code, out (string Caster, long Duration) info)
                    ? (info.Caster, info.Duration)
                    : null,
                // No self-confirm path exercised here.
                _ => null,
                removesShortsFor: code =>
                    Removes.TryGetValue(code, out string[]? v) ? v : System.Array.Empty<string>());
            Director.SetPartyBuffSource(() => PartyBuffs);
            Director.SetRoomPresenceCheck(g => InRoom.Contains(g));
            Director.SetPartyWideBuffCheck(s => WholeParty.Contains(s));
            Director.SetSpellNameLookup(code => SpellNames.TryGetValue(code, out string? n) ? n : null);
            // Healthy + full mana so survival categories never pre-empt buffs.
            State.MaxHp = 200; State.Hp = 200;
            State.MaxMa = 100; State.Ma = 100;
            State.HasPromptData = true;
        }

        private static string Given(string name) => name.Split(' ')[0];

        public PartyMember AddMember(string name)
        {
            PartyMember m = new()
            {
                Name = name,
                BaselineHp = 100,
                HpPercent = 100,
                BaselineMp = 100,
                MpPercent = 100,
            };
            Party.Members.Add(m);
            Party.IsInParty = true;      // a member present ⇒ in a party (party-buff slots apply)
            InRoom.Add(Given(name));     // present in the room by default
            return m;
        }

        // A single-target slot that blesses the named members (given names).
        public BuffSlot AddTargetSlot(string spell, params string[] targets)
        {
            BuffSlot slot = new() { Spell = spell };
            foreach (string t in targets) slot.Targets.Add(t.ToLowerInvariant());
            PartyBuffs.Slots.Add(slot);
            return slot;
        }

        // A single-target slot that blesses every in-party, in-room member.
        public BuffSlot AddAllMembersSlot(string spell)
        {
            BuffSlot slot = new() { Spell = spell, AllMembers = true };
            PartyBuffs.Slots.Add(slot);
            return slot;
        }

        // A whole-party slot (one cast, no target). on = WholePartyOn.
        public BuffSlot AddWholePartySlot(string spell, bool on = true)
        {
            WholeParty.Add(spell);
            BuffSlot slot = new() { Spell = spell, WholePartyOn = on };
            PartyBuffs.Slots.Add(slot);
            return slot;
        }

        // Simulate a server line arriving on the wired
        // LineExtractor by invoking the director's private
        // OnLine handler directly (matches the ConditionTracker.OnLine
        // reflection idiom used elsewhere in this file).
        public void Confirm(string casterLine) => Server.Line(casterLine);

        public void Dispose()
        {
            Director.Dispose();
            Cast.Dispose();
        }
    }

    [Fact]
    public void PartyBless_SelectedMember_CastsOnMember()
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("bles Raijin", h.CastsSent[0]);
    }

    [Fact]
    public void PartyBless_NotInParty_DoesNotCast()
    {
        // Solo (IsInParty false) ⇒ the party-buff slots never fire, even with a lingering
        // member row — party buffs are used only alongside self buffs while in a party.
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");
        h.Party.IsInParty = false;        // now solo

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyBless_UnselectedMember_NoCast()
    {
        // A member who isn't one of the slot's chosen targets is left alone.
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("bles", "Raijin");   // only Raijin selected
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Goldar");               // Goldar isn't selected

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyBless_PartyMember_CastAttemptedRegardlessOfAlsoHere()
    {
        // Party = same room, so a roster member is castable even when they're NOT in
        // the room's "Also here:" list (e.g. the leader we follow is never listed
        // there). The old pre-emptive "Also here:" gate wrongly skipped them.
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");
        h.InRoom.Clear();                    // not listed in "Also here:" — still cast

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("bles Raijin", h.CastsSent[0]);
    }

    [Fact]
    public void PartyBless_HiddenMember_BacksOffThenRetriesOnMove()
    {
        // A HIDING member answers "You do not see <name> here!" to our cast — back off
        // (no per-round spam) until we move, then retry.
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");
        h.InRoom.Remove("Raijin");           // hiding ⇒ absent from "Also here:"

        h.Director.Evaluate();               // attempt (in party ⇒ here)
        Assert.Single(h.CastsSent);

        h.Confirm("You do not see Raijin here!");   // hidden ⇒ back off
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);           // still hiding ⇒ skipped, no spam

        h.Director.NoteRoomChanged();        // we moved ⇒ retry
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();    // new round frees the between-round slot
        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
        Assert.Equal("bles Raijin", h.CastsSent[0]);
    }

    [Fact]
    public void PartyBless_HiddenMember_RetriesWhenReappearsInAlsoHere()
    {
        // The other clear path: a backed-off hidden member is retried the moment they
        // reappear in "Also here:" (they unhid).
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");
        h.InRoom.Remove("Raijin");           // hiding

        h.Director.Evaluate();
        h.Confirm("You do not see Raijin here!");
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);           // still hiding

        h.InRoom.Add("Raijin");              // reappears in "Also here:" (unhid)
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();    // new round frees the between-round slot
        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
    }

    // ----- A member negates our buff (user rulings 2026-10-17) -----------------

    private static void NextRound(PartyBlessHarness h, int seconds = 6)
    {
        h.Now = h.Now.AddSeconds(seconds);
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();
        h.Director.Evaluate();
    }

    [Fact]
    public void PartyBless_MemberNegatesIt_MarkedOnTheSlot_NoTimer_NeverOfferedAgain()
    {
        // "if we buff someone, and they're wearing something that negates it, that means
        // we can never buff them with that spell and shouldnt keep trying".
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        BuffSlot slot = h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");
        List<(string Short, string Given)> raised = new();
        h.Director.BuffNegated += (s, g) => raised.Add((s, g));

        h.Director.Evaluate();
        Assert.Equal("bles Raijin", Assert.Single(h.CastsSent));

        h.Confirm("You attempt to cast bless at Raijin, but it's negated!");

        Assert.Equal(new[] { "raijin" }, slot.NegatedTargets);
        Assert.Equal(("bles", "raijin"), Assert.Single(raised));
        Assert.DoesNotContain(h.Director.SnapshotActiveBuffs(), b => b.Target == "raijin");
        Assert.Contains(h.Log.Snapshot(), e => e.Message.Contains("bles on Raijin was negated"));

        h.CastsSent.Clear();
        for (int i = 0; i < 5; i++) NextRound(h);
        Assert.Empty(h.CastsSent);

        // For good, not for this fight: a new character load keeps the mark (it lives
        // on the saved slot, the character tier), so still nothing is cast.
        h.Director.ResetBuffTracking();
        NextRound(h, 600);
        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyBless_NegatedMarkLifted_CastsOnTheMemberAgain()
    {
        // The user unticks and re-ticks the member, which lifts the mark: re-armed.
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        BuffSlot slot = h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");

        h.Director.Evaluate();
        h.Confirm("You attempt to cast bless at Raijin, but it's negated!");
        h.CastsSent.Clear();
        NextRound(h);
        Assert.Empty(h.CastsSent);

        slot.NegatedTargets.Clear();
        NextRound(h);
        Assert.Equal("bles Raijin", Assert.Single(h.CastsSent));
    }

    [Fact]
    public void PartyBless_TimedFromSend_Negated_DropsTheTimerArmedOnSend()
    {
        // A caster line that can't name the target times the buff from its send; the
        // negate line takes that timer back.
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        BuffSlot slot = h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {spellname}!", 300);
        h.AddMember("Raijin");

        h.Director.Evaluate();
        Assert.Contains(h.Director.SnapshotActiveBuffs(), b => b.Target == "raijin");

        h.Confirm("You attempt to cast bless at Raijin, but it's negated!");

        Assert.DoesNotContain(h.Director.SnapshotActiveBuffs(), b => b.Target == "raijin");
        Assert.Equal(new[] { "raijin" }, slot.NegatedTargets);
    }

    [Fact]
    public void PartyBless_ResistedNotNegated_MarksNothing_KeepsBuffingThem()
    {
        // "a spell resist is not a negate. a negate specifically has the word negate/d
        // in the line." (user, 2026-10-17)
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        BuffSlot slot = h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");
        int raised = 0;
        h.Director.BuffNegated += (_, _) => raised++;

        h.Director.Evaluate();
        h.Confirm("You attempt to cast bless at Raijin, but the spell is resisted.");

        Assert.Empty(slot.NegatedTargets);
        Assert.Equal(0, raised);
        h.CastsSent.Clear();
        NextRound(h);
        Assert.Equal("bles Raijin", Assert.Single(h.CastsSent));   // still offered
    }

    [Fact]
    public void PartyBless_SpellNamedNegateMagic_Resisted_IsNotANegate()
    {
        // The word has to be the line's own "it's negated!", not part of a spell's name.
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.SpellNames["nega"] = "negate magic";
        BuffSlot slot = h.AddTargetSlot("nega", "Raijin");
        h.BuffInfo["nega"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");

        h.Director.Evaluate();
        h.Confirm("You attempt to cast negate magic at Raijin, but the spell is resisted.");

        Assert.Empty(slot.NegatedTargets);
    }

    [Fact]
    public void PartyBless_RoomSpellNegatedOnTheMember_LeavesOurCastAlone()
    {
        // "yes, only act on casts we sent, matched by spell name": the Underwater
        // Passage's own holding breath, negated by a worn fish-helm, prints the same
        // caster line while our bless is out. It is not our cast.
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        BuffSlot slot = h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");

        h.Director.Evaluate();
        h.Confirm("You attempt to cast holding breath at Raijin, but it's negated!");
        Assert.Empty(slot.NegatedTargets);

        h.Confirm("You cast bless on Raijin!");   // our bless still lands and is timed
        Assert.Contains(h.Director.SnapshotActiveBuffs(), b => b.Target == "raijin" && b.Short == "bles");
    }

    [Fact]
    public void PartyBless_NegateLineTwoRoundsAfterOurCast_IsNotTiedToIt()
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        BuffSlot slot = h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");

        h.Director.Evaluate();
        h.Now = h.Now.AddSeconds(11);
        h.Confirm("You attempt to cast bless at Raijin, but it's negated!");

        Assert.Empty(slot.NegatedTargets);
    }

    [Fact]
    public void PartyBless_NegateLineNamingAnotherTarget_IsNotOurs()
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        BuffSlot slot = h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");

        h.Director.Evaluate();
        h.Confirm("You attempt to cast bless at Goldar, but it's negated!");
        h.Confirm("Raijin negates Goldar's cast of bless!");

        Assert.Empty(slot.NegatedTargets);
    }

    [Fact]
    public void PartyBless_Reconnect_SelfTimerResumesWithItsTimeLeft_TheMembersRunsOn()
    {
        // On OUR reconnect BOTH survive the drop (paradigm-20260908-205555). Our own
        // runs on with the time it had when the drop began: "as soon as we've
        // disconnected all timers need to be paused until we're back in the game, then
        // they all resume" (user, 2026-10-10); until that ruling this test pinned its
        // expiry as absolute and unshifted too. The party member's still is: the
        // member stayed online, and its buff ran down the whole time (user,
        // 2026-08-28).
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.BuffInfo["mysh"] = (string.Empty, 200);
        h.AddMember("Raijin");

        h.Director.Evaluate();                          // cast bles on Raijin
        h.Confirm("You cast bless on Raijin!");         // arm ("raijin", bles)
        h.Director.NoteManualBuffCast("mysh");          // arm a self buff ("")

        DateTime partyUntil = default, selfUntil = default;
        foreach (Game.Spells.ActiveBuffTimer t in h.Director.SnapshotActiveBuffs())
            if (t.Target.Length > 0) partyUntil = t.Until; else selfUntil = t.Until;
        Assert.Equal(2, h.Director.SnapshotActiveBuffs().Count);

        h.Director.PauseBuffTimers();
        h.Now = h.Now.AddSeconds(45);                   // brief gap, under both durations
        h.Director.ResumeBuffTimers();

        System.Collections.Generic.IReadOnlyList<Game.Spells.ActiveBuffTimer> after =
            h.Director.SnapshotActiveBuffs();
        Assert.Equal(2, after.Count);                   // both survive
        foreach (Game.Spells.ActiveBuffTimer t in after)
        {
            if (t.Target == "raijin") Assert.Equal(partyUntil, t.Until);  // absolute, unshifted
            // Ours is moved on by the 45 s away: the time left is what it was.
            else { Assert.Equal(string.Empty, t.Target); Assert.Equal(selfUntil.AddSeconds(45), t.Until); }
        }
    }

    [Fact]
    public void PartyBless_OurDeath_ClearsSelfKeepsPartyTimers()
    {
        // OUR death wipes OUR buffs only — self timers clear, the (alive) party member's
        // timer stays so we don't needlessly re-bless them.
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.BuffInfo["mysh"] = (string.Empty, 200);
        h.AddMember("Raijin");

        h.Director.Evaluate();
        h.Confirm("You cast bless on Raijin!");
        h.Director.NoteManualBuffCast("mysh");
        Assert.Equal(2, h.Director.SnapshotActiveBuffs().Count);

        h.Director.ClearSelfBuffTracking();

        Game.Spells.ActiveBuffTimer kept = Assert.Single(h.Director.SnapshotActiveBuffs());
        Assert.Equal("raijin", kept.Target);
    }

    // Nothing casts on someone who has left the party, so nothing ever overwrote or
    // cleared their timer: it stayed in the store, expired, for the rest of the
    // session (report paradigm-20261010-145330).
    [Fact]
    public void PartyBless_MemberLeft_TheirTimerIsDroppedOnceItRunsOut()
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.BuffInfo["mysh"] = (string.Empty, 200);
        PartyMember raijin = h.AddMember("Raijin");

        h.Director.Evaluate();
        h.Confirm("You cast bless on Raijin!");
        h.Director.NoteManualBuffCast("mysh");
        Assert.Equal(2, h.Director.SnapshotActiveBuffs().Count);

        h.Party.Members.Remove(raijin);
        h.Party.IsInParty = false;
        h.Now = h.Now.AddSeconds(120);
        h.Director.Evaluate();
        // Still running: whoever rejoins inside it is still buffed.
        Assert.Contains(h.Director.SnapshotActiveBuffs(), t => t.Target == "raijin");

        h.Now = h.Now.AddSeconds(400);
        h.Director.Evaluate();
        // Theirs is gone; our own run-out timer stays for the recast to count from.
        Game.Spells.ActiveBuffTimer own = Assert.Single(h.Director.SnapshotActiveBuffs());
        Assert.Equal(string.Empty, own.Target);
    }

    [Fact]
    public void PartyBless_MemberStillInParty_TheirRunOutTimerIsKept()
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");
        h.Director.Evaluate();
        h.Confirm("You cast bless on Raijin!");
        Assert.Single(h.Director.SnapshotActiveBuffs());

        h.InRoom.Remove("Raijin");                      // out of reach: nothing recasts it
        h.Now = h.Now.AddSeconds(400);
        h.Director.Evaluate();

        Assert.Contains(h.Director.SnapshotActiveBuffs(), t => t.Target == "raijin");
    }

    // The roster carries the long par-row form; the timer is keyed by the given name.
    [Fact]
    public void PartyBless_MemberListedByLongName_TheirRunOutTimerIsKept()
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("RAIJIN WuzHere");
        h.Director.Evaluate();
        h.Confirm("You cast bless on Raijin!");
        Assert.Single(h.Director.SnapshotActiveBuffs());

        h.InRoom.Remove("RAIJIN");
        h.Now = h.Now.AddSeconds(400);
        h.Director.Evaluate();

        Assert.Contains(h.Director.SnapshotActiveBuffs(), t => t.Target == "raijin");
    }

    // Invited back and not yet joined: they are on the roster.
    [Fact]
    public void PartyBless_MemberInvitedNotYetJoined_TheirRunOutTimerIsKept()
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        PartyMember m = h.AddMember("Raijin");
        h.Director.Evaluate();
        h.Confirm("You cast bless on Raijin!");
        h.Party.Members.Remove(m);
        h.Party.Members.Add(new PartyMember { Name = "Raijin", IsInvited = true });
        h.InRoom.Remove("Raijin");

        h.Now = h.Now.AddSeconds(400);
        h.Director.Evaluate();

        Assert.Contains(h.Director.SnapshotActiveBuffs(), t => t.Target == "raijin");
    }

    // A negative recast margin counts from the run-out entry. A member off the
    // roster for a moment after the run-out (dropped and back, a party re-formed)
    // must not lose it, or the recast no longer waits.
    [Fact]
    public void PartyBless_NegativeMargin_MemberBrieflyOffRoster_RecastStillWaits()
    {
        using PartyBlessHarness h = new();
        BuffSlot slot = h.AddTargetSlot("bles", "Raijin");
        slot.RecastMarginSec = -120;                 // recast two minutes after it runs out
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        PartyMember m = h.AddMember("Raijin");
        Assert.Equal("bles", h.Director.Evaluate());
        h.Confirm("You cast bless on Raijin!");
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();

        h.Now = h.Now.AddSeconds(310);               // ran out 10 s ago; due at +420
        Assert.Null(h.Director.Evaluate());          // the margin holds it

        h.Party.Members.Remove(m);                   // off the roster for a moment
        h.Director.Evaluate();
        Assert.Single(h.Director.SnapshotActiveBuffs());
        h.Party.Members.Add(m);                      // and back
        h.Now = h.Now.AddSeconds(20);
        Assert.Null(h.Director.Evaluate());
        Assert.Empty(h.CastsSent);

        h.Party.Members.Remove(m);                   // gone for good: dropped once the wait is over
        h.Now = h.Now.AddSeconds(100);               // +430, past the +420 the recast was due at
        h.Director.Evaluate();
        Assert.Empty(h.Director.SnapshotActiveBuffs());
    }

    // A spell strips what it removes off its own target only. Smite on one member and
    // greater smite on another (each removes the other) cleared each other's timers
    // and were recast every few seconds (report paradigm-20261002-012234).
    [Fact]
    public void PartyBless_RemoverOnOneMember_LeavesItsVictimOnAnother()
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("smit", "Raijin");
        h.AddTargetSlot("grsm", "Goldar");
        h.BuffInfo["smit"] = ("You cast {s} on {s}!", 365);
        h.BuffInfo["grsm"] = ("You cast {s} on {s}!", 365);
        h.Removes["smit"] = new[] { "grsm" };
        h.Removes["grsm"] = new[] { "smit" };
        h.AddMember("Raijin");
        h.AddMember("Goldar");

        h.Director.Evaluate();
        h.Confirm("You cast smite on Raijin!");
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();
        h.Director.Evaluate();
        h.Confirm("You cast greater smite on Goldar!");

        Assert.Equal(
            new[] { ("goldar", "grsm"), ("raijin", "smit") },
            h.Director.SnapshotActiveBuffs().Select(b => (b.Target, b.Short)).OrderBy(b => b.Target));

        // Both are up, so nothing is due: neither is recast.
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyBless_RemoverOnAMember_LeavesOurOwnVictimBuff()
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("smit", "Raijin");
        h.BuffInfo["smit"] = ("You cast {s} on {s}!", 365);
        h.BuffInfo["grsm"] = (string.Empty, 365);
        h.Removes["smit"] = new[] { "grsm" };
        h.AddMember("Raijin");
        h.Director.NoteManualBuffCast("grsm");           // greater smite on ourselves ("")

        h.Director.Evaluate();
        h.Confirm("You cast smite on Raijin!");

        Assert.Contains(h.Director.SnapshotActiveBuffs(), b => b.Short == "grsm" && b.Target.Length == 0);
    }

    [Fact]
    public void PartyBless_RemoverOnTheSameMember_ClearsItsVictimThere()
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("smit", "Raijin");
        h.AddTargetSlot("grsm", "Raijin");
        h.BuffInfo["smit"] = ("You cast {s} on {s}!", 365);
        h.BuffInfo["grsm"] = ("You cast {s} on {s}!", 365);
        h.Removes["grsm"] = new[] { "smit" };
        h.AddMember("Raijin");

        h.Director.Evaluate();
        h.Confirm("You cast smite on Raijin!");
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();
        h.Director.Evaluate();
        h.Confirm("You cast greater smite on Raijin!");

        Game.Spells.ActiveBuffTimer kept = Assert.Single(h.Director.SnapshotActiveBuffs());
        Assert.Equal("grsm", kept.Short);
    }

    [Fact]
    public void PartyBless_MemberDeath_ClearsThatMembersTimersOnly()
    {
        // A party member's death wipes THEIR buffs — clear the timers we hold on them;
        // every other member's timer stays.
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddAllMembersSlot("bles");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");
        h.AddMember("Goldar");

        h.Director.Evaluate();                           // casts on the first (Raijin)
        h.Confirm("You cast bless on Raijin!");
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();                // new round frees the between-round slot
        h.Director.Evaluate();                           // casts on Goldar
        h.Confirm("You cast bless on Goldar!");
        Assert.Equal(2, h.Director.SnapshotActiveBuffs().Count);

        h.Director.ClearMemberBuffTimers("Raijin");

        Game.Spells.ActiveBuffTimer kept = Assert.Single(h.Director.SnapshotActiveBuffs());
        Assert.Equal("goldar", kept.Target);
    }

    [Fact]
    public void ClearBuffTimer_RemovesOnlyTheNamedTimer()
    {
        // The Buff Watchdog ✕: manually drop one (target, spell) timer, leaving the rest.
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddAllMembersSlot("bles");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");
        h.AddMember("Goldar");

        h.Director.Evaluate();
        h.Confirm("You cast bless on Raijin!");
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();              // new round frees the between-round slot
        h.Director.Evaluate();
        h.Confirm("You cast bless on Goldar!");
        Assert.Equal(2, h.Director.SnapshotActiveBuffs().Count);

        h.Director.ClearBuffTimer("raijin", "bles");   // ✕ on Raijin's row (case-insensitive)

        Game.Spells.ActiveBuffTimer kept = Assert.Single(h.Director.SnapshotActiveBuffs());
        Assert.Equal("goldar", kept.Target);
    }

    [Fact]
    public void PartyBless_SkipsSelf()
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddAllMembersSlot("bles");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        PartyMember self = h.AddMember("Me");
        self.IsSelf = true;

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyBless_WholeParty_CastsWithNoTarget()
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddWholePartySlot("chan");
        h.AddMember("Raijin");

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("chan", h.CastsSent[0]);   // one cast, no target
    }

    [Fact]
    public void PartyBless_WholePartyOff_NoCast()
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddWholePartySlot("chan", on: false);   // all-off
        h.AddMember("Raijin");

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyBless_WholePartyOff_SoloOptionOn_NoCast()
    {
        // Regression: the Solo option used to bypass the unchecked Party master and
        // cast every bulk-added whole-party buff while alone, draining the mana pool.
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        BuffSlot slot = h.AddWholePartySlot("chan", on: false);
        slot.CastSolo = true;
        h.Party.IsInParty = false;

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyBless_WholeParty_Solo_StillCasts()
    {
        // MajorMUD treats a lone character as a party of one — a whole-party cast
        // still lands on us solo (confirmed, report paradigm-20260906-150624: a
        // whole-party item-cast buff never fired outside a party). No members, no
        // IsInParty — the self-bless timing gates apply instead of the party ones.
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddWholePartySlot("chan");
        h.Party.IsInParty = false;

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("chan", h.CastsSent[0]);
    }

    [Fact]
    public void PartyBless_WholeParty_Solo_ObeysSelfCombatGate()
    {
        // Solo, the fallback is the SELF-bless timing gate, not an unconditional
        // allow — in combat without the slot's "during combat" it's held, same as any
        // other self-cast buff.
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddWholePartySlot("chan");
        h.Party.IsInParty = false;
        h.State.InCombat = true;

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyBless_WholeParty_Solo_SelfCombatGateOn_Casts()
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddWholePartySlot("chan");
        h.Party.IsInParty = false;
        h.State.InCombat = true;
        h.DuringCombat = true;

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
    }

    [Fact]
    public void PartyBless_WholeParty_Solo_CastSoloOff_Held()
    {
        // The "Solo" per-slot flag gates the solo fallback: unticked, a whole-party
        // buff is party-only again and stays held while solo even though the self-bless
        // gate would otherwise allow it.
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        BuffSlot slot = h.AddWholePartySlot("chan");
        slot.CastSolo = false;
        h.Party.IsInParty = false;

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyBless_WholeParty_InParty_CastSoloOff_StillCasts()
    {
        // CastSolo only governs the SOLO case. In a party the whole-party cast rides
        // WholePartyOn + the party gate, so an unticked Solo box doesn't hold it.
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        BuffSlot slot = h.AddWholePartySlot("chan");
        slot.CastSolo = false;
        h.AddMember("Raijin");

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("chan", h.CastsSent[0]);
    }

    [Fact]
    public void PartyBless_ConfirmStartsTimer_NoImmediateRecast()
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");

        h.Director.Evaluate();
        Assert.Single(h.CastsSent);

        // Confirm OUR cast landed → starts the 300s timer keyed to Raijin.
        h.Confirm("You cast bless on Raijin!");
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();

        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyBless_Confirm_SurfacesDurationOnInfoLog()
    {
        // The user couldn't tell from the program log whether a party bless armed
        // its recast timer, because the confirmation logged on the combat channel
        // (off in normal play). It now lands on the always-on Info channel with the
        // effect duration and the recast lead (duration − 15s recast margin).
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");

        h.Director.Evaluate();
        h.Confirm("You cast bless on Raijin!");

        LogEntry entry = Assert.Single(
            h.Log.Snapshot(),
            e => e.Severity == LogSeverity.Info && e.Message.Contains("party-buff confirmed"));
        Assert.Contains("spell=bles", entry.Message);
        Assert.Contains("target=Raijin", entry.Message);
        Assert.Contains("duration=300s", entry.Message);
        Assert.Contains("recast in 285s", entry.Message);   // 300 − 15s margin
    }

    [Fact]
    public void PartyBless_NoConfirm_ReattemptsNextPass()
    {
        // Decision: no confirmation observed ⇒ no timer ⇒ re-attempt. The
        // CastCoordinator cooldown (cleared here) is the only spam guard.
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");

        h.Director.Evaluate();
        Assert.Single(h.CastsSent);

        // No Confirm() — timer never starts.
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();   // new round frees the between-round slot

        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
        Assert.Equal("bles Raijin", h.CastsSent[0]);
    }

    [Fact]
    public void PartyBless_RecastWithinExpiryWindow()
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");

        h.Director.Evaluate();
        h.Confirm("You cast bless on Raijin!");
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();

        // Mid-duration → no recast.
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);

        // Within 15s of expiry → due.
        h.Now = h.Now.AddSeconds(290);
        h.Cast.OnCombatTick();
        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
        Assert.Equal("bles Raijin", h.CastsSent[0]);
    }

    [Fact]
    public void PartyBless_CyclesAcrossMembers()
    {
        // Both members covered by one all-members slot — cycle blesses each in turn.
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddAllMembersSlot("bles");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");
        h.AddMember("Goldar");

        h.Director.Evaluate();
        Assert.Equal("bles Raijin", h.CastsSent[^1]);
        h.Confirm("You cast bless on Raijin!");
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();   // new round frees the between-round slot

        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
        Assert.Equal("bles Goldar", h.CastsSent[0]);
    }

    // ----- a caster line that can't name its target (report paradigm-20261008-222838) -----

    private static void FreshRound(PartyBlessHarness h)
    {
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();   // new round frees the between-round slot
    }

    [Fact]
    public void PartyBless_CastLineNamesNoTarget_IsTimedFromTheSend_AndTheNextMemberGetsTheirs()
    {
        // angelic halo's caster line is "You cast angelic halo!" whoever it goes on.
        // Waiting for a line naming the member never armed a timer, so the first
        // member was cast on every round and the second never was.
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddAllMembersSlot("halo");
        h.BuffInfo["halo"] = ("You cast {spellname}!", 420);
        h.AddMember("Raijin");
        h.AddMember("Goldar");

        h.Director.Evaluate();
        Assert.Equal("halo Raijin", Assert.Single(h.CastsSent));
        h.Confirm("You cast angelic halo!");

        FreshRound(h);
        h.Director.Evaluate();
        Assert.Equal("halo Goldar", Assert.Single(h.CastsSent));
        h.Confirm("You cast angelic halo!");

        FreshRound(h);
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);

        Assert.Equal(new[] { "goldar", "raijin" },
            h.Director.SnapshotActiveBuffs().Where(t => t.Short == "halo").Select(t => t.Target).OrderBy(t => t));
    }

    [Fact]
    public void PartyBless_NoCasterLineAtAll_IsTimedFromTheSend()
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("vlwa", "Raijin");
        h.BuffInfo["vlwa"] = (string.Empty, 300);
        h.AddMember("Raijin");

        h.Director.Evaluate();
        Assert.Equal("vlwa Raijin", Assert.Single(h.CastsSent));

        FreshRound(h);
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);

        Game.Spells.ActiveBuffTimer timer = Assert.Single(h.Director.SnapshotActiveBuffs());
        Assert.Equal("raijin", timer.Target);
        Assert.Equal(h.Now.AddSeconds(300), timer.Until);
    }

    [Fact]
    public void PartyBless_TimedFromTheSend_SaysSoOnTheInfoLog()
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("halo", "Raijin");
        h.BuffInfo["halo"] = ("You cast {spellname}!", 420);
        h.AddMember("Raijin");

        h.Director.Evaluate();

        LogEntry entry = Assert.Single(
            h.Log.Snapshot(),
            e => e.Severity == LogSeverity.Info && e.Message.Contains("party-buff sent"));
        Assert.Contains("spell=halo", entry.Message);
        Assert.Contains("target=Raijin", entry.Message);
        Assert.Contains("names no target", entry.Message);
        Assert.Contains("duration=420s", entry.Message);
    }

    [Fact]
    public void PartyBless_TimedFromTheSend_ARefusedCast_GivesTheTimerBack()
    {
        // No line confirms this cast, so the game's refusal is the only word that
        // it never went on.
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("halo", "Raijin");
        h.BuffInfo["halo"] = ("You cast {spellname}!", 420);
        h.AddMember("Raijin");

        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
        h.Router.Dispatch(new LineExtractor.EmittedLine(
            "You attempt to cast angelic halo, but fail.", Array.Empty<CellAttributes>(),
            DateTimeOffset.UtcNow, IsPromptLine: false));

        Assert.Empty(h.Director.SnapshotActiveBuffs());
        FreshRound(h);
        h.Director.Evaluate();
        Assert.Equal("halo Raijin", Assert.Single(h.CastsSent));
    }

    [Fact]
    public void PartyBless_TimedFromTheSend_AMemberWeCannotSee_GivesTheTimerBack_AndBacksOff()
    {
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("halo", "Raijin");
        h.BuffInfo["halo"] = ("You cast {spellname}!", 420);
        h.AddMember("Raijin");
        h.InRoom.Remove("Raijin");           // hiding ⇒ absent from "Also here:"

        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
        h.Confirm("You do not see Raijin here!");

        Assert.Empty(h.Director.SnapshotActiveBuffs());
        FreshRound(h);
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);           // backed off, not recast every round
    }

    [Fact]
    public void PartyBless_TimedFromTheSend_ALaterCannotSeeLine_LeavesTheRunningTimerAlone()
    {
        // Minutes on, "You do not see Raijin here!" answers something else the user
        // typed. It isn't the reply to the cast, and the buff is still on.
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("halo", "Raijin");
        h.BuffInfo["halo"] = ("You cast {spellname}!", 420);
        h.AddMember("Raijin");

        h.Director.Evaluate();
        h.Now = h.Now.AddSeconds(60);
        h.Confirm("You do not see Raijin here!");

        Assert.Equal("raijin", Assert.Single(h.Director.SnapshotActiveBuffs()).Target);
    }

    [Fact]
    public void PartyBless_ALineThatNamesItsTarget_StillWaitsForIt()
    {
        // Unchanged for the spells whose line does name the member: no line, no timer.
        using PartyBlessHarness h = new();
        h.ManaFloor = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {spellname} on {target}!", 300);
        h.AddMember("Raijin");

        h.Director.Evaluate();
        Assert.Empty(h.Director.SnapshotActiveBuffs());

        h.Confirm("You cast bless on Raijin!");
        Assert.Equal("raijin", Assert.Single(h.Director.SnapshotActiveBuffs()).Target);
    }

    [Fact]
    public void ManualCast_OnAMember_OfASpellWhoseLineNamesNoTarget_TimesThatMember()
    {
        using PartyBlessHarness h = new();
        h.BuffInfo["halo"] = ("You cast {spellname}!", 420);
        h.AddMember("Raijin");
        h.AddMember("Goldar");

        h.Director.NoteManualBuffCast("halo", "rai");

        Game.Spells.ActiveBuffTimer timer = Assert.Single(h.Director.SnapshotActiveBuffs());
        Assert.Equal("raijin", timer.Target);
        Assert.Equal("halo", timer.Short);
    }

    [Fact]
    public void ManualCast_OfSuchASpell_OnSomeoneOutsideTheParty_TimesNobody()
    {
        using PartyBlessHarness h = new();
        h.BuffInfo["halo"] = ("You cast {spellname}!", 420);
        h.AddMember("Raijin");

        h.Director.NoteManualBuffCast("halo", "zed");

        Assert.Empty(h.Director.SnapshotActiveBuffs());
    }

    [Fact]
    public void PartyBless_WhileRestingOff_NoCast()
    {
        // A triggered recovery rest holds a party buff whose own "while resting" is off.
        using PartyBlessHarness h = new();
        h.State.Position = PlayerPosition.Resting;
        h.TriggeredRest = true;                  // active recovery rest
        h.ManaFloor = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");

        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyBless_IdleResting_Casts()
    {
        // Idle resting (Position=Resting, not a triggered recovery) still buffs the
        // party even with the buff's own "while resting" off (its default).
        using PartyBlessHarness h = new();
        h.State.Position = PlayerPosition.Resting;
        h.TriggeredRest = false;                 // idle rest
        h.ManaFloor = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");

        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
        Assert.Equal("bles Raijin", h.CastsSent[0]);
    }

    [Fact]
    public void PartyBless_CombatSwitchIsPerBuff_OnlyTheTickedBuffGoesOutOnAMember()
    {
        // Two buffs aimed at the same member mid-fight: the one with "cast during
        // combat" goes out, the other waits for the fight to end.
        using PartyBlessHarness h = new();
        h.State.InCombat = true;
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = "prot", BlessIfAboveMa = 0, Targets = { "raijin" } });
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = "bles", BlessIfAboveMa = 0, Targets = { "raijin" }, BlessDuringCombat = true });
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.BuffInfo["prot"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");

        h.Director.Evaluate();

        Assert.Equal(new[] { "bles Raijin" }, h.CastsSent);
    }

    [Fact]
    public void PartyBless_RestSwitchIsPerBuff_OnlyTheTickedBuffGoesOutOnAMember()
    {
        using PartyBlessHarness h = new();
        h.State.Position = PlayerPosition.Resting;
        h.TriggeredRest = true;
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = "prot", BlessIfAboveMa = 0, Targets = { "raijin" } });
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = "bles", BlessIfAboveMa = 0, Targets = { "raijin" }, BlessWhileResting = true });
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.BuffInfo["prot"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");

        h.Director.Evaluate();

        Assert.Equal(new[] { "bles Raijin" }, h.CastsSent);
    }

    [Fact]
    public void PartyBless_TheOldPartySwitches_NoLongerDecideAnything()
    {
        // Settings → Party's pair is unread: with both on there, a buff without its
        // own "cast during combat" still waits out the fight.
        using PartyBlessHarness h = new();
        h.PartySettings.BlessDuringCombat = true;
        h.PartySettings.BlessWhileResting = true;
        h.State.InCombat = true;
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = "bles", BlessIfAboveMa = 0, Targets = { "raijin" } });
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyBless_ManaFloorIsPerBuff_OnAMemberToo()
    {
        using PartyBlessHarness h = new();
        h.State.Ma = 50;
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = "prot", BlessIfAboveMa = 80, Targets = { "raijin" } });
        h.PartyBuffs.Slots.Add(new BuffSlot { Spell = "bles", BlessIfAboveMa = 40, Targets = { "raijin" } });
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.BuffInfo["prot"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");

        h.Director.Evaluate();

        Assert.Equal(new[] { "bles Raijin" }, h.CastsSent);
    }

    [Fact]
    public void PartyBless_WholeParty_InAParty_FollowsTheBuffsOwnCombatSwitch()
    {
        using PartyBlessHarness h = new();
        h.State.InCombat = true;
        h.AddWholePartySlot("chan");
        h.PartyBuffs.Slots[^1].BlessIfAboveMa = 0;
        h.AddMember("Raijin");
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);

        h.PartyBuffs.Slots[^1].BlessDuringCombat = true;
        h.Director.Evaluate();

        Assert.Equal(new[] { "chan" }, h.CastsSent);
    }

    // ----- Mana-regen reroll routes through the priority pass ----------

    [Fact]
    public void ManaRegenReroll_Staged_FiresThroughBuffPass()
    {
        // The reroller stages a reroll on the director; it casts through the
        // between-round pass (as a Buffing candidate), not on the raw wire.
        using Harness h = new();
        BuffSettings buffs = new() { Slots = { new BuffSlot { Spell = "mreg" } } };
        h.Director.SetPartyBuffSource(() => buffs);
        h.SetPrompt(hp: 100, maxHp: 100, ma: 100, maxMa: 100);   // full, out of combat, nothing due
        Assert.Empty(h.CastsSent);

        h.Director.RequestManaRegenReroll("mreg");

        Assert.Equal(new[] { "mreg" }, h.CastsSent);
    }

    [Fact]
    public void ManaRegenReroll_RespectsOnePerRoundSlot_InCombat()
    {
        // In combat, a due heal spends the round's one between-round slot; a staged
        // reroll must then yield (not double-cast the same round) — the exact race
        // the old raw-wire reroll could lose.
        using Harness h = new();
        // The reroll's buff may cast in combat, so only the spent slot holds it.
        BuffSettings buffs = new() { Slots = { new BuffSlot { Spell = "mreg", BlessDuringCombat = true } } };
        h.Director.SetPartyBuffSource(() => buffs);
        h.Spells.MinorHealSpell = "heal";
        h.Health.MinorHealCombatTrigger = 70;
        h.SetPrompt(hp: 50, maxHp: 100, ma: 100, maxMa: 100, inCombat: true);   // 50% < 70% → heal fires, spends slot
        Assert.Contains("heal", h.CastsSent);
        h.CastsSent.Clear();

        h.Director.RequestManaRegenReroll("mreg");

        Assert.DoesNotContain("mreg", h.CastsSent);
    }
}
