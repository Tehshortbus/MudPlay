using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// The attack bonuses from buffs actually being cast on us: a slot in the list that
// isn't cast on us doesn't count (user, 2026-09-30).
public sealed class BuffCombatCalculatorTests
{
    private static KnownSpell Spell(string code, int targets, params SpellAbility[] abilities)
        => new(Number: 0, Short: code, Name: code, Magery: 0, MageryLvl: 0, ReqLevel: 1,
               Targets: targets,
               Formula: new SpellFormulaInput { ReqLevel = 1, Cap = 50, Abilities = abilities });

    private static readonly List<KnownSpell> Available = new()
    {
        Spell("smit", targets: 1, new SpellAbility(4, 2)),                               // +max damage
        Spell("shad", targets: 1, new SpellAbility(116, 5), new SpellAbility(117, 9), new SpellAbility(118, 11)),
        Spell("bles", targets: 10, new SpellAbility(22, 4)),                             // party accuracy
    };

    [Fact]
    public void EnabledSelfBuffs_Count_AndEachSourceIsListed()
    {
        BuffSettings buffs = new()
        {
            Slots =
            {
                new BuffSlot { Spell = "smit", CastOnSelf = true },
                new BuffSlot { Spell = "shad", CastOnSelf = true },
            },
        };
        BuffCombat b = BuffCombatCalculator.Compute(buffs, level: 13, Available, inParty: false);

        Assert.Equal(2, b.MaxDamage);
        Assert.Equal(5, b.BsAccuracy);
        Assert.Equal(9, b.BsMin);
        Assert.Equal(11, b.BsMax);
        Assert.Contains(b.Sources, s => s.Spell == "smit" && s.What == "max damage" && s.Value == 2);
    }

    [Fact]
    public void ConfiguredButNotCastOnUs_DoesNotCount()
    {
        BuffSettings buffs = new()
        {
            Slots =
            {
                new BuffSlot { Spell = "smit", CastOnSelf = false },
                new BuffSlot { Spell = "bles", WholePartyOn = true, CastSolo = false },   // solo: not cast
            },
        };
        BuffCombat b = BuffCombatCalculator.Compute(buffs, level: 13, Available, inParty: false);

        Assert.Equal(0, b.MaxDamage);
        Assert.Equal(0, b.Accuracy);
        Assert.Empty(b.Sources);
    }

    [Fact]
    public void WholePartyBuff_CountsInAParty_EvenWithCastSoloOff()
    {
        BuffSettings buffs = new() { Slots = { new BuffSlot { Spell = "bles", WholePartyOn = true, CastSolo = false } } };
        BuffCombat b = BuffCombatCalculator.Compute(buffs, level: 13, Available, inParty: true);

        Assert.Equal(4, b.Accuracy);
        Assert.Equal(4, b.AccuracyMaxSingle);
    }

    [Fact]
    public void ASpellInTwoSlots_CountsOnce()
    {
        BuffSettings buffs = new()
        {
            Slots =
            {
                new BuffSlot { Spell = "smit", CastOnSelf = true },
                new BuffSlot { Spell = "smit", CastOnSelf = true },
            },
        };
        Assert.Equal(2, BuffCombatCalculator.Compute(buffs, level: 13, Available, inParty: false).MaxDamage);
    }
}
