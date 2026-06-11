using FujinTerm.Game.Spells;
using FujinTerm.ViewModels;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the level-scaled display formatting on <see cref="SpellBookRowViewModel"/>
/// — effect string (damage / heal / duration), mana, formula tooltip, and the
/// obtained glyph — independent of game-data loading. Magnitudes come from the
/// ported <see cref="SpellCalculator"/>; these tests assert the row's string
/// shaping, not the math (which <c>SpellCalculatorTests</c> owns).
/// </summary>
public sealed class SpellBookRowViewModelTests
{
    private static SpellFormulaInput Damage(int minBase, int maxBase) => new()
    {
        Number = 1,
        MinBase = minBase,
        MaxBase = maxBase,
        Abilities = [new SpellAbility(1, 0)], // code 1 = direct damage
    };

    private static SpellFormulaInput Heal(int minBase, int maxBase) => new()
    {
        Number = 2,
        MinBase = minBase,
        MaxBase = maxBase,
        Abilities = [new SpellAbility(18, 0)], // code 18 = heal
    };

    private static SpellFormulaInput Buff(int dur) => new()
    {
        Number = 3,
        Dur = dur,
        Abilities = [], // no damage/heal magnitude — duration only
    };

    private static KnownSpell Spell(string shortCode, string name, int reqLevel, SpellFormulaInput formula)
        => new(formula.Number, shortCode, name, Magery: 1, MageryLvl: 1, reqLevel, formula);

    private static SpellFormulaInput? NoChain(int _) => null;

    [Fact]
    public void DamageRange_RendersAsDmg()
    {
        KnownSpell s = Spell("star", "starlight", 2, Damage(minBase: 6, maxBase: 10));
        SpellBookRowViewModel row = new(s, isObtained: true, level: 5, NoChain);

        Assert.Equal("Dmg 6–10", row.EffectText);
        Assert.Equal("star", row.Short);
        Assert.Equal("starlight", row.Name);
        Assert.Equal("2", row.ReqLevelText);
        Assert.Equal("✓", row.ObtainedGlyph);
    }

    [Fact]
    public void EqualMinMax_RendersSingleValue()
    {
        KnownSpell s = Spell("bolt", "bolt", 1, Damage(minBase: 8, maxBase: 8));
        SpellBookRowViewModel row = new(s, isObtained: false, level: 5, NoChain);

        Assert.Equal("Dmg 8", row.EffectText);
        Assert.Equal(string.Empty, row.ObtainedGlyph);
    }

    [Fact]
    public void HealRange_RendersAsHeal()
    {
        KnownSpell s = Spell("heal", "minor heal", 3, Heal(minBase: 20, maxBase: 30));
        SpellBookRowViewModel row = new(s, isObtained: true, level: 7, NoChain);

        Assert.Equal("Heal 20–30", row.EffectText);
    }

    [Fact]
    public void DurationOnly_RendersSeconds()
    {
        // Durations are stored in 3-second spell-round ticks: 8 × 3 = 24s.
        KnownSpell s = Spell("bless", "bless", 4, Buff(dur: 8));
        SpellBookRowViewModel row = new(s, isObtained: false, level: 10, NoChain);

        Assert.Equal("24 seconds", row.EffectText);
    }

    [Fact]
    public void NoMagnitude_RendersDash()
    {
        // No damage/heal ability and no duration → nothing to show.
        SpellFormulaInput empty = new() { Number = 9, Abilities = [] };
        KnownSpell s = Spell("misc", "misc", 1, empty);
        SpellBookRowViewModel row = new(s, isObtained: false, level: 5, NoChain);

        Assert.Equal("—", row.EffectText);
    }

    [Fact]
    public void StatAffect_RendersDecodedAbility()
    {
        // Pure buff: AC +10 (code 2). No damage / heal / duration figure, so
        // the decoded affect is the only thing the Effect column shows.
        SpellFormulaInput f = new()
        {
            Number = 5,
            Abilities = [new SpellAbility(2, 10)],
        };
        KnownSpell s = Spell("shld", "shield", 4, f);
        SpellBookRowViewModel row = new(s, isObtained: true, level: 6, NoChain);

        Assert.Equal("AC +10", row.EffectText);
    }

    [Fact]
    public void ZeroValueAffect_RendersLevelScaledMinMax()
    {
        // Stealth (code 27) with AbilVal 0 — the magnitude lives in the
        // spell's Min/Max base (MME PullSpellEQ appends sMin/sMax). Equal
        // min/max collapse to a single signed value.
        SpellFormulaInput f = new()
        {
            Number = 10,
            MinBase = 10,
            MaxBase = 10,
            ReqLevel = 4,
            Abilities = [new SpellAbility(27, 0)],
        };
        KnownSpell s = Spell("hide", "shadow cloak", 4, f);
        SpellBookRowViewModel row = new(s, isObtained: true, level: 6, NoChain);

        Assert.Equal("Stealth +10", row.EffectText);
    }

    [Fact]
    public void ZeroValueAffect_BelowReqLevel_ClampsToObtainLevel()
    {
        // MaxDamage (code 4) gains +1 every level past base 0. Required at
        // level 6; evaluated at level 1 it must clamp UP to level 6 (→ +6),
        // never the out-of-range level-1 figure.
        SpellFormulaInput f = new()
        {
            Number = 11,
            MaxBase = 0,
            MaxInc = 1,
            MaxIncLVLs = 1,
            ReqLevel = 6,
            Abilities = [new SpellAbility(4, 0)],
        };
        KnownSpell s = Spell("edge", "keen edge", 6, f);
        SpellBookRowViewModel row = new(s, isObtained: false, level: 1, NoChain);

        // Min base 0/no slope → 0; Max → 6. Range shows "0 to +6".
        Assert.Equal("MaxDamage 0 to +6", row.EffectText);
    }

    [Fact]
    public void DurationPlusAffect_RendersBoth()
    {
        // Timed buff that grants Strength +3 (code 46) for 8 rounds.
        SpellFormulaInput f = new()
        {
            Number = 6,
            Dur = 8,
            Abilities = [new SpellAbility(46, 3)],
        };
        KnownSpell s = Spell("migt", "might", 5, f);
        SpellBookRowViewModel row = new(s, isObtained: false, level: 10, NoChain);

        Assert.Equal("24 seconds · Strength +3", row.EffectText);
    }

    [Fact]
    public void RemovesSpell_RendersTargetByName()
    {
        // Abil 122 (RemovesSpell) → resolve the AbilVal to the removed
        // spell's name via the supplied resolver (MME's GetSpellName path).
        SpellFormulaInput f = new()
        {
            Number = 8,
            Abilities = [new SpellAbility(122, 42)],
        };
        KnownSpell s = Spell("dpel", "dispel", 6, f);
        SpellBookRowViewModel row = new(
            s, isObtained: true, level: 10, NoChain,
            resolveSpellName: n => n == 42 ? "blindness" : null);

        Assert.Equal("Removes blindness", row.EffectText);
    }

    [Fact]
    public void MessageOnlySlots_AreNotSurfaced()
    {
        // DescMsg (115) / StartMsg (120) / ShockMsg (137) are display-only
        // message slots MME hides — they must not appear as effect labels.
        SpellFormulaInput f = new()
        {
            Number = 9,
            MinBase = 6,
            MaxBase = 10,
            Abilities =
            [
                new SpellAbility(1, 0),
                new SpellAbility(115, 3),
                new SpellAbility(120, 4),
                new SpellAbility(137, 5),
            ],
        };
        KnownSpell s = Spell("bolt", "bolt", 3, f);
        SpellBookRowViewModel row = new(s, isObtained: false, level: 5, NoChain);

        Assert.Equal("Dmg 6–10", row.EffectText);
    }

    [Fact]
    public void TextBlock_RendersUnsignedRecordNumber()
    {
        // Abil 148 (TextBlock) carries a TextBlock *record number*, not a
        // magnitude — it must render "TextBlock 869", never "TextBlock +869"
        // (MME's NO-HEADER group: GetAbilityName(148) & " " & nValue).
        SpellFormulaInput f = new()
        {
            Number = 12,
            Abilities = [new SpellAbility(148, 869)],
        };
        KnownSpell s = Spell("wood", "wooden box gems", 5, f);
        SpellBookRowViewModel row = new(s, isObtained: true, level: 6, NoChain);

        Assert.Equal("TextBlock 869", row.EffectText);
    }

    [Fact]
    public void DamageAbility_NotDoublePrintedAsAffect()
    {
        // Code 1 (Damage) is folded into the Dmg figure; an accompanying
        // Slowness -5 (code 68) debuff still surfaces as an affect.
        SpellFormulaInput f = new()
        {
            Number = 7,
            MinBase = 6,
            MaxBase = 10,
            Abilities = [new SpellAbility(1, 0), new SpellAbility(68, -5)],
        };
        KnownSpell s = Spell("frst", "frost", 3, f);
        SpellBookRowViewModel row = new(s, isObtained: true, level: 5, NoChain);

        Assert.Equal("Dmg 6–10 · Slowness -5", row.EffectText);
    }

    [Fact]
    public void Formula_ShowsBaseAndSlope()
    {
        SpellFormulaInput f = new()
        {
            Number = 1,
            MinBase = 6,
            MaxBase = 10,
            MaxInc = 2,
            MaxIncLVLs = 1,
            Abilities = [new SpellAbility(1, 0)],
        };
        KnownSpell s = Spell("star", "starlight", 2, f);
        SpellBookRowViewModel row = new(s, isObtained: true, level: 5, NoChain);

        Assert.Contains("base 6–10", row.FormulaText);
        Assert.Contains("max +2/1lv", row.FormulaText);
    }
}
