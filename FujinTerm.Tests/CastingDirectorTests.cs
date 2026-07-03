using FujinTerm.Game;
using FujinTerm.Game.Conditions;
using FujinTerm.Game.Spells;
using FujinTerm.Models.GameData;
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
    public void DefaultPriority_MinorBeatsMajor_WhenBothCandidates()
    {
        // Default priority: Minor self heal=3, Major self heal=4.
        // HP=30% triggers BOTH (Minor threshold 70%, Major threshold
        // 40%). Lower priority number wins → Minor goes first. If
        // the user wants Major to win at life-threat, they re-order
        // priorities on the Spells tab.
        using Harness h = new();
        h.Spells.MajorHealSpell = "fullheal";
        h.Spells.MinorHealSpell = "heal";
        h.Health.MajorHealCombatTrigger = 40;
        h.Health.MinorHealCombatTrigger = 70;

        h.SetPrompt(hp: 30, maxHp: 100, inCombat: true);

        Assert.Single(h.CastsSent);
        Assert.Equal("heal", h.CastsSent[0]);
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
        public List<string> SelfBuffLanded { get; } = new();
        public SpellsSettings Spells { get; set; } = new();
        public HealthSettings Health { get; set; } = new();
        public bool AutoBlessEnabled { get; set; } = true;

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
                isEnabled: () => true,
                log: Log);
            Director.SetAutoBlessGate(() => AutoBlessEnabled);
            Director.SetClock(() => Now);
            // Self-buff confirmation: the condition's Name is the buff short
            // in this harness, so map a fired record back to its Name and
            // resolve its duration from BuffInfo.
            Director.SetBuffDurationSources(
                code => BuffInfo.TryGetValue(code, out (string Caster, long Duration) info)
                    ? (info.Caster, info.Duration)
                    : null,
                record => record.Name);
            // Capture the reroll sink so a test can assert a confirmed self-buff
            // landing is reported to the mana-regen reroll engine.
            Director.SetSelfBuffLandedSink(SelfBuffLanded.Add);
            // Healthy baseline so Tier-1 doesn't fire over the cure path.
            State.MaxHp = 200;
            State.Hp = 200;
            State.HasPromptData = true;
        }

        public void RecordCondition(string name, MessageFlags flags,
                                    string applied, string endsWith = "")
        {
            Messages.Messages.Add(new MessageRecord(
                Id: MessageRecord.ComputeId(name, "", "", "", applied, endsWith),
                Name: name,
                Action: MessageAction.Ignore,
                Flags: flags,
                RawFlagsHex: (ushort)flags,
                Response: string.Empty,
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
    public void SelfBuffLanding_NotifiesTheRerollSink()
    {
        using CureHarness h = new();
        // A confirmed self-buff — its condition Name doubles as the cast short
        // in this harness, so a resolved landing reports that short to the sink.
        h.RecordCondition("ntap", MessageFlags.None, "You tap into the mana around you.");

        h.FeedLine("You tap into the mana around you.");

        Assert.Contains("ntap", h.SelfBuffLanded);
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
        // Server-confirmed early wear-off drops the timer so the next pass
        // re-attempts without waiting out the stale clock.
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

        h.FeedLine("Your blessing fades.");    // early wear-off → clears timer
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();
        h.Director.Evaluate();                 // due again immediately

        Assert.Single(h.CastsSent);
        Assert.Equal("bless", h.CastsSent[0]);
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

        public DateTime Now { get; set; } =
            new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Buff short → (CasterMessage template, duration seconds).</summary>
        public Dictionary<string, (string Caster, long Duration)> BuffInfo { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Class number → class name (mirrors Classes.json).</summary>
        public Dictionary<int, string> Classes { get; } = new();

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
            Director.SetBuffDurationSources(
                code => BuffInfo.TryGetValue(code, out (string Caster, long Duration) info)
                    ? (info.Caster, info.Duration)
                    : null,
                // No self-confirm path exercised here.
                _ => null);
            Director.SetClassResolver(
                n => Classes.TryGetValue(n, out string? name) ? name : null);
            // Healthy + full mana so survival categories never pre-empt buffs.
            State.MaxHp = 200; State.Hp = 200;
            State.MaxMa = 100; State.Ma = 100;
            State.HasPromptData = true;
        }

        public PartyMember AddMember(string name, string cls)
        {
            PartyMember m = new()
            {
                Name = name,
                Class = cls,
                BaselineHp = 100,
                HpPercent = 100,
                BaselineMp = 100,
                MpPercent = 100,
            };
            Party.Members.Add(m);
            return m;
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
    public void PartyBless_ClassMatch_CastsOnMember()
    {
        using PartyBlessHarness h = new();
        h.Classes[1] = "Mage";
        h.Health.BlessIfAboveMa = 0;
        h.PartySettings.BlessSlots[0].Spell = "bles";
        h.PartySettings.BlessSlots[0].ClassNumbers = new() { 1 };
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin", "Mage");

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("bles Raijin", h.CastsSent[0]);
    }

    [Fact]
    public void PartyBless_ClassMismatch_NoCast()
    {
        using PartyBlessHarness h = new();
        h.Classes[1] = "Mage";
        h.Health.BlessIfAboveMa = 0;
        h.PartySettings.BlessSlots[0].Spell = "bles";
        h.PartySettings.BlessSlots[0].ClassNumbers = new() { 1 };
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Goldar", "Warrior");

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyBless_SkipsSelf()
    {
        using PartyBlessHarness h = new();
        h.Classes[1] = "Mage";
        h.Health.BlessIfAboveMa = 0;
        h.PartySettings.BlessSlots[0].Spell = "bles";
        h.PartySettings.BlessSlots[0].ClassNumbers = new() { 1 };
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        PartyMember self = h.AddMember("Me", "Mage");
        self.IsSelf = true;

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyBless_ConfirmStartsTimer_NoImmediateRecast()
    {
        using PartyBlessHarness h = new();
        h.Classes[1] = "Mage";
        h.Health.BlessIfAboveMa = 0;
        h.PartySettings.BlessSlots[0].Spell = "bles";
        h.PartySettings.BlessSlots[0].ClassNumbers = new() { 1 };
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin", "Mage");

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
    public void PartyBless_NoConfirm_ReattemptsNextPass()
    {
        // Decision: no confirmation observed ⇒ no timer ⇒ re-attempt. The
        // CastCoordinator cooldown (cleared here) is the only spam guard.
        using PartyBlessHarness h = new();
        h.Classes[1] = "Mage";
        h.Health.BlessIfAboveMa = 0;
        h.PartySettings.BlessSlots[0].Spell = "bles";
        h.PartySettings.BlessSlots[0].ClassNumbers = new() { 1 };
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin", "Mage");

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
        h.Classes[1] = "Mage";
        h.Health.BlessIfAboveMa = 0;
        h.PartySettings.BlessSlots[0].Spell = "bles";
        h.PartySettings.BlessSlots[0].ClassNumbers = new() { 1 };
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin", "Mage");

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
        // Both Mages covered by one slot — cycle blesses each in turn.
        using PartyBlessHarness h = new();
        h.Classes[1] = "Mage";
        h.Health.BlessIfAboveMa = 0;
        h.PartySettings.BlessSlots[0].Spell = "bles";
        h.PartySettings.BlessSlots[0].ClassNumbers = new() { 1 };
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin", "Mage");
        h.AddMember("Goldar", "Mage");

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
        h.Classes[1] = "Mage";
        h.Health.BlessIfAboveMa = 0;
        h.PartySettings.BlessSlots[0].Spell = "bles";
        h.PartySettings.BlessSlots[0].ClassNumbers = new() { 1 };
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin", "Mage");

        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyBless_WhileRestingOff_NoCast()
    {
        using PartyBlessHarness h = new();
        h.PartySettings.BlessWhileResting = false;
        h.State.Position = PlayerPosition.Resting;
        h.Classes[1] = "Mage";
        h.Health.BlessIfAboveMa = 0;
        h.PartySettings.BlessSlots[0].Spell = "bles";
        h.PartySettings.BlessSlots[0].ClassNumbers = new() { 1 };
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin", "Mage");

        h.Director.Evaluate();
        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void PartyBless_DuringCombatOn_Casts()
    {
        // Default gate is ON — party-bless fires even in combat.
        using PartyBlessHarness h = new();
        h.State.InCombat = true;
        h.Classes[1] = "Mage";
        h.Health.BlessIfAboveMa = 0;
        h.PartySettings.BlessSlots[0].Spell = "bles";
        h.PartySettings.BlessSlots[0].ClassNumbers = new() { 1 };
        h.BuffInfo["bles"] = ("You cast {s} on {s}!", 300);
        h.AddMember("Raijin", "Mage");

        h.Director.Evaluate();
        Assert.Single(h.CastsSent);
        Assert.Equal("bles Raijin", h.CastsSent[0]);
    }
}
