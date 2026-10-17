using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

// Pins which spells are mana-regen roll spells, against the real stock Spells
// shapes, so the reroll paths stay code-driven and never regress into hardcoding
// spell names. Each fixture mirrors an actual stock spell's Abil / duration
// columns.
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

    [Fact]
    public void NatureTap_IsARoll()
        // #854: 145/0, Dur=100 — rolled mana-regen rate.
        => Assert.True(RegenSpellClassifier.IsManaRegenRoll(Spell(dur: 100, abilities: new SpellAbility(145, 0))));

    [Fact]
    public void ManaFlux_IsARoll()
        // #406: 145/0, Dur=70.
        => Assert.True(RegenSpellClassifier.IsManaRegenRoll(Spell(dur: 70, durInc: 1, durIncLvls: 1,
            abilities: new SpellAbility(145, 0))));

    [Fact]
    public void FixedManaRegenBonus_IsNotARoll()
        // 145 with a non-zero value lands the same every cast — nothing to reroll.
        => Assert.False(RegenSpellClassifier.IsManaRegenRoll(Spell(dur: 70, abilities: new SpellAbility(145, 12))));

    [Fact]
    public void RapidHealing_IsNotARoll()
        // #831: 123/100, Dur=60 — an HP-regen-rate buff.
        => Assert.False(RegenSpellClassifier.IsManaRegenRoll(Spell(dur: 60,
            abilities: new[] { new SpellAbility(123, 100), new SpellAbility(108, 0) })));

    [Fact]
    public void ChaosSurge_IsNotARoll()
        // #748: 150/0, 123/-150, ..., Dur=80 — a mana heal-over-time.
        => Assert.False(RegenSpellClassifier.IsManaRegenRoll(Spell(dur: 80,
            abilities: new[]
            {
                new SpellAbility(150, 0),
                new SpellAbility(123, -150),
                new SpellAbility(7, -50),
                new SpellAbility(36, 20),
            })));

    [Fact]
    public void DamageSpell_IsNotARoll()
        // elemental chaos #30: 17/0 (Damage(-MR)).
        => Assert.False(RegenSpellClassifier.IsManaRegenRoll(Spell(abilities: new SpellAbility(17, 0))));

    [Fact]
    public void EmptyFormula_IsNotARoll()
        => Assert.False(RegenSpellClassifier.IsManaRegenRoll(new SpellFormulaInput()));

    [Fact]
    public void ARollAmongOtherAbilities_IsStillARoll()
        // A contrived timed spell that both rolls mana-regen (145/0) and ticks HP (18).
        => Assert.True(RegenSpellClassifier.IsManaRegenRoll(Spell(dur: 50,
            abilities: new[] { new SpellAbility(145, 0), new SpellAbility(18, 0) })));

    // The reroller and the Settings readout classify the same way.
    [Fact]
    public void IsRollSpell_AgreesWithTheClassifier()
    {
        SpellFormulaInput natureTap = Spell(dur: 100, abilities: new SpellAbility(145, 0));
        SpellFormulaInput rapidHealing = Spell(dur: 60, abilities: new SpellAbility(123, 100));

        Assert.True(ManaRegenReroller.IsRollSpell(natureTap));
        Assert.False(ManaRegenReroller.IsRollSpell(rapidHealing));
    }
}
