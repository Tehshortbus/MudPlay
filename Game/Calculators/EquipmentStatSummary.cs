namespace FujinTerm.Game.Calculators;

// Aggregated equipment stat bonuses summed across every equipped item (and any
// race / class / quest ability bonuses folded in). Field-to-ability-ID mapping
// is documented inline; CharacterCalculator.MapAbilityToStat owns the dispatch.
// This is a mutable accumulator — built up by the aggregator, then read by the
// Workshop's Equipment Bonuses panel and the combat formulas.
public sealed class EquipmentStatSummary
{
    // AC / DR (item base ArmourClass + Abil 2 + Abil 10 [Blur]; item base DamageResist + Abil 7).
    public double PlusAC { get; set; }
    public double PlusDR { get; set; }

    // Core attribute bonuses.
    public int PlusStrength { get; set; }        // Abil 46
    public int PlusIntellect { get; set; }       // Abil 44
    public int PlusWillpower { get; set; }       // Abil 45
    public int PlusAgility { get; set; }         // Abil 48
    public int PlusHealth { get; set; }          // Abil 47
    public int PlusCharm { get; set; }           // Abil 49

    // HP / Mana.
    public int PlusMaxHp { get; set; }           // Abil 88
    public int PlusMaxMana { get; set; }         // Abil 69
    public int HpRegenPercent { get; set; }      // Abil 123
    public int MpRegenPercent { get; set; }      // Abil 145

    // Combat offense.
    public int PlusCrits { get; set; }           // Abil 58
    public int PlusAccuracy { get; set; }        // Abil 22 + 105 + 106 (all sum)
    public int PlusMinDamage { get; set; }       // Abil 1 (flat "Damage" add, the low-end bonus)
    public int PlusMaxDamage { get; set; }       // Abil 4
    public int SpellDamageBonus { get; set; }    // Abil 165

    // Combat defense.
    public int PlusDodge { get; set; }           // Abil 34
    public int PlusMagicResist { get; set; }     // Abil 36

    // Backstab.
    public int PlusBSAccuracy { get; set; }      // Abil 116
    public int PlusBSMin { get; set; }           // Abil 117
    public int PlusBSMax { get; set; }           // Abil 118

    // Skills.
    public int PlusStealth { get; set; }         // Abil 27
    public int PlusPerception { get; set; }      // Abil 77
    public int PlusSpellcasting { get; set; }    // Abil 70
    public int PlusEncumbrance { get; set; }     // Abil 96
    public int PlusTraps { get; set; }           // Abil 40 + 179 (sum)
    public int PlusPicklocks { get; set; }       // Abil 37 + 180 (sum)
    public int PlusIlluminate { get; set; }      // Abil 13 + 14 (sum)
    public int PlusQuickness { get; set; }       // Abil 67
    public int PlusHitMagic { get; set; }        // Abil 28 + 142 (sum from ALL equipped items)
    public int WeaponHitMagic { get; set; }      // Abil 28 + 142 from Weapon Hand only

    // Resistances.
    public int PlusColdResist { get; set; }      // Abil 3
    public int PlusFireResist { get; set; }      // Abil 5
    public int PlusStoneResist { get; set; }     // Abil 65
    public int PlusLightningResist { get; set; } // Abil 66
    public int PlusWaterResist { get; set; }     // Abil 147
    public int PlusShadowResist { get; set; }    // Abil 9

    // Protection.
    public int PlusProtEvil { get; set; }        // Abil 24
    public int PlusProtGood { get; set; }        // Abil 25

    // Weapon data (from item base fields, not abilities).
    public int WeaponHandAccy { get; set; }      // Accy field from Weapon Hand item
    public int OffHandAccy { get; set; }         // Accy field from Off-Hand item
    public int TotalWornAccy { get; set; }       // Sum of Accy fields from ALL equipped items
    public int WeaponStrReq { get; set; }        // StrReq from Weapon Hand item
    public int WeaponMin { get; set; }           // Min damage from Weapon Hand item (0 = unarmed)
    public int WeaponMax { get; set; }           // Max damage from Weapon Hand item (0 = unarmed)
    public int WeaponType { get; set; }          // WeaponType from Weapon Hand item
    public int WeaponSpeed { get; set; }         // Speed field from Weapon Hand item (drives swings/round; 0 = unarmed)
    public int MaxSingleAbil22 { get; set; }     // Highest single abil 22/105/106 value across all sources (Stock accuracy)

    // Martial arts (Mystic).
    public int PlusPunchDmg { get; set; }        // Abil 92
    public int PlusPunchAccy { get; set; }       // Abil 89
    public int PlusKickDmg { get; set; }         // Abil 93
    public int PlusKickAccy { get; set; }        // Abil 90
    public int PlusJumpKickDmg { get; set; }     // Abil 94
    public int PlusJumpKickAccy { get; set; }    // Abil 91
}
