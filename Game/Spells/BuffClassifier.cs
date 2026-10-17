using MudPlay.Game.Calculators;

namespace MudPlay.Game.Spells;

// Classifies a spell as a party buff for the Party-window picker and the casting
// path. The primary signal is a ZERO energy cost (a buff, not an attack — attack
// spells cost 500-1000 energy); the Targets scope then splits whole-party from
// single-target. Confirmed against game data across stock + Paradigm sets:
//   Targets 2       = single-target beneficial buff, cast on one other member
//                     (frenzy, divine favour, blood ritual, regeneration…)
//   Targets 10 / 13 = whole-party buff, one cast blankets the party
//                     (chant, mass frenzy, unholy fanaticism, rejuvenating field…)
// Self-only (0 / 1), enemy (4 / 8 / 9 / 12), item (7), and generic-area scopes
// are excluded — they aren't beneficial party buffs.
public static class BuffClassifier
{
    // Whole-party scope codes (one cast, no target). Matches AppServices.IsPartyWideBuff.
    public static bool IsWholeParty(int targets) => targets is 10 or 13;

    // A beneficial single-target buff cast on another player (or, for the unified
    // list, castable on ourselves too).
    public static bool IsSingleTargetBuff(int targets) => targets is 2;

    // A self-only beneficial buff (bless, troll skin, and kin) — only castable on us.
    public static bool IsSelfBuff(int targets) => targets is 0 or 1;

    // A hostile scope: cast at an enemy, one (4 Monster, 8 Monster or User) or the
    // room's (9 Divided Attack Area, 12 Full Attack Area). Every learnable curse,
    // hold, blind, confusion and attack in both sets carries one of these.
    public static bool IsEnemyScope(int targets) => targets is 4 or 8 or 9 or 12;

    // A spell actually persists — has a duration, base or level-scaled — rather
    // than firing once. An instant heal, cure, or utility spell (identify, and
    // the like) leaves all three duration columns 0. Zero-energy,
    // self/single-target scope alone isn't enough to call something a "buff" —
    // cure poison and minor healing share that exact shape and are not buffs;
    // only something with a real timer to maintain belongs in the Buff Watchdog.
    public static bool HasDuration(in SpellFormulaInput formula) =>
        formula.Dur > 0 || (formula.DurInc != 0 && formula.DurIncLVLs > 0);

    // Abil codes 97/98/112 gate a spell to ONLY good/evil/neutral respectively;
    // 110/111/113 gate it to NOT good/evil/neutral (KnownSpellCatalog.IsUsable
    // applies the same six codes against its own raw-row alignment check —
    // duplicated here rather than shared because that path reads straight off the
    // Spells table's row, before a SpellFormulaInput exists to hand it). Unknown
    // alignment (no `who` observation of ourselves yet) never
    // excludes — the caller doesn't get to guess wrong, it just doesn't filter.
    // evilPoints, when known, adds evil-only's value gate (Outlaw at least).
    public static bool IsAlignmentEligible(in SpellFormulaInput formula, AlignmentBucket? alignment,
        EvilPointRange? evilPoints = null)
    {
        if (alignment is not { } a) return true;
        foreach (SpellAbility ability in formula.Abilities)
        {
            switch (ability.Code)
            {
                case 97: if (a != AlignmentBucket.Good) return false; break;
                case 98:
                    if (a != AlignmentBucket.Evil || evilPoints?.MeetsEvilOnly(ability.Value) == false) return false;
                    break;
                case 112: if (a != AlignmentBucket.Neutral) return false; break;
                case 110: if (a == AlignmentBucket.Good) return false; break;
                case 111: if (a == AlignmentBucket.Evil) return false; break;
                case 113: if (a == AlignmentBucket.Neutral) return false; break;
            }
        }
        return true;
    }

    // True when the spell belongs in the UNIFIED buff picker: a zero-energy,
    // maintained buff we can keep up on ourselves, a member, or the whole party
    // (self / single-target / whole-party scopes). Attacks (energy > 0), enemy /
    // area / item-target scopes, and instant effects with nothing to maintain
    // (heals, cures, utility spells) are excluded.
    public static bool IsAnyBuff(in KnownSpell spell) =>
        spell.Formula.EnergyCost == 0 && HasDuration(spell.Formula)
        && (IsSelfBuff(spell.Targets) || IsSingleTargetBuff(spell.Targets) || IsWholeParty(spell.Targets));
}
