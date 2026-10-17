using MudPlay.Game;
using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

// Pins the realm damage/resist math (Stock and Paradigm): level scaling of the range,
// the magic-resist partial cut (code 17 "Damage(-MR)" only), the elemental
// flat-percent cut, and the low-MR amplification.
public sealed class SpellDamageCalculatorTests
{
    // A code-17 cold spell: 12–21 base, no scaling, AttType 0 (cold).
    private static SpellFormulaInput ColdBolt(int damageCode = 17) => new()
    {
        Number = 5, MinBase = 12, MaxBase = 21, ReqLevel = 4, Cap = 18, AttType = 0,
        Abilities = [new SpellAbility(damageCode, 0)],
    };

    [Fact]
    public void Compute_Unresisted_IsTheScaledRange()
    {
        SpellFormulaInput f = new()
        {
            Number = 6, MinBase = 10, MaxBase = 20, MinInc = 1, MinIncLVLs = 1,
            MaxInc = 2, MaxIncLVLs = 1, ReqLevel = 5, Cap = 10, AttType = 4,
            Abilities = [new SpellAbility(1, 0)],
        };
        Assert.Equal((15L, 30L), SpellDamageCalculator.Compute(f, 5));    // 10+5, 20+10
        Assert.Equal((20L, 40L), SpellDamageCalculator.Compute(f, 10));   // at cap
    }

    [Fact]
    public void Compute_Code17_MagicResistPartialCut()
    {
        // MR 100 > 50: cut = (100-50)/2 = 25%. 12*.75=9, 21*.75=15.75→16.
        Assert.Equal((9L, 16L), SpellDamageCalculator.Compute(ColdBolt(), level: 4, magicResist: 100));
    }

    [Fact]
    public void Compute_Code1_NotReducedByMagicResist()
    {
        // Code 1 "Damage" is NOT the MR-bearing variant — MR leaves it untouched.
        Assert.Equal((12L, 21L), SpellDamageCalculator.Compute(ColdBolt(damageCode: 1), level: 4, magicResist: 100));
    }

    [Fact]
    public void Compute_LowMagicResist_AmplifiesDamage()
    {
        // MR 30 < 50 amplifies: dmg + dmg*(50-30)/100 = dmg*1.2. 12→14.4→14, 21→25.2→25.
        Assert.Equal((14L, 25L), SpellDamageCalculator.Compute(ColdBolt(), level: 4, magicResist: 30));
    }

    [Fact]
    public void Compute_ElementalResist_FlatPercentCut()
    {
        // 50% cold resist: 12→6, 21→10.5→10 (banker's rounding).
        Assert.Equal((6L, 10L), SpellDamageCalculator.Compute(ColdBolt(), level: 4, elementalResist: 50));
    }

    [Fact]
    public void Compute_NegativeElementalResist_AmplifiesDamage()
    {
        // -50% cold resist = vulnerability: 12→18, 21→31.5 — Stock truncates to 31,
        // Paradigm rounds to 32.
        Assert.Equal((18L, 31L), SpellDamageCalculator.Compute(ColdBolt(), level: 4, elementalResist: -50));
        Assert.Equal((18L, 32L), SpellDamageCalculator.Compute(ColdBolt(), level: 4, elementalResist: -50,
            realm: RealmType.ParaMud));
    }

    // The caster's spell-damage bonus lifts Damage (1) and Damage(-MR) (17) on both
    // realms; drain (8) only on Paradigm.
    [Fact]
    public void Compute_SpellDamageBonus_ByCodeAndRealm()
    {
        Assert.Equal((14L, 25L), SpellDamageCalculator.Compute(ColdBolt(1), 4, spellDamageBonus: 20));
        Assert.Equal((14L, 25L), SpellDamageCalculator.Compute(ColdBolt(17), 4, spellDamageBonus: 20));
        Assert.Equal((12L, 21L), SpellDamageCalculator.Compute(ColdBolt(8), 4, spellDamageBonus: 20));
        Assert.Equal((14L, 25L), SpellDamageCalculator.Compute(ColdBolt(8), 4, spellDamageBonus: 20,
            realm: RealmType.ParaMud));
    }

    // Stock's order: elemental cut, then the bonus, then the MR cut, truncating each
    // step. Max 21: ×50/100 = 10 → ×120/100 = 12 → MR 100 cuts 25% → 9.
    [Fact]
    public void Compute_Stock_ElementalThenBonusThenMagicResist()
    {
        Assert.Equal(9L, SpellDamageCalculator.Compute(ColdBolt(17), 4, magicResist: 100, elementalResist: 50,
            spellDamageBonus: 20).Max);
    }

    // Stock lowers a min above the max to the max.
    [Fact]
    public void Compute_Stock_MinAboveMax_LoweredToMax()
    {
        SpellFormulaInput odd = new()
        {
            Number = 8, MinBase = 30, MaxBase = 20, ReqLevel = 1, AttType = 4,
            Abilities = [new SpellAbility(1, 0)],
        };
        Assert.Equal((20L, 20L), SpellDamageCalculator.Compute(odd, 1));
    }

    [Fact]
    public void Compute_NormalSpell_IgnoresElementalResist()
    {
        // AttType 4 (Normal) has no element — an elemental resist can't touch it.
        SpellFormulaInput normal = new()
        {
            Number = 7, MinBase = 10, MaxBase = 10, ReqLevel = 1, AttType = 4,
            Abilities = [new SpellAbility(17, 0)],
        };
        Assert.Equal((10L, 10L), SpellDamageCalculator.Compute(normal, 1, elementalResist: 80));
    }

    [Fact]
    public void UsesMagicResist_Code17NotNonMagical()
    {
        Assert.True(SpellDamageCalculator.UsesMagicResist(ColdBolt()));
        Assert.False(SpellDamageCalculator.UsesMagicResist(ColdBolt(damageCode: 1)));

        SpellFormulaInput nonMagical = new()
        {
            Number = 8, Abilities = [new SpellAbility(17, 0), new SpellAbility(144, 0)],
        };
        Assert.False(SpellDamageCalculator.UsesMagicResist(nonMagical));
    }

    [Theory]
    [InlineData(0, SpellDamageElement.Cold)]
    [InlineData(1, SpellDamageElement.Fire)]
    [InlineData(2, SpellDamageElement.Stone)]
    [InlineData(3, SpellDamageElement.Lightning)]
    [InlineData(4, SpellDamageElement.None)]
    [InlineData(5, SpellDamageElement.Water)]
    [InlineData(6, SpellDamageElement.Poison)]
    public void Element_MapsAttType(int attType, SpellDamageElement expected)
    {
        SpellFormulaInput f = new() { Number = 9, AttType = attType };
        Assert.Equal(expected, SpellDamageCalculator.Element(f));
    }

    [Fact]
    public void IsDamageSpell_TrueForDamageCodes()
    {
        Assert.True(SpellDamageCalculator.IsDamageSpell(ColdBolt()));
        SpellFormulaInput buff = new() { Number = 10, Abilities = [new SpellAbility(2, 10)] };
        Assert.False(SpellDamageCalculator.IsDamageSpell(buff));
    }

    // A player's own magic resist against a monster's Damage(-MR) spell: the cut is
    // (MR-50)/2 capped at 50, MR/2 capped at 75 for an AntiMagic class, and a hit is
    // amplified below MR 50.
    [Theory]
    [InlineData(55, false, -2)]
    [InlineData(50, false, 0)]
    [InlineData(30, false, 20)]
    [InlineData(200, false, -50)]
    [InlineData(100, true, -50)]
    [InlineData(200, true, -75)]
    public void PlayerMagicResistDamagePercent_BothRealms(int mr, bool antimagic, double expected)
    {
        Assert.Equal(expected, SpellDamageCalculator.PlayerMagicResistDamagePercent(mr, antimagic, RealmType.Stock));
        Assert.Equal(expected, SpellDamageCalculator.PlayerMagicResistDamagePercent(mr, antimagic, RealmType.ParaMud));
    }

    // A player's elemental resist against a monster's spell: Stock cuts every damage
    // type (poison by ImmuPoison, vulnerabilities included); Paradigm cuts only the
    // five elements, and only by a positive resist.
    [Fact]
    public void PlayerElementalResist_FollowsTheRealm()
    {
        ElementalResists r = new(Cold: -20, Fire: 25, Stone: 0, Lightning: 0, Water: 0, Poison: 50);

        Assert.Equal(25, SpellDamageCalculator.PlayerElementalResist(r, SpellDamageElement.Fire, RealmType.Stock));
        Assert.Equal(-20, SpellDamageCalculator.PlayerElementalResist(r, SpellDamageElement.Cold, RealmType.Stock));
        Assert.Equal(50, SpellDamageCalculator.PlayerElementalResist(r, SpellDamageElement.Poison, RealmType.Stock));
        Assert.Equal(0, SpellDamageCalculator.PlayerElementalResist(r, SpellDamageElement.None, RealmType.Stock));

        Assert.Equal(25, SpellDamageCalculator.PlayerElementalResist(r, SpellDamageElement.Fire, RealmType.ParaMud));
        Assert.Equal(0, SpellDamageCalculator.PlayerElementalResist(r, SpellDamageElement.Cold, RealmType.ParaMud));
        Assert.Equal(0, SpellDamageCalculator.PlayerElementalResist(r, SpellDamageElement.Poison, RealmType.ParaMud));
    }

    // An outright resist is a roll at or under M.R./2. Stock's roll is 1–99, so the
    // figure is out of 99 there; a player's M.R./2 stops at 97 on Stock, a monster's
    // at 98.
    [Fact]
    public void FullResist_IsOutOf99OnStock()
    {
        Assert.Equal(27 * 100.0 / 99, SpellDamageCalculator.FullResistChance(2, 55, false, RealmType.Stock), 6);
        Assert.Equal(27, SpellDamageCalculator.FullResistChance(2, 55, false, RealmType.ParaMud), 6);
        Assert.Equal(98 * 100.0 / 99, SpellDamageCalculator.FullResistChance(2, 500, false, RealmType.Stock), 6);
        Assert.Equal(0, SpellDamageCalculator.FullResistChance(0, 500, false, RealmType.Stock), 6);
        Assert.Equal(0, SpellDamageCalculator.FullResistChance(1, 500, false, RealmType.Stock), 6);   // needs AntiMagic

        Assert.Equal(27 * 100.0 / 99, SpellDamageCalculator.PlayerFullResistChance(55, RealmType.Stock), 6);
        Assert.Equal(97 * 100.0 / 99, SpellDamageCalculator.PlayerFullResistChance(500, RealmType.Stock), 6);
        Assert.Equal(98, SpellDamageCalculator.PlayerFullResistChance(500, RealmType.ParaMud), 6);
    }
}
