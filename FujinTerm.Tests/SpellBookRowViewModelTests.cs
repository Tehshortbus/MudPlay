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
        Assert.Equal("Lv 2", row.ReqLevelText);
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
    public void DurationOnly_RendersAsDur()
    {
        KnownSpell s = Spell("bless", "bless", 4, Buff(dur: 8));
        SpellBookRowViewModel row = new(s, isObtained: false, level: 10, NoChain);

        Assert.Equal("Dur 8", row.EffectText);
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
