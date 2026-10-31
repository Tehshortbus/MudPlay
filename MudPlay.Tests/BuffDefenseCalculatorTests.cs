using System.Collections.Generic;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

public sealed class BuffDefenseCalculatorTests
{
    private static KnownSpell Spell(string code, int targets, params SpellAbility[] abilities)
        => new(Number: 0, Short: code, Name: code, Magery: 0, MageryLvl: 0, ReqLevel: 1,
               Targets: targets,
               Formula: new SpellFormulaInput { ReqLevel = 1, Cap = 50, Abilities = abilities });

    [Fact]
    public void Compute_SumsSelfApplicableAcAndDr_SkipsNonSelfTargets()
    {
        List<KnownSpell> available = new()
        {
            Spell("shld", targets: 1, new SpellAbility(2, 15)),   // self-only AC +15
            Spell("gbls", targets: 10, new SpellAbility(7, 20)),  // whole-party DR +2.0 (stored 20)
            Spell("sbls", targets: 2, new SpellAbility(2, 8)),    // single-target AC — only if cast on self
        };
        BuffSettings buffs = new()
        {
            Slots =
            {
                new BuffSlot { Spell = "shld", CastOnSelf = true },
                new BuffSlot { Spell = "gbls", WholePartyOn = true },
                new BuffSlot { Spell = "sbls", CastOnSelf = false, AllMembers = true }, // NOT on us
            },
        };

        BuffDefense d = BuffDefenseCalculator.Compute(buffs, level: 20, available);
        Assert.Equal(15, d.Ac);    // shld only; sbls excluded (single-target, not on self)
        Assert.Equal(2.0, d.Dr);   // gbls DR 20 / 10
    }

    [Fact]
    public void Compute_SingleTargetCastOnSelf_Counts()
    {
        List<KnownSpell> available = new() { Spell("sbls", targets: 2, new SpellAbility(2, 8)) };
        BuffSettings buffs = new() { Slots = { new BuffSlot { Spell = "sbls", CastOnSelf = true } } };
        Assert.Equal(8, BuffDefenseCalculator.Compute(buffs, level: 20, available).Ac);
    }

    [Fact]
    public void Compute_FoldsAcBlur_ProtEvil_Shadow_VileWard()
    {
        List<KnownSpell> available = new()
        {
            Spell("blur", targets: 1, new SpellAbility(10, 5)),  // AC-Blur folds into AC
            Spell("prot", targets: 1, new SpellAbility(24, 12)), // Prot-Evil ward
            Spell("shad", targets: 1, new SpellAbility(9, 1)),   // shadow property
            Spell("vile", targets: 1, new SpellAbility(1113, 1)),// vile ward
        };
        BuffSettings buffs = new()
        {
            Slots =
            {
                new BuffSlot { Spell = "blur", CastOnSelf = true },
                new BuffSlot { Spell = "prot", CastOnSelf = true },
                new BuffSlot { Spell = "shad", CastOnSelf = true },
                new BuffSlot { Spell = "vile", CastOnSelf = true },
            },
        };

        BuffDefense d = BuffDefenseCalculator.Compute(buffs, level: 20, available);
        Assert.Equal(5, d.Ac);        // AC-Blur folded into AC
        Assert.Equal(12, d.ProtEvil);
        Assert.True(d.HasShadow);
        Assert.True(d.HasVileWard);
    }

    [Fact]
    public void Compute_ItemTokenAndUnknownCode_Skipped()
    {
        List<KnownSpell> available = new() { Spell("shld", targets: 1, new SpellAbility(2, 15)) };
        BuffSettings buffs = new()
        {
            Slots =
            {
                new BuffSlot { Spell = "#waterskin", CastOnSelf = true }, // #item token
                new BuffSlot { Spell = "xxxx", CastOnSelf = true },       // not a class spell
            },
        };
        Assert.False(BuffDefenseCalculator.Compute(buffs, level: 20, available).Any);
    }
}
