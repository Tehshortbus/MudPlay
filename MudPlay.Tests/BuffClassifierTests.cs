using MudPlay.Game.Calculators;
using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

// Party-buff picker filter, confirmed against game data: zero energy cost + a
// real duration = a buff (not an attack, not an instant heal/cure/utility spell);
// Targets 2 = single-target-on-a-member, 10 / 13 = whole party. Self-only / enemy
// / item scopes are excluded from IsPartyBuff (IsAnyBuff below includes self-only
// too, for the unified self+party picker).
public sealed class BuffClassifierTests
{
    private static KnownSpell Spell(int targets, int energy, int dur = 10) =>
        new(Number: 1, Short: "abcd", Name: "Test", Magery: 0, MageryLvl: 0,
            ReqLevel: 1, Targets: targets, Formula: new SpellFormulaInput { EnergyCost = energy, Dur = dur });

    [Theory]
    [InlineData(10, true)]
    [InlineData(13, true)]
    [InlineData(2, false)]
    [InlineData(0, false)]
    public void IsWholeParty_Only10And13(int targets, bool expected) =>
        Assert.Equal(expected, BuffClassifier.IsWholeParty(targets));

    [Theory]
    [InlineData(0, 0, 10, true)]     // self-only, maintained (bless, troll skin)
    [InlineData(1, 0, 10, true)]     // self-only, maintained
    [InlineData(2, 0, 10, true)]     // single-target, maintained
    [InlineData(13, 0, 40, true)]    // whole-party, maintained
    [InlineData(0, 0, 0, false)]     // self-only, instant — minor healing / cure poison's shape
    [InlineData(1, 0, 0, false)]     // self-only, instant
    [InlineData(2, 0, 0, false)]     // single-target, instant — cure poison's actual shape
    [InlineData(13, 0, 0, false)]    // whole-party, instant — greater healing rain's actual shape
    [InlineData(0, 500, 10, false)]  // energy cost ⇒ an attack, not a buff, regardless of duration
    [InlineData(4, 0, 10, false)]    // monster (enemy) scope, even if it happened to carry a duration
    public void IsAnyBuff(int targets, int energy, int dur, bool expected) =>
        Assert.Equal(expected, BuffClassifier.IsAnyBuff(Spell(targets, energy, dur)));

    [Fact]
    public void HasDuration_LevelScaledDurationWithZeroBase_CountsAsMaintained()
    {
        // Dur itself is 0 but DurInc/DurIncLVLs mean it grows from level 1 — still
        // a real, maintainable duration (mirrors RegenSpellClassifier's HoT gate).
        SpellFormulaInput formula = new() { Dur = 0, DurInc = 3, DurIncLVLs = 2 };
        Assert.True(BuffClassifier.HasDuration(formula));
    }

    [Fact]
    public void HasDuration_AllZero_IsInstant()
    {
        Assert.False(BuffClassifier.HasDuration(new SpellFormulaInput()));
    }

    private static SpellFormulaInput Formula(params int[] abilCodes) =>
        new() { Abilities = System.Array.ConvertAll(abilCodes, c => new SpellAbility(c, 0)) };

    // Confirmed Paradigm data (report: user's evil Priest got recommended
    // "protection from evil" — Abil 111, non-evil only — and never got offered
    // "unholy armour", the Abil-98 evil-only spell that mutually removes
    // "holy armour"). Abil 111 = "protection from evil"'s actual shape.
    [Theory]
    [InlineData(AlignmentBucket.Good, true)]
    [InlineData(AlignmentBucket.Neutral, true)]
    [InlineData(AlignmentBucket.Evil, false)]
    [InlineData(null, true)]   // unknown alignment ⇒ don't guess, don't exclude
    public void IsAlignmentEligible_NonEvilOnly_ProtectionFromEvilShape(AlignmentBucket? alignment, bool expected) =>
        Assert.Equal(expected, BuffClassifier.IsAlignmentEligible(Formula(111), alignment));

    // "unholy armour"'s actual shape — Abil 98, evil only.
    [Theory]
    [InlineData(AlignmentBucket.Good, false)]
    [InlineData(AlignmentBucket.Neutral, false)]
    [InlineData(AlignmentBucket.Evil, true)]
    [InlineData(null, true)]
    public void IsAlignmentEligible_EvilOnly_UnholyArmourShape(AlignmentBucket? alignment, bool expected) =>
        Assert.Equal(expected, BuffClassifier.IsAlignmentEligible(Formula(98), alignment));

    // "vile ward"'s actual shape — Abil 110, NOT-good only (i.e. Neutral or Evil;
    // report: user flagged this as the opposite direction from NotEvil — a
    // "Not Good"-restricted spell is exactly what an Evil character CAN cast).
    [Theory]
    [InlineData(AlignmentBucket.Good, false)]
    [InlineData(AlignmentBucket.Neutral, true)]
    [InlineData(AlignmentBucket.Evil, true)]
    [InlineData(null, true)]
    public void IsAlignmentEligible_NotGoodOnly_VileWardShape(AlignmentBucket? alignment, bool expected) =>
        Assert.Equal(expected, BuffClassifier.IsAlignmentEligible(Formula(110), alignment));

    [Theory]
    [InlineData(AlignmentBucket.Good, true)]
    [InlineData(AlignmentBucket.Neutral, false)]
    [InlineData(AlignmentBucket.Evil, false)]
    public void IsAlignmentEligible_GoodOnly(AlignmentBucket? alignment, bool expected) =>
        Assert.Equal(expected, BuffClassifier.IsAlignmentEligible(Formula(97), alignment));

    [Theory]
    [InlineData(AlignmentBucket.Good, false)]
    [InlineData(AlignmentBucket.Neutral, true)]
    [InlineData(AlignmentBucket.Evil, false)]
    public void IsAlignmentEligible_NeutralOnly(AlignmentBucket? alignment, bool expected) =>
        Assert.Equal(expected, BuffClassifier.IsAlignmentEligible(Formula(112), alignment));

    [Fact]
    public void IsAlignmentEligible_NoAlignmentAbility_AlwaysEligible()
    {
        SpellFormulaInput formula = Formula(4, 58, 22, 115, 122);   // "zeal"'s real ability set
        Assert.True(BuffClassifier.IsAlignmentEligible(formula, AlignmentBucket.Good));
        Assert.True(BuffClassifier.IsAlignmentEligible(formula, AlignmentBucket.Evil));
    }
}
