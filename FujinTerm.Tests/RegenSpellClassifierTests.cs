using FujinTerm.Game.Spells;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the ability-code → recovery-role mapping against the real stock
/// <c>Spells</c> shapes, so the regen / HoT / reroll paths stay code-driven and
/// never regress into hardcoding spell names. Each fixture mirrors an actual
/// stock spell's <c>Abil</c> / duration columns.
/// </summary>
public sealed class RegenSpellClassifierTests
{
    private static SpellFormulaInput Spell(
        int dur = 0, int durInc = 0, int durIncLvls = 0, params SpellAbility[] abilities)
        => new()
        {
            Dur = dur,
            DurInc = durInc,
            DurIncLVLs = durIncLvls,
            Abilities = abilities,
        };

    // ----- mana-regen buffs (code 145) ------------------------------

    [Fact]
    public void NatureTap_IsManaRegenRoll()
        // #854: 145/0, Dur=100 — rolled mana-regen rate.
        => Assert.Equal(
            RegenSpellTraits.ManaRegenRoll,
            RegenSpellClassifier.Classify(Spell(dur: 100, abilities: new SpellAbility(145, 0))));

    [Fact]
    public void ManaFlux_IsManaRegenRoll()
        // #406: 145/0, Dur=70.
        => Assert.Equal(
            RegenSpellTraits.ManaRegenRoll,
            RegenSpellClassifier.Classify(Spell(dur: 70, durInc: 1, durIncLvls: 1,
                abilities: new SpellAbility(145, 0))));

    [Fact]
    public void FixedManaRegenBonus_IsManaRegenFixed()
        // 145 with a non-zero value lands the same every cast — nothing to reroll.
        => Assert.Equal(
            RegenSpellTraits.ManaRegenFixed,
            RegenSpellClassifier.Classify(Spell(dur: 70, abilities: new SpellAbility(145, 12))));

    // ----- HP heal-rate buff (code 123) -----------------------------

    [Fact]
    public void RapidHealing_IsHpRegenRateBuff()
        // #831: 123/100, Dur=60 — a positive HP-regen-rate buff.
        => Assert.Equal(
            RegenSpellTraits.HpRegenRateBuff,
            RegenSpellClassifier.Classify(Spell(dur: 60,
                abilities: new[] { new SpellAbility(123, 100), new SpellAbility(108, 0) })));

    // ----- HP heal-over-time (code 18 + duration) -------------------

    [Fact]
    public void Regeneration_IsHpHealOverTime()
        // #349: 18/0, Dur=7 (+1 per 4 levels).
        => Assert.Equal(
            RegenSpellTraits.HpHealOverTime,
            RegenSpellClassifier.Classify(Spell(dur: 7, durInc: 1, durIncLvls: 4,
                abilities: new SpellAbility(18, 0))));

    [Fact]
    public void RejuvinatingField_IsHpHealOverTime()
        // #1012: 18/0, Dur=10.
        => Assert.Equal(
            RegenSpellTraits.HpHealOverTime,
            RegenSpellClassifier.Classify(Spell(dur: 10, abilities: new SpellAbility(18, 0))));

    [Fact]
    public void InstantMinorHeal_IsNotAHoT()
        // #13: 18/0, Dur=0 — a one-shot heal, not heal-over-time.
        => Assert.Equal(
            RegenSpellTraits.None,
            RegenSpellClassifier.Classify(Spell(
                abilities: new[] { new SpellAbility(18, 0), new SpellAbility(108, 0) })));

    [Fact]
    public void LevelScaledDurationCountsAsTimed()
        // Dur base 0 but scales with level (DurInc/DurIncLVLs) — still a HoT.
        => Assert.Equal(
            RegenSpellTraits.HpHealOverTime,
            RegenSpellClassifier.Classify(Spell(durInc: 1, durIncLvls: 1,
                abilities: new SpellAbility(18, 0))));

    // ----- mana heal-over-time (code 150 + duration) ----------------

    [Fact]
    public void ChaosSurge_IsManaHealOverTime_NotHpRegenBuff()
        // #748: 150/0, 123/-150, ..., Dur=80. The negative 123 is a drain cost,
        // so the ONLY trait is the mana HoT — not an HP-regen-rate buff.
        => Assert.Equal(
            RegenSpellTraits.ManaHealOverTime,
            RegenSpellClassifier.Classify(Spell(dur: 80,
                abilities: new[]
                {
                    new SpellAbility(150, 0),
                    new SpellAbility(123, -150),
                    new SpellAbility(7, -50),
                    new SpellAbility(36, 20),
                })));

    [Fact]
    public void InstantManaRefill_IsNotAHoT()
        // bigheal #313: 18/0 + 150/0, Dur=0 — instant HP+mana, neither is a HoT.
        => Assert.Equal(
            RegenSpellTraits.None,
            RegenSpellClassifier.Classify(Spell(
                abilities: new[] { new SpellAbility(18, 0), new SpellAbility(150, 0) })));

    // ----- non-recovery spells --------------------------------------

    [Fact]
    public void DamageSpell_HasNoRecoveryTraits()
        // elemental chaos #30: 17/0 (Damage(-MR)).
        => Assert.Equal(
            RegenSpellTraits.None,
            RegenSpellClassifier.Classify(Spell(abilities: new SpellAbility(17, 0))));

    [Fact]
    public void EmptyFormula_IsNone()
        => Assert.Equal(RegenSpellTraits.None, RegenSpellClassifier.Classify(new SpellFormulaInput()));

    // ----- multi-trait & Has() helper -------------------------------

    [Fact]
    public void SpellCanCarryMultipleTraits()
    {
        // A contrived timed spell that both rolls mana-regen (145/0) and ticks
        // HP (18) — both traits report.
        RegenSpellTraits traits = RegenSpellClassifier.Classify(Spell(dur: 50,
            abilities: new[] { new SpellAbility(145, 0), new SpellAbility(18, 0) }));

        Assert.True(traits.HasFlag(RegenSpellTraits.ManaRegenRoll));
        Assert.True(traits.HasFlag(RegenSpellTraits.HpHealOverTime));
    }

    [Fact]
    public void Has_MatchesAnyRequestedTrait()
    {
        SpellFormulaInput chaosSurge = Spell(dur: 80,
            abilities: new[] { new SpellAbility(150, 0), new SpellAbility(123, -150) });

        Assert.True(RegenSpellClassifier.Has(chaosSurge,
            RegenSpellTraits.HpHealOverTime | RegenSpellTraits.ManaHealOverTime));
        Assert.False(RegenSpellClassifier.Has(chaosSurge, RegenSpellTraits.ManaRegenRoll));
    }

    // IsRollSpell now delegates here — confirm the alias still holds so the
    // reroller and Settings readout keep classifying the same way.
    [Fact]
    public void IsRollSpell_AgreesWithManaRegenRollTrait()
    {
        SpellFormulaInput natureTap = Spell(dur: 100, abilities: new SpellAbility(145, 0));
        SpellFormulaInput rapidHealing = Spell(dur: 60, abilities: new SpellAbility(123, 100));

        Assert.True(ManaRegenReroller.IsRollSpell(natureTap));
        Assert.False(ManaRegenReroller.IsRollSpell(rapidHealing));
    }
}
