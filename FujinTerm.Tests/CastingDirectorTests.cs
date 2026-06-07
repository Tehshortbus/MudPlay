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
        Assert.Equal("c heal", h.CastsSent[0]);
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
        public SpellsSettings Spells { get; set; } = new();
        public HealthSettings Health { get; set; } = new();

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
        Assert.Equal("c neutralize", h.CastsSent[0]);
    }

    [Fact]
    public void Cure_MovementPrevented_CastsCureHolds()
    {
        using CureHarness h = new();
        h.Spells.CureHoldsSpell = "freedom";
        h.RecordCondition("Paralyze", MessageFlags.MovementPrevented, "paralyzed!");

        h.FeedLine("You have been paralyzed!");

        Assert.Single(h.CastsSent);
        Assert.Equal("c freedom", h.CastsSent[0]);
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
        Assert.Equal("c freedom", h.CastsSent[0]);
    }

    // ----- Buffing (Bless1–10 slot walk) -----------------------------

    [Fact]
    public void Buff_OutOfCombat_FiresFirstUnactiveSlot()
    {
        using CureHarness h = new();
        h.Spells.Bless1Spell = "bless";
        h.Spells.Bless2Spell = "haste";
        h.Health.BlessIfAboveMa = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;
        h.State.Position = PlayerPosition.Standing;

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("c bless", h.CastsSent[0]);
    }

    [Fact]
    public void Buff_SkipsActiveBuff_PicksNext()
    {
        using CureHarness h = new();
        h.Spells.Bless1Spell = "bless";
        h.Spells.Bless2Spell = "haste";
        h.Health.BlessIfAboveMa = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;

        // bless is already active — record applied without ends.
        h.RecordCondition("bless", MessageFlags.None,
            applied: "You are blessed!", endsWith: "Your blessing fades.");
        h.FeedLine("You are blessed!");
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();

        h.Director.Evaluate();

        Assert.Single(h.CastsSent);
        Assert.Equal("c haste", h.CastsSent[0]);
    }

    [Fact]
    public void Buff_AllActive_NoCast()
    {
        using CureHarness h = new();
        h.Spells.Bless1Spell = "bless";
        h.Health.BlessIfAboveMa = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;
        h.State.InCombat = false;

        h.RecordCondition("bless", MessageFlags.None,
            applied: "You are blessed!", endsWith: "Your blessing fades.");
        h.FeedLine("You are blessed!");
        h.CastsSent.Clear();
        h.Cast.OnCombatTick();

        h.Director.Evaluate();

        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void Buff_BelowMaFloor_NoCast()
    {
        // MA too low — saving for heals.
        using CureHarness h = new();
        h.Spells.Bless1Spell = "bless";
        h.Health.BlessIfAboveMa = 70;
        h.State.MaxMa = 100;
        h.State.Ma = 50;
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
        h.Spells.Bless1Spell = "bless";
        h.Health.BlessIfAboveMa = 50;
        h.State.MaxMa = 100;
        h.State.Ma = 80;

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
        Assert.Equal("c fullheal", h.CastsSent[0]);
    }
}
