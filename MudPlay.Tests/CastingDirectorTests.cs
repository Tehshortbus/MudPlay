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
        public bool AutoBlessEnabled { get; set; } = true;

        /// <summary>Cast-code → required mana. Empty by default, so the lookup
        /// returns null (unknown ⇒ no affordability block) and the legacy tests
        /// behave exactly as before. Populate to exercise the mana gate.</summary>
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

    // ----- Tier 2 cures (game-data Messages driven) -----------------

    private sealed class CureHarness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public PlayerState State { get; } = new();
        public MessageStore Messages { get; } = new();
        public CastCoordinator Cast { get; }
        public ConditionTracker Conditions { get; }
        public CastingDirector Director { get; }
        public List<string> CastsSent { get; } = new();
        public List<string> SelfBuffCast { get; } = new();
        public SpellsSettings Spells { get; set; } = new();
        public HealthSettings Health { get; set; } = new();
        public bool AutoBlessEnabled { get; set; } = true;
        public bool AutoHealRestEnabled { get; set; } = true;

        /// <summary>Extra unified-list buffs (party / member / whole-party) beyond the
        /// self-bless the tests set via <see cref="Spells"/>. Rarely used here.</summary>
        public BuffSettings PartyBuffs { get; } = new();

        /// <summary>When true the triggered-rest gate reports an active recovery
        /// rest (HP/MA below rest-if-below), so the bless "while resting" gate
        /// engages. A bare Position=Resting does NOT set this — idle resting still
        /// buffs.</summary>
        public bool TriggeredRest { get; set; }

        /// <summary>When true the buff-strip-room gate reports the current room
        /// removes buffs on entry, so the Buffing category is suppressed.</summary>
        public bool BuffStripRoom { get; set; }

        /// <summary>Test clock — buff-expiry math reads this so tests can
        /// advance time deterministically.</summary>
        public DateTime Now { get; set; } =
            new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Buff cast-code → (caster template, duration seconds).
        /// Populate to give a self-buff a duration timer; an unmapped code
        /// resolves to null (no timer ⇒ always due ⇒ re-attempt each pass).
        /// In this harness the confirmed condition's Name doubles as the
        /// buff short, so this keys on the condition Name.</summary>
        public Dictionary<string, (string Caster, long Duration)> BuffInfo { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public CureHarness()
        {
            DefaultPatterns.Seed(Router);
            Cast = new CastCoordinator(Router, Log);
            Cast.SetWireSender(_ => { });
            Cast.CastSent += CastsSent.Add;
            Conditions = new ConditionTracker(Messages, Log);
            Director = new CastingDirector(State, Cast, Conditions,
                readSpells: () => Spells,
                readHealth: () => Health,
                isEnabled: () => AutoHealRestEnabled,
                log: Log);
            Director.SetAutoBlessGate(() => AutoBlessEnabled);
            Director.SetTriggeredRestGate(() => TriggeredRest);
            Director.SetBuffStripRoomGate(() => BuffStripRoom);
            Director.SetClock(() => Now);
            // Self buffs now live in the unified list. Fold the tests' self-bless
            // config (Spells.BlessSlots / WhenHp/MaFull) into it at read time, exactly
            // like ProfileMigrations does on load, so the tests set them the old way.
            Director.SetPartyBuffSource(FoldedBuffs);
            // Self-buff confirmation: the condition's Name is the buff short
            // in this harness, so map a fired record back to its Name and
            // resolve its duration from BuffInfo.
            Director.SetBuffDurationSources(
                code => BuffInfo.TryGetValue(code, out (string Caster, long Duration) info)
                    ? (info.Caster, info.Duration)
                    : null,
                record => record.Name);
            // Capture the reroll sink so a test can assert a self-buff CAST is
            // reported to the mana-regen reroll engine (fired from the send path).
            Director.SetSelfBuffCastSink(SelfBuffCast.Add);
            // Healthy baseline so Tier-1 doesn't fire over the cure path.
            State.MaxHp = 200;
            State.Hp = 200;
            State.HasPromptData = true;
        }

        // Mirror ProfileMigrations' v2→v3 fold: BlessSlots (in order) + WhenHp/MaFull
        // become CastOnSelf slots, prepended to any explicit unified-list buffs.
        private BuffSettings FoldedBuffs()
        {
            BuffSettings merged = new();
            foreach (System.Collections.Generic.KeyValuePair<int, string> kv in
                     Spells.BlessSlots.OrderBy(k => k.Key))
            {
                if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                int margin = Spells.BlessSlotRecastMargins.TryGetValue(kv.Key, out int m)
                    ? m : SpellsSettings.DefaultBlessRecastMarginSec;
                merged.Slots.Add(new BuffSlot { Spell = kv.Value, CastOnSelf = true, RecastMarginSec = margin });
            }
            if (!string.IsNullOrWhiteSpace(Spells.WhenHpFullSpell))
                merged.Slots.Add(new BuffSlot { Spell = Spells.WhenHpFullSpell, CastOnSelf = true, OnlyWhenHpFull = true });
            if (!string.IsNullOrWhiteSpace(Spells.WhenMaFullSpell))
                merged.Slots.Add(new BuffSlot { Spell = Spells.WhenMaFullSpell, CastOnSelf = true, OnlyWhenMaFull = true });
            // Mana-regen also folds into the unified list now (v4 migration) — a
            // maintained CastOnSelf slot, appended after the bless / when-full slots.
            if (!string.IsNullOrWhiteSpace(Spells.MaRegenSpell))
                merged.Slots.Add(new BuffSlot { Spell = Spells.MaRegenSpell, CastOnSelf = true });
            merged.Slots.AddRange(PartyBuffs.Slots);
            return merged;
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

        public void FeedLine(string text)
        {
            var emitted = new LineExtractor.EmittedLine(
                text, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false);
            typeof(ConditionTracker)
                .GetMethod("OnLine",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!
                .Invoke(Conditions, new object[] { emitted });
        }

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
        h.Spells.MaRegenSpell = "ntap";
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
        h.Cast.OnCombatTick();      // clear recent-cast cooldown

        // Trigger a fresh evaluation (combat tick path).
        h.Director.OnCombatTick();

        Assert.Single(h.CastsSent);
        Assert.Equal("freedom", h.CastsSent[0]);
    }

    // ----- Buffing (bless slot walk) ---------------------------------

    [Fact]
    public void Buff_OutOfCombat_FiresFirstUnactiveSlot()
    {
        using CureHarness h = new();
        h.Spells.BlessSlots[1] ="bless";
        h.Spells.BlessSlots[2] ="haste";
        h.Health.BlessIfAboveMa = 50;
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
        h.Health.BlessIfAboveMa = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Standing;
        h.CastsSent.Clear();
        h.Spells.BlessSlots[1] = "bless";   // configured after state — the setup cascade can't have cast it

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
        h.Health.BlessIfAboveMa = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Standing;
        h.CastsSent.Clear();
        h.Spells.BlessSlots[1] = "bless";

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
        h.Spells.SelfBlessDuringCombat = true;   // buffs allowed in combat — only the idle gate can stop this
        h.Health.BlessIfAboveMa = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.Position = PlayerPosition.Standing;
        h.CastsSent.Clear();
        h.Spells.BlessSlots[1] = "bless";

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
        h.Spells.BlessSlots[1] = "bless";
        h.Health.BlessIfAboveMa = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void Buff_InCombat_DuringCombatOn_Casts()
    {
        // Opt-in: with SelfBlessDuringCombat the same fight allows the recast.
        using CureHarness h = new();
        h.State.InCombat = true;
        h.Spells.SelfBlessDuringCombat = true;
        h.Spells.BlessSlots[1] = "bless";
        h.Health.BlessIfAboveMa = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("bless", h.CastsSent[0]);
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
        h.Spells.SelfBlessWhileResting = false;  // the new default
        h.Spells.BlessSlots[1] = "bless";
        h.Health.BlessIfAboveMa = 50;
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
        h.Spells.SelfBlessWhileResting = false;
        h.Spells.BlessSlots[1] = "bless";
        h.Health.BlessIfAboveMa = 50;
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
        h.Spells.SelfBlessWhileResting = true;   // opt-in override
        h.Spells.BlessSlots[1] = "bless";
        h.Health.BlessIfAboveMa = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("bless", h.CastsSent[0]);
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
        h.Spells.BlessSlots[1] = "bless";
        h.Health.BlessIfAboveMa = 50;
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
        h.Spells.BlessSlots[1] ="bless";
        h.Health.BlessIfAboveMa = 50;
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
        h.Spells.BlessSlots[1] = "bless";
        h.Health.BlessIfAboveMa = 50;
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
        h.Spells.BlessSlots[1] = "bless";
        h.Health.BlessIfAboveMa = 50;
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
        h.Spells.BlessSlots[1] ="bless";
        h.Spells.BlessSlots[2] ="haste";
        h.Health.BlessIfAboveMa = 50;
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

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("haste", h.CastsSent[0]);
    }

    [Fact]
    public void Buff_AllActive_NoCast()
    {
        using CureHarness h = new();
        h.Spells.BlessSlots[1] ="bless";
        h.Health.BlessIfAboveMa = 50;
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
        h.Spells.BlessSlots[1] ="bless";
        h.BuffInfo["bless"] = (string.Empty, 300);
        h.Health.BlessIfAboveMa = 50;
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
    public void Buff_ConditionEnded_ClearsTimer_RecastsImmediately()
    {
        // Server-confirmed early wear-off drops the timer so the wear-off line's
        // own re-evaluation recasts immediately — once — without waiting out the
        // stale clock. The optimistic on-send timer then blocks a same-round
        // double.
        using CureHarness h = new();
        h.Spells.BlessSlots[1] ="bless";
        h.BuffInfo["bless"] = (string.Empty, 300);
        h.Health.BlessIfAboveMa = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.RecordCondition("bless", MessageFlags.None,
            applied: "You are blessed!", endsWith: "Your blessing fades.");

        h.Director.Evaluate();                 // first cast
        h.FeedLine("You are blessed!");        // 300s timer
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();

        h.Director.Evaluate();                 // mid-duration → no recast
        Assert.Empty(h.CastsSent);

        h.FeedLine("Your blessing fades.");    // wear-off clears timer → recasts now
        Assert.Single(h.CastsSent);
        Assert.Equal("bless", h.CastsSent[0]);

        // The recast re-armed the optimistic timer, so a stale re-evaluation this
        // round can't fire a second bless.
        h.Cast.OnCombatTick();
        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
    }

    [Fact]
    public void Buff_SelfPerSlotMargin_RecastsAtConfiguredLead_NotTheDefault()
    {
        // A slot with a 30s recast lead re-casts 30s before expiry — earlier than
        // the 15s default would.
        using CureHarness h = new();
        h.Spells.BlessSlots[1] = "bless";
        h.Spells.BlessSlotRecastMargins[1] = 30;
        h.BuffInfo["bless"] = (string.Empty, 300);
        h.Health.BlessIfAboveMa = 50;
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
        h.Spells.BlessSlots[1] = "bless";
        h.Spells.BlessSlotRecastMargins[1] = 0;
        h.BuffInfo["bless"] = (string.Empty, 300);
        h.Health.BlessIfAboveMa = 50;
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
        h.Spells.BlessSlots[1] = "bless";
        h.Spells.BlessSlotRecastMargins[1] = 0;
        h.BuffInfo["bless"] = (string.Empty, 300);
        h.Health.BlessIfAboveMa = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.RecordCondition("bless", MessageFlags.None,
            applied: "You are blessed!", endsWith: "Your blessing fades.");

        h.Director.Evaluate();
        h.FeedLine("You are blessed!");        // 300s timer
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();

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
        h.Spells.BlessSlots[1] = "tige";
        h.BuffInfo["tige"] = (string.Empty, 300);
        h.Health.BlessIfAboveMa = 70;          // 70% of 5 kai → floor 4
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
        h.Spells.BlessSlots[1] = "bles";
        h.BuffInfo["bles"] = (string.Empty, 300);   // long duration → phantom timer would suppress
        h.Health.BlessIfAboveMa = 50;
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
        h.Spells.BlessSlots[1] = "bles";
        h.BuffInfo["bles"] = (string.Empty, 300);
        h.Health.BlessIfAboveMa = 50;
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
        h.Spells.SelfBlessDuringCombat = true;   // opt in to blessing mid-fight
        h.Spells.BlessSlots[1] = "mshi";
        h.Spells.BlessSlots[2] = "armr";
        h.BuffInfo["mshi"] = (string.Empty, 300);
        h.BuffInfo["armr"] = (string.Empty, 300);
        h.Health.BlessIfAboveMa = 0;
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
        h.Spells.BlessSlots[1] = "mshi";
        h.BuffInfo["mshi"] = (string.Empty, 300);
        h.Health.BlessIfAboveMa = 0;
        h.State.MaxMa = 100;
        h.State.Ma = 100;
        h.State.InCombat = false;   // out of combat: isolate the failure handling from the round gate
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
        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
        Assert.Equal("mshi", h.CastsSent[0]);
    }

    [Fact]
    public void SnapshotActiveBuffs_ReflectsArmedTimers_ClearedByReset()
    {
        // The Buff Watchdog reads live timers through this snapshot — each armed
        // buff surfaces as (Target="" self, Short, Until, MarginSec, TotalSec).
        using CureHarness h = new();
        h.Spells.BlessSlots[1] = "bles";
        h.BuffInfo["bles"] = (string.Empty, 300);
        h.Health.BlessIfAboveMa = 0;
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
        h.Spells.BlessSlots[1] = "bles";
        h.BuffInfo["bles"] = (string.Empty, 300);
        h.Health.BlessIfAboveMa = 0;
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
        h.Spells.BlessSlots[1] = "bles";
        h.BuffInfo["bles"] = (string.Empty, 300);
        h.Health.BlessIfAboveMa = 0;
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
        h.Spells.BlessSlots[1] = "bles";
        h.Spells.BlessSlotRecastMargins[1] = 20;   // the slot's configured recast lead
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
    public void PauseResume_ClearsSelfTimersOnReconnect()
    {
        // WE were the one offline, so on reconnect our own buffs are uncertain — the
        // self timers (keyed "") are cleared so they re-establish fresh.
        using CureHarness h = new();
        h.Spells.BlessSlots[1] = "bles";
        h.BuffInfo["bles"] = (string.Empty, 300);
        h.Director.NoteManualBuffCast("bles");     // a self timer
        Assert.Single(h.Director.SnapshotActiveBuffs());

        h.Director.PauseBuffTimers();              // drop
        h.Now = h.Now.AddSeconds(45);              // 45s offline
        h.Director.ResumeBuffTimers();             // reconnect

        Assert.Empty(h.Director.SnapshotActiveBuffs());   // self cleared
    }

    [Fact]
    public void PausedAtUtc_ReflectsFreezeState()
    {
        // The Buff Watchdog reads PausedAtUtc to freeze its display at the drop instant
        // (its heartbeat is a wall clock that keeps ticking while disconnected).
        using CureHarness h = new();
        h.Spells.BlessSlots[1] = "bles";
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
    public void SelfBuff_CoveredByPartyBuff_IsNotCast()
    {
        // In a party, a party-wide buff (chan) that removes bless supersedes the self-cast:
        // the director skips self-casting bles and lets the party buff cover us.
        using CureHarness h = new();
        h.Spells.BlessSlots[1] = "bles";
        h.BuffInfo["bles"] = (string.Empty, 300);
        h.Health.BlessIfAboveMa = 0;
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
        h.Spells.BlessSlots[1] = "bles";
        h.BuffInfo["bles"] = (string.Empty, 300);
        h.Health.BlessIfAboveMa = 0;
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
        h.Spells.BlessSlots[1] = "tige";
        h.BuffInfo["tige"] = (string.Empty, 300);
        h.Health.MaThresholdMode = ThresholdMode.Absolute;
        h.Health.BlessIfAboveMa = 4;
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
        h.Spells.BlessSlots[1] ="bless";
        h.Health.BlessIfAboveMa = 70;
        h.State.MaxMa = 100;
        h.State.Ma = 50;
        h.State.InCombat = false;

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void Buff_StealthGate_Suppressed()
    {
        // Stealth gate (wired by AppServices to StealthManager.IsStealthed)
        // suppresses buff casts to keep the backstab window open.
        using CureHarness h = new();
        h.Director.SetStealthGate(() => true);
        h.Spells.BlessSlots[1] ="bless";
        h.Health.BlessIfAboveMa = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void Buff_InCombat_Suppressed()
    {
        // v1 hard-gates buffs out of combat — never burn a round
        // mid-fight on a buff. Set InCombat FIRST so the property-
        // change cascade below doesn't fire a Buff cast through the
        // (still-default) InCombat=false window.
        using CureHarness h = new();
        h.State.InCombat = true;
        h.Spells.BlessSlots[1] ="bless";
        h.Health.BlessIfAboveMa = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    // ----- Item-cast buffs (PR 10.18 #token Bless slot) --------------

    [Fact]
    public void ItemCastBuff_FiresSequence_BypassesRawCast_AndKeysRecastByToken()
    {
        using CureHarness h = new();
        const string token = "#emerald tipped crozier";
        h.Spells.BlessSlots[1] =token;
        h.Health.BlessIfAboveMa = 50;
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
        h.Spells.BlessSlots[1] =token;
        h.Health.BlessIfAboveMa = 50;
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
        h.Spells.BlessSlots[1] =token;
        // MA below the bless floor: a mana-drawing buff would be suppressed.
        h.Health.BlessIfAboveMa = 50;
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
        h.Spells.BlessSlots[1] =token;
        h.Health.BlessIfAboveMa = 50;
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
        h.Health.BlessIfAboveMa = 50;
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
        h.Spells.BlessSlots[1] ="bless";
        h.Spells.MaRegenSpell = "kindred";
        h.Health.BlessIfAboveMa = 50;
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

        Assert.Single(h.CastsSent);
        Assert.Equal("kindred", h.CastsSent[0]);
    }

    [Fact]
    public void Utility_WhenHpFullSpell_FiresInBuffSlot()
    {
        // WhenHpFull is a buff with extra eligibility (HP at max).
        // Out-of-combat, MA above BlessIfAboveMa, HP at max → fires.
        using CureHarness h = new();
        h.Spells.WhenHpFullSpell = "detect";
        h.Health.BlessIfAboveMa = 50;
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
        h.Spells.WhenHpFullSpell = "detect";
        h.Health.BlessIfAboveMa = 50;
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
        h.Spells.WhenHpFullSpell = "detect";
        h.Health.BlessIfAboveMa = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;

        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void Utility_WhenHpFullSpell_AlreadyActive_NoCast()
    {
        using CureHarness h = new();
        h.Spells.WhenHpFullSpell = "detect";
        h.Health.BlessIfAboveMa = 50;
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
                log: Log);
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
    public void PartyHeal_SelfIsLowest_CastsBareCode_NotOwnParName()
    {
        // Live bug: the minor party-heal picked the self member and cast
        // "mihe Raijin Par" — appending our own par-row "Given Family"
        // name. MajorMUD self-casts take the bare code; the trailing name
        // makes the server reject the cast. Fix: MemberTarget(self) → null.
        using PartyHarness h = new();
        h.PartySettings.MinorPartyHealSpell = "mihe";
        h.PartySettings.MinorHealMemberThresholdPercent = 70;
        PartyMember self = h.AddMember("Raijin Par", hpPercent: 55);
        self.IsSelf = true;

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("mihe", h.CastsSent[0]);
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
    public void PartyHeal_NoPartySettings_NoCast()
    {
        // Mirror real wiring with no PartyState reader — just falls
        // through to other categories.
        using PartyHarness h = new();
        // No spells configured anywhere.
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);
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
        public List<string> CastsSent { get; } = new();
        public SpellsSettings Spells { get; set; } = new();
        public HealthSettings Health { get; set; } = new();
        public PartySettings PartySettings { get; set; } = new();

        /// <summary>The party-buff plan the director reads (CharacterProfile.PartyBuffs).</summary>
        public BuffSettings PartyBuffs { get; } = new();

        /// <summary>Given names currently in the room — the presence gate. AddMember
        /// puts a member here by default; drop a name to model an absent member.</summary>
        public HashSet<string> InRoom { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Spell shorts that are whole-party (Targets 10/13).</summary>
        public HashSet<string> WholeParty { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>When true the triggered-rest gate reports an active recovery
        /// rest. A bare Position=Resting does NOT set this — idle resting still
        /// buffs the party.</summary>
        public bool TriggeredRest { get; set; }

        public DateTime Now { get; set; } =
            new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Buff short → (CasterMessage template, duration seconds).</summary>
        public Dictionary<string, (string Caster, long Duration)> BuffInfo { get; } =
            new(StringComparer.OrdinalIgnoreCase);

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
                log: Log);
            Director.SetClock(() => Now);
            Director.SetTriggeredRestGate(() => TriggeredRest);
            Director.SetBuffDurationSources(
                code => BuffInfo.TryGetValue(code, out (string Caster, long Duration) info)
                    ? (info.Caster, info.Duration)
                    : null,
                // No self-confirm path exercised here.
                _ => null);
            Director.SetPartyBuffSource(() => PartyBuffs);
            Director.SetRoomPresenceCheck(g => InRoom.Contains(g));
            Director.SetPartyWideBuffCheck(s => WholeParty.Contains(s));
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

        /// <summary>A single-target slot that blesses the named members (given names).</summary>
        public BuffSlot AddTargetSlot(string spell, params string[] targets)
        {
            BuffSlot slot = new() { Spell = spell };
            foreach (string t in targets) slot.Targets.Add(t.ToLowerInvariant());
            PartyBuffs.Slots.Add(slot);
            return slot;
        }

        /// <summary>A single-target slot that blesses every in-party, in-room member.</summary>
        public BuffSlot AddAllMembersSlot(string spell)
        {
            BuffSlot slot = new() { Spell = spell, AllMembers = true };
            PartyBuffs.Slots.Add(slot);
            return slot;
        }

        /// <summary>A whole-party slot (one cast, no target). on = WholePartyOn.</summary>
        public BuffSlot AddWholePartySlot(string spell, bool on = true)
        {
            WholeParty.Add(spell);
            BuffSlot slot = new() { Spell = spell, WholePartyOn = on };
            PartyBuffs.Slots.Add(slot);
            return slot;
        }

        /// <summary>Simulate a server line arriving on the wired
        /// <see cref="LineExtractor"/> by invoking the director's private
        /// OnLine handler directly (matches the ConditionTracker.OnLine
        /// reflection idiom used elsewhere in this file).</summary>
        public void Confirm(string casterLine)
        {
            var emitted = new LineExtractor.EmittedLine(
                casterLine, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false);
            typeof(CastingDirector)
                .GetMethod("OnLine",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!
                .Invoke(Director, new object[] { emitted });
        }

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
        h.Health.BlessIfAboveMa = 0;
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
        h.Health.BlessIfAboveMa = 0;
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
        h.Health.BlessIfAboveMa = 0;
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
        h.Health.BlessIfAboveMa = 0;
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
        h.Health.BlessIfAboveMa = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");
        h.InRoom.Remove("Raijin");           // hiding ⇒ absent from "Also here:"

        h.Director.Evaluate();               // attempt (in party ⇒ here)
        Assert.Single(h.CastsSent);

        h.Confirm("You do not see Raijin here!");   // hidden ⇒ back off
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);           // still hiding ⇒ skipped, no spam

        h.Director.NoteRoomChanged();        // we moved ⇒ retry
        h.Cast.OnCombatTick();
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
        h.Health.BlessIfAboveMa = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");
        h.InRoom.Remove("Raijin");           // hiding

        h.Director.Evaluate();
        h.Confirm("You do not see Raijin here!");
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();
        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);           // still hiding

        h.InRoom.Add("Raijin");              // reappears in "Also here:" (unhid)
        h.Cast.OnCombatTick();
        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
    }

    [Fact]
    public void PartyBless_Reconnect_ClearsSelfKeepsPartyCountingDown()
    {
        // On OUR reconnect: self timers clear; the party member (online the whole time)
        // keeps their ABSOLUTE expiry — it isn't shifted forward, so it now reads the
        // real reduced remaining.
        using PartyBlessHarness h = new();
        h.Health.BlessIfAboveMa = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.BuffInfo["mysh"] = (string.Empty, 200);
        h.AddMember("Raijin");

        h.Director.Evaluate();                          // cast bles on Raijin
        h.Confirm("You cast bless on Raijin!");         // arm ("raijin", bles)
        h.Director.NoteManualBuffCast("mysh");          // arm a self buff ("")

        DateTime partyUntil = default;
        foreach (Game.Spells.ActiveBuffTimer t in h.Director.SnapshotActiveBuffs())
            if (t.Target.Length > 0) partyUntil = t.Until;
        Assert.Equal(2, h.Director.SnapshotActiveBuffs().Count);

        h.Director.PauseBuffTimers();
        h.Now = h.Now.AddSeconds(45);
        h.Director.ResumeBuffTimers();

        Game.Spells.ActiveBuffTimer kept = Assert.Single(h.Director.SnapshotActiveBuffs());
        Assert.Equal("raijin", kept.Target);            // self gone, party kept
        Assert.Equal(partyUntil, kept.Until);           // absolute expiry unchanged (not shifted)
    }

    [Fact]
    public void PartyBless_OurDeath_ClearsSelfKeepsPartyTimers()
    {
        // OUR death wipes OUR buffs only — self timers clear, the (alive) party member's
        // timer stays so we don't needlessly re-bless them.
        using PartyBlessHarness h = new();
        h.Health.BlessIfAboveMa = 0;
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

    [Fact]
    public void PartyBless_MemberDeath_ClearsThatMembersTimersOnly()
    {
        // A party member's death wipes THEIR buffs — clear the timers we hold on them;
        // every other member's timer stays.
        using PartyBlessHarness h = new();
        h.Health.BlessIfAboveMa = 0;
        h.AddAllMembersSlot("bles");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");
        h.AddMember("Goldar");

        h.Director.Evaluate();                           // casts on the first (Raijin)
        h.Confirm("You cast bless on Raijin!");
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();
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
        h.Health.BlessIfAboveMa = 0;
        h.AddAllMembersSlot("bles");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");
        h.AddMember("Goldar");

        h.Director.Evaluate();
        h.Confirm("You cast bless on Raijin!");
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();
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
        h.Health.BlessIfAboveMa = 0;
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
        h.Health.BlessIfAboveMa = 0;
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
        h.Health.BlessIfAboveMa = 0;
        h.AddWholePartySlot("chan", on: false);   // all-off
        h.AddMember("Raijin");

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyBless_ConfirmStartsTimer_NoImmediateRecast()
    {
        using PartyBlessHarness h = new();
        h.Health.BlessIfAboveMa = 0;
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
        h.Health.BlessIfAboveMa = 0;
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
        h.Health.BlessIfAboveMa = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");

        h.Director.Evaluate();
        Assert.Single(h.CastsSent);

        // No Confirm() — timer never starts.
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();

        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
        Assert.Equal("bles Raijin", h.CastsSent[0]);
    }

    [Fact]
    public void PartyBless_RecastWithinExpiryWindow()
    {
        using PartyBlessHarness h = new();
        h.Health.BlessIfAboveMa = 0;
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
        h.Health.BlessIfAboveMa = 0;
        h.AddAllMembersSlot("bles");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");
        h.AddMember("Goldar");

        h.Director.Evaluate();
        Assert.Equal("bles Raijin", h.CastsSent[^1]);
        h.Confirm("You cast bless on Raijin!");
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();

        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
        Assert.Equal("bles Goldar", h.CastsSent[0]);
    }

    [Fact]
    public void PartyBless_DuringCombatOff_NoCast()
    {
        using PartyBlessHarness h = new();
        h.PartySettings.BlessDuringCombat = false;
        h.State.InCombat = true;
        h.Health.BlessIfAboveMa = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");

        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyBless_WhileRestingOff_NoCast()
    {
        // A triggered recovery rest with the override off holds party-bless too.
        using PartyBlessHarness h = new();
        h.PartySettings.BlessWhileResting = false;
        h.State.Position = PlayerPosition.Resting;
        h.TriggeredRest = true;                  // active recovery rest
        h.Health.BlessIfAboveMa = 0;
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
        // party even with the "while resting" override off (its new default).
        using PartyBlessHarness h = new();
        h.PartySettings.BlessWhileResting = false;
        h.State.Position = PlayerPosition.Resting;
        h.TriggeredRest = false;                 // idle rest
        h.Health.BlessIfAboveMa = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");

        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
        Assert.Equal("bles Raijin", h.CastsSent[0]);
    }

    [Fact]
    public void PartyBless_DuringCombatOn_Casts()
    {
        // "During combat" is an opt-in override (off by default) — turn it on and
        // party-bless fires even in combat.
        using PartyBlessHarness h = new();
        h.PartySettings.BlessDuringCombat = true;
        h.State.InCombat = true;
        h.Health.BlessIfAboveMa = 0;
        h.AddTargetSlot("bles", "Raijin");
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin");

        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
        Assert.Equal("bles Raijin", h.CastsSent[0]);
    }

    // ----- Mana-regen reroll routes through the priority pass ----------

    [Fact]
    public void ManaRegenReroll_Staged_FiresThroughBuffPass()
    {
        // The reroller stages a reroll on the director; it casts through the
        // between-round pass (as a Buffing candidate), not on the raw wire.
        using Harness h = new();
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
        h.Spells.SelfBlessDuringCombat = true;   // allow a buff (the reroll) in combat
        h.Spells.MinorHealSpell = "heal";
        h.Health.MinorHealCombatTrigger = 70;
        h.SetPrompt(hp: 50, maxHp: 100, ma: 100, maxMa: 100, inCombat: true);   // 50% < 70% → heal fires, spends slot
        Assert.Contains("heal", h.CastsSent);
        h.CastsSent.Clear();

        h.Director.RequestManaRegenReroll("mreg");

        Assert.DoesNotContain("mreg", h.CastsSent);
    }
}
