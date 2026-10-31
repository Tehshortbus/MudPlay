using System;
using System.Collections.Generic;

namespace MudPlay.Game.Spells;

// What a set of the character's OWN buffs adds to their offense — Stealth,
// accuracy, the backstab bonuses, +max damage and crits — for the Monster Intel
// "Apply Buffs" what-if (e.g. a Gypsy's shadowform). The offense mirror of
// BuffDefenseCalculator, with the same rule for a 0 AbilVal: the magnitude comes
// from the spell's level-scaled affect range (shadowform at level 8 = 5 + 8/3 = 7,
// matching the live `stat` jump from 83 to 90 Stealth).
//
// Stealth shows in `stat` while the buff is up (user-confirmed), so the live
// Stealth already carries an active buff — counting it again would double it.
// Its Stealth is therefore folded only while it ISN'T up; the rest (accuracy,
// backstab bonuses, damage, crits) come from the gear aggregate, which never
// sees buffs, so they always fold.
//
// Ability 1 is deliberately absent: on a spell it's the damage marker, not the
// +min damage it means on gear.
public static class BuffOffenseCalculator
{
    private const int StealthCode = 27, MaxDamageCode = 4, CritsCode = 58,
                      BsAccuracyCode = 116, BsMinCode = 117, BsMaxCode = 118;

    // A buff worth listing in the picker: one that can land on ourselves and
    // moves at least one offense number.
    public static bool AffectsOffense(KnownSpell spell)
    {
        if (!BuffClassifier.CanLandOnSelf(spell.Targets)) return false;
        foreach (SpellAbility a in spell.Formula.Abilities)
            if (IsOffenseCode(a.Code)) return true;
        return false;
    }

    private static bool IsOffenseCode(int code) =>
        code is StealthCode or MaxDamageCode or CritsCode
             or BsAccuracyCode or BsMinCode or BsMaxCode
             or 22 or 105 or 106;

    // Sum the selected buffs. isUp says whether a buff (by spell name) is on the
    // character right now, so its Stealth is already in the live stats.
    //
    // lowest: each level-scaled value at the bottom of its roll instead of the top.
    // The game rolls a buff's value as it's cast, so the two folds bracket what a
    // cast can give, and only the lowest is what a cast is sure to give.
    public static BuffOffense Fold(IReadOnlyList<KnownSpell>? buffs, int level, Func<string, bool>? isUp = null,
        bool lowest = false)
    {
        if (buffs is null || buffs.Count == 0) return default;

        int stealth = 0, acc = 0, bsAcc = 0, bsMin = 0, bsMax = 0, maxDmg = 0, crits = 0;
        foreach (KnownSpell spell in buffs)
        {
            bool up = isUp?.Invoke(spell.Name) ?? false;
            (long affMin, long affMax) = SpellCalculator.AffectMagnitude(spell.Formula, level);
            foreach (SpellAbility a in spell.Formula.Abilities)
            {
                int mag = a.Value != 0 ? a.Value : (int)(lowest ? affMin : affMax);
                switch (a.Code)
                {
                    case StealthCode: if (!up) stealth += mag; break;
                    case 22 or 105 or 106: acc += mag; break;
                    case BsAccuracyCode: bsAcc += mag; break;
                    case BsMinCode: bsMin += mag; break;
                    case BsMaxCode: bsMax += mag; break;
                    case MaxDamageCode: maxDmg += mag; break;
                    case CritsCode: crits += mag; break;
                }
            }
        }
        return new BuffOffense(stealth, acc, bsAcc, bsMin, bsMax, maxDmg, crits);
    }
}

// The offense a folded buff set adds — each a plain amount added onto the
// character's own. Default is the no-buff identity.
public readonly record struct BuffOffense(
    int Stealth, int Accuracy, int BsAccuracy, int BsMin, int BsMax, int MaxDamage, int Crits)
{
    public bool Any => this != default;
}
