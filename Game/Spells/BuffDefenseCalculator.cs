using System;
using System.Collections.Generic;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Spells;

// The AC + DR the character's CONFIGURED buffs grant, assuming they're up. Reads
// the unified buff plan (CharacterProfile.PartyBuffs — the same list the Buff
// Watchdog edits), resolves each self-applicable slot's spell against the class
// catalog, and sums its AC (abil 2, plus AC-Blur abil 10 — folded into AC the
// same way worn gear does) and DR (abil 7, stored at 10x the applied value).
//
// Used wherever the character's defense is shown "as if buffed" — the Monster
// Intel matchup, the Equipment Manager, and Character Info — so a buff you've
// configured but that isn't currently up still counts toward the projection.
public static class BuffDefenseCalculator
{
    private const int AcCode = 2, AcBlurCode = 10, DrCode = 7,
                      ProtEvilCode = 24, ShadowCode = 9, VileWardCode = 1113;

    public static BuffDefense Compute(BuffSettings? buffs, int level, IReadOnlyList<KnownSpell>? available)
    {
        if (buffs is null || available is null || buffs.Slots.Count == 0) return default;

        // Cast-code → spell; the class list is already distinct by code, first wins.
        Dictionary<string, KnownSpell> byCode = new(StringComparer.OrdinalIgnoreCase);
        foreach (KnownSpell s in available) byCode.TryAdd(s.Short, s);

        int ac = 0, protEvil = 0;
        double dr = 0;
        bool hasShadow = false, hasVileWard = false;
        int cold = 0, fire = 0, stone = 0, lightning = 0, water = 0, poison = 0;
        foreach (BuffSlot slot in buffs.Slots)
        {
            string? code = slot.Spell?.Trim();
            if (string.IsNullOrEmpty(code) || code.StartsWith('#')) continue;  // unconfigured / #item-cast token
            if (!byCode.TryGetValue(code, out KnownSpell spell)) continue;     // not a spell this class casts
            if (!LandsOnSelf(spell.Targets, slot)) continue;

            (long _, long affMax) = SpellCalculator.AffectMagnitude(spell.Formula, level);
            foreach (SpellAbility a in spell.Formula.Abilities)
            {
                // A stored AbilVal is the flat granted value; a 0 AbilVal means the
                // magnitude comes from the spell's level-scaled range — take its max,
                // the buff's full value while it's up. Mirrors SpellEffectFormatter.
                switch (a.Code)
                {
                    case AcCode or AcBlurCode: ac += a.Value != 0 ? a.Value : (int)affMax; break;
                    case DrCode: dr += (a.Value != 0 ? a.Value : affMax) / 10.0; break;   // DR is stored at 10x
                    case ProtEvilCode: protEvil += a.Value != 0 ? a.Value : (int)affMax; break;
                    case ShadowCode: hasShadow = true; break;
                    case VileWardCode: hasVileWard = true; break;
                    case 3: cold += a.Value != 0 ? a.Value : (int)affMax; break;
                    case 5: fire += a.Value != 0 ? a.Value : (int)affMax; break;
                    case 65: stone += a.Value != 0 ? a.Value : (int)affMax; break;
                    case 66: lightning += a.Value != 0 ? a.Value : (int)affMax; break;
                    case 147: water += a.Value != 0 ? a.Value : (int)affMax; break;
                    case 21: poison += a.Value != 0 ? a.Value : (int)affMax; break;
                }
            }
        }
        return new BuffDefense(ac, dr, protEvil, hasShadow, hasVileWard,
            new ElementalResists(cold, fire, stone, lightning, water, poison));
    }

    // A configured slot lands on us (so its AC/DR counts toward our buffed defense)
    // when the spell is self-only (Targets 1), a whole-party spell we keep on
    // (10/13 + WholePartyOn), or a single-target spell we cast on ourselves
    // (Targets 2 + CastOnSelf). A Targets 0 spell goes only to other players.
    private static bool LandsOnSelf(int targets, BuffSlot slot) => targets switch
    {
        0 => false,
        1 => true,
        10 or 13 => slot.WholePartyOn,
        _ => slot.CastOnSelf,
    };
}
