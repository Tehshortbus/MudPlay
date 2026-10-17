namespace MudPlay.Game.Calculators;

// Composes a both-directions combat preview between the player and one monster,
// on top of CombatCalculator.CalculateHitChance:
//   Player → monster — normal-attack hit chance vs the monster's AC and dodge
//     (some monsters carry a Dodge ability; they have no alignment ward),
//     per-hit damage after the monster's damage-resist, then DPS
//     (hit% * dmg/hit * swings) and rounds-to-kill.
//   Monster → player — the monster's primary physical attack's per-hit damage
//     after our damage-resist and its swings per round. Physical attacks reduce
//     by DR; elemental / magic resist doesn't apply to the physical melee slot.
//     Its chance to hit us is IncomingHitEstimator's, across every attack.
// Pure math — the caller extracts the monster row and current player profile
// and passes typed values in.
public static class MonsterMatchupCalculator
{
    // Run the matchup for the supplied player and monster profiles.
    public static MonsterMatchupResult Compute(PlayerMatchupProfile player, MonsterMatchupProfile monster)
    {
        RealmType realm = player.Realm;

        // Player → monster. Some monsters carry a Dodge ability (e.g. Lord of the
        // Hunt); it raises their effective defence exactly like the player's dodge
        // does on the return direction. Monsters have no prot wards, and no class
        // armour type, so the realm's default floor applies (GAME_MECHANICS "To-hit
        // floor").
        HitCalcResult playerHit = CombatCalculator.CalculateHitChance(
            attackerAccuracy: player.NormalAccuracy,
            defenderAC: monster.ArmourClass,
            defenderDodge: monster.Dodge,
            realmType: realm);

        int monsterDr = monster.DamageResist * player.MonsterDrMultiplier;
        int playerDmgPerHit = System.Math.Max(0, player.AvgWeaponDamage - monsterDr);

        // Fold critical hits into the per-swing average the same way MajorMUD's
        // round-damage does: a crit averages 3x the normal max (its own DR is
        // subtracted), blended by the crit chance. The displayed dmg/hit stays
        // the non-crit value (the "avg hit"); only DPS reflects crits.
        int critPerHit = System.Math.Max(0, player.AvgCritDamage - monsterDr);
        double critPct = System.Math.Clamp(player.CritChancePercent, 0, 100) / 100.0;
        double effectivePerHit = ((1.0 - critPct) * playerDmgPerHit) + (critPct * critPerHit);

        double dps = player.HasWeapon
            ? playerHit.OverallHitChance / 100.0 * effectivePerHit * player.SwingsPerRound
            : 0;
        // RoundsToKill is 0 when the player can't out-damage a kill (no weapon
        // or zero effective DPS) — the UI renders that as "—". Clamp before the
        // int cast: a pathological superboss (Hp/dps past int range — e.g. a
        // high-DR boss chipped only by a crit sliver) saturates to int.MaxValue,
        // which the caller's rounds-to-kill cap then shows as "<cap>+", rather
        // than wrapping to a negative that renders blank.
        double rounds = dps > 0 ? System.Math.Ceiling(monster.Hp / dps) : 0;
        int roundsToKill = rounds >= int.MaxValue ? int.MaxValue : (int)rounds;

        // Monster → player: the primary physical slot's damage and swings (its chance
        // to hit the player is IncomingHitEstimator's, across every attack).
        int monsterDmgPerHit = 0;
        int monsterSwings = 0;
        if (monster.HasPhysicalAttack)
        {
            monsterDmgPerHit = System.Math.Max(0, monster.AvgAttackDamage - player.DamageResist);
            // Monster attacks/round = its per-round energy budget divided by the
            // primary attack's energy cost — the same energy/AttEnergy formula the
            // Game-Data monster readout uses. A slowness debuff (folded by the
            // caller into a raised Slot energy) thins the swing count here, which
            // is what makes the monster attack less. Falls back to a single swing
            // when the row carries no usable energy figures.
            monsterSwings = MonsterSwingsPerRound(monster.EnergyPerRound, monster.PrimaryAttackEnergy);
        }

        return new MonsterMatchupResult(
            PlayerHitChance: playerHit.OverallHitChance,
            PlayerDamagePerHit: playerDmgPerHit,
            PlayerSwingsPerRound: player.HasWeapon ? player.SwingsPerRound : 0,
            PlayerDps: dps,
            RoundsToKill: roundsToKill,
            HasWeapon: player.HasWeapon,
            MonsterHasPhysicalAttack: monster.HasPhysicalAttack,
            MonsterDamagePerHit: monsterDmgPerHit,
            MonsterSwingsPerRound: monsterSwings);
    }

    // Attacks/round the monster lands with its primary physical slot: the
    // per-round energy budget floor-divided by that slot's energy cost (>=1 when
    // both are positive). Mirrors the Game-Data readout's "Max N x/round". When
    // either figure is missing (0), fall back to a single swing so the preview
    // still shows one attack rather than zero.
    public static int MonsterSwingsPerRound(int energyPerRound, int primaryAttackEnergy)
    {
        if (energyPerRound <= 0 || primaryAttackEnergy <= 0) return 1;
        return System.Math.Max(1, energyPerRound / primaryAttackEnergy);
    }
}

// Player-side inputs to MonsterMatchupCalculator.Compute — the offensive numbers
// (normal-attack accuracy, average weapon damage, swings/round) and the DR a
// monster's hit is reduced by.
//   Realm             — active realm, selects the Stock / ParaMUD hit formula.
//   NormalAccuracy    — computed normal-attack accuracy (the to-hit number).
//   AvgWeaponDamage   — avg of the normal-attack min/max damage, before monster DR.
//   SwingsPerRound    — swings landed per round with the current weapon.
//   HasWeapon         — false when unarmed, gates DPS / rounds-to-kill.
//   DamageResist      — player DR, subtracted from each monster hit.
//   CritChancePercent — normal-attack crit chance (0-100), gear/quest crit +
//                       Quick-and-Deadly. Folds into DPS, not the per-hit display.
//   AvgCritDamage     — avg crit damage before the monster's DR (3x the normal max).
//   MonsterDrMultiplier — how many times the monster's DR counts against a hit: the
//                       attack's post-roll multiplier when the realm takes DR off
//                       before it (Stock bash ×3 / smash ×5), else 1.
//   BackstabMin/Max   — the backstab range before the monster's DR (0 for any
//                       other attack) — the one-stab verdict reads the min.
//   MinDamage/MaxDamage — one hit's range before the monster's DR, for whichever
//                       attack this profile is (a backstab's is BackstabMin/Max).
//   QuickAndDeadlyBonus — the crit a fast swing added to CritChancePercent.
public readonly record struct PlayerMatchupProfile(
    RealmType Realm,
    int NormalAccuracy,
    int AvgWeaponDamage,
    double SwingsPerRound,
    bool HasWeapon,
    int DamageResist,
    int CritChancePercent = 0,
    int AvgCritDamage = 0,
    int MonsterDrMultiplier = 1,
    int BackstabMin = 0,
    int BackstabMax = 0,
    int MinDamage = 0,
    int MaxDamage = 0,
    int QuickAndDeadlyBonus = 0);

// Monster-side inputs to MonsterMatchupCalculator.Compute — defense
// (AC / DR / HP) and the primary physical attack slot (average damage, energy).
//   ArmourClass       — monster AC the player swings against.
//   DamageResist      — monster DR, subtracted from each player hit.
//   Hp                — monster max HP, the rounds-to-kill denominator.
//   Dodge             — monster raw dodge (the Dodge ability, abil 34), 0 for most.
//   HasPhysicalAttack — true when the monster has a melee / rob slot to preview.
//   AvgAttackDamage   — avg of the primary physical slot's min/max, before player DR.
//   EnergyPerRound    — the monster's per-round energy budget (row Energy).
//   PrimaryAttackEnergy — the primary physical slot's per-attack energy cost;
//                       swings/round = EnergyPerRound / this. A slowness debuff
//                       is folded in by the caller as a raised value here.
public readonly record struct MonsterMatchupProfile(
    int ArmourClass,
    int DamageResist,
    int Hp,
    int Dodge,
    bool HasPhysicalAttack,
    int AvgAttackDamage,
    int EnergyPerRound = 0,
    int PrimaryAttackEnergy = 0);

// Output of MonsterMatchupCalculator.Compute — the player's hit chance and
// DPS / rounds-to-kill projection, plus the monster's damage and swings.
//   PlayerHitChance           — player normal-attack hit chance vs monster AC + dodge,
//                               unrounded.
//   PlayerDamagePerHit        — player avg damage per landed hit, after monster DR.
//   PlayerSwingsPerRound      — swings/round used in the DPS projection (0 unarmed).
//   PlayerDps                 — projected damage per round: hit% * dmg/hit * swings.
//   RoundsToKill              — rounds to drop the monster at the projected DPS;
//                               0 when not killable (no weapon / zero DPS).
//   HasWeapon                 — whether the player had a weapon (gates DPS fields).
//   MonsterHasPhysicalAttack  — whether the monster has a physical slot to preview.
//   MonsterDamagePerHit       — monster avg damage per landed hit, after player DR.
//   MonsterSwingsPerRound     — attacks/round the monster lands (energy/attEnergy),
//                               thinned by a slowness debuff; 0 with no physical slot.
public readonly record struct MonsterMatchupResult(
    double PlayerHitChance,
    int PlayerDamagePerHit,
    double PlayerSwingsPerRound,
    double PlayerDps,
    int RoundsToKill,
    bool HasWeapon,
    bool MonsterHasPhysicalAttack,
    int MonsterDamagePerHit,
    int MonsterSwingsPerRound = 0);

// One of the player's known, damage-dealing attack spells — the input
// MonsterMatchupCalculator.RankAttackSpells scores against a specific
// monster's SpellImmu, resists and magic resist. Min/MaxDamagePerRound and
// ManaCostPerRound are already level-scaled, energy-multiplied and lifted by the
// caster's spell-damage bonus (see SpellCalculator.MinDamage / MaxDamage /
// ManaCost) — this record carries the RESULT of that player-side math, not the
// raw formula; the target-side resists are applied here. AffectsUndeadOnly /
// AffectsLivingOnly mirror the spell's own Abil 23 / Abil 108 flags
// (AbilityNames.cs) — a caster-side target-type gate independent of SpellImmu
// and elemental resist.
//   Targets — the spell's raw target-scope code; area scopes (DebuffTargeting.
//     IsAreaEnemy: 3/5/9/11/12) mark it an AOE attack, the rest single-target.
//   MinDamagePerRound — null for a flat spell (same as the max).
//   UsesMagicResist — a Damage(-MR) spell the target's M.R. cuts.
//   TypeOfResists — the spell's full-resist eligibility (0 / 1 / 2).
public readonly record struct PlayerAttackSpell(
    string Name, string Short, int ReqLevel, int AttType,
    long MaxDamagePerRound, long ManaCostPerRound,
    bool AffectsUndeadOnly = false, bool AffectsLivingOnly = false, int Targets = 0,
    int ManaCostPerCast = 0, long? MinDamagePerRound = null, bool UsesMagicResist = false,
    int TypeOfResists = 0);

// One spell's effectiveness against a specific monster — either blocked
// (SpellImmu too high, or the monster resists its element at or above 100%)
// with a human-readable reason, or eligible with its resist-adjusted effective
// damage AND its mana efficiency against that monster: how many rounds to kill,
// the total mana that costs, and the damage-per-mana ratio — so the user can
// judge which spell is the cheapest kill (RankAttackSpells fills RoundsToKill /
// ManaToKill only when a target HP is supplied).
public readonly record struct SpellEffectivenessResult(
    string Name, string Short, string Element, long EffectiveDamage,
    long ManaCostPerRound, bool Eligible, string? BlockedReason, bool IsAoe = false,
    int ManaCostPerCast = 0, int RoundsToKill = 0, long ManaToKill = 0)
{
    // Effective damage per point of mana — the efficiency headline. Uses the
    // per-round figures (both damage and mana carry the same energy multiplier),
    // so it reads as damage output per mana spent. 0 when free / blocked.
    public double DamagePerMana => ManaCostPerRound > 0 ? (double)EffectiveDamage / ManaCostPerRound : 0;
}

// Spell-matchup additions to MonsterMatchupCalculator below — kept as their
// own members rather than folded into Compute(), since the inputs (known
// spells, SpellImmu, elemental resists) share nothing with Compute()'s
// weapon/AC/dodge inputs and Compute() already has real callers (the
// Calculators tab's Hit Calculator) that must keep working unchanged.
public static class MonsterMatchupCalculatorSpells
{
    // Whether the player's currently-worn weapon can even land a physical hit —
    // MonsterMagicIndex.MagicalLevel(monster) is the level a weapon's own
    // HitMagic (ItemMagicIndex) must meet or exceed. A monster with no Magical
    // ability reads 0, so any weapon (including bare hands, HitMagic 0) hits.
    public static bool WeaponMeetsMagical(int weaponHitMagic, int monsterMagical)
        => weaponHitMagic >= monsterMagical;

    // Chance ONE monster attack of the given accuracy lands on the defender,
    // applying whichever ward (Prot Evil / Prot Good / Vile Ward) matches the
    // monster's alignment, unrounded so the defender's dodge still shows against a
    // hit chance at or near the floor. Monster Intel, route Details and the loop
    // simulator all read it (through IncomingHitEstimator).
    // MajorMUD alignment codes 1/2/5/6 are evil, 0/4 are good.
    public static double AttackHitChance(
        int accuracy, int alignment,
        int defenderAc, int defenderDodge, int protEvil, int protGood,
        RealmType realm, bool hasShadow = false,
        int vileWard = 0, EvilLevel defenderEvil = EvilLevel.Saint,
        int defenderArmourType = 0)
    {
        bool isEvil = alignment is 1 or 2 or 5 or 6;
        bool isGood = alignment is 0 or 4;
        // Prot Evil and Vile Ward are evil-only wards: they raise the defender's
        // defense only when the attacker (this monster) is evil, so both are
        // zeroed against a neutral/good monster. Vile Ward's raw value is scaled
        // by the defender's own evil tier inside CalculateHitChance (AdjustVileWard).
        return CombatCalculator.CalculateHitChance(
            attackerAccuracy: accuracy,
            defenderAC: defenderAc,
            defenderDodge: defenderDodge,
            protEvil: isEvil ? protEvil : 0,
            protGood: isGood ? protGood : 0,
            vileWard: isEvil ? vileWard : 0,
            evilLevel: defenderEvil,
            hasShadow: hasShadow,
            defenderArmourType: defenderArmourType,
            realmType: realm).OverallHitChance;
    }

    // A monster's overall chance to land a hit when it swings, blended across ALL
    // its physical attacks: each attack's own hit chance weighted by how often the
    // monster throws it (its use-weight, which sums to ~100 across the physical
    // slots). This is the "resembles the real chance it hits you, considering
    // every attack" figure — more faithful than the single majority-slot accuracy,
    // and it averages the per-attack hit OUTPUTS, not the accuracies (which would
    // skew through the non-linear hit formula). Unrounded, like AttackHitChance.
    // `accuracyDelta` is subtracted from every attack's accuracy (a folded accuracy
    // debuff; 0 for the base case) and may push it negative. Null when the monster
    // has no physical attack to blend.
    public static double? WeightedIncomingHitChance(
        IReadOnlyList<(int Accuracy, double Weight)> physicalAttacks, int accuracyDelta,
        int alignment, int defenderAc, int defenderDodge, int protEvil, int protGood,
        RealmType realm, bool hasShadow = false,
        int vileWard = 0, EvilLevel defenderEvil = EvilLevel.Saint, int defenderArmourType = 0)
    {
        if (physicalAttacks is null || physicalAttacks.Count == 0) return null;
        double totalWeight = 0, weightedHit = 0, sum = 0;
        foreach ((int accuracy, double weight) in physicalAttacks)
        {
            double w = weight > 0 ? weight : 0;
            double hit = AttackHitChance(accuracy - accuracyDelta, alignment, defenderAc,
                defenderDodge, protEvil, protGood, realm, hasShadow, vileWard, defenderEvil,
                defenderArmourType);
            weightedHit += w * hit;
            totalWeight += w;
            sum += hit;
        }
        // Every weight zero (a data quirk) → fall back to a plain mean so the row
        // still shows a number rather than dropping out.
        return totalWeight > 0 ? weightedHit / totalWeight : sum / physicalAttacks.Count;
    }

    // Rank the player's known attack spells by effective damage against one
    // monster: a spell whose ReqLevel is below the monster's SpellImmu never
    // lands (GAME_MECHANICS "Why an attack spell fails to damage — three
    // independent mechanics"; the combat engine applies the same rule in
    // CombatSpellGates.LevelBlocked); a spell restricted to undead-
    // only (Abil 23) or living-only (Abil 108) targets is blocked outright
    // against a monster on the wrong side of that gate, independent of
    // SpellImmu/resist; a spell whose element the monster resists ≥100% deals
    // zero real damage (MonsterResistIndex's own determinism — exactly 100
    // blocks, over 100 heals the target, so both read as blocked here, not
    // just "reduced to 0"). Everything else scores by (100 - resist%) applied
    // to the spell's own per-round max damage. Eligible spells sort first,
    // highest effective damage first; blocked spells trail, in their input
    // order.
    public static IReadOnlyList<SpellEffectivenessResult> RankAttackSpells(
        IReadOnlyList<PlayerAttackSpell> spells,
        int monsterSpellImmunity,
        IReadOnlyDictionary<int, int> monsterElementalResists,
        bool monsterIsUndead = false,
        long monsterHp = 0,
        int monsterMagicResist = 0,
        bool monsterAntiMagic = false,
        RealmType realm = RealmType.Stock)
    {
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(monsterElementalResists);

        var results = new List<SpellEffectivenessResult>();
        foreach (PlayerAttackSpell s in spells)
        {
            string element = Game.GameData.LookupEnums.FormatSpellAttackType(
                s.AttType.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? "?";
            // Area target scopes (Divided/Full Attack Area) mark an AOE attack;
            // everything else is single-target. Drives the panel's spell grouping.
            bool isAoe = Game.Combat.DebuffTargeting.IsAreaEnemy(s.Targets);

            if (s.ReqLevel < monsterSpellImmunity)
            {
                results.Add(new SpellEffectivenessResult(s.Name, s.Short, element, 0,
                    s.ManaCostPerRound, Eligible: false,
                    $"Spell immune below level {monsterSpellImmunity} (this spell is {s.ReqLevel})", IsAoe: isAoe));
                continue;
            }

            if (s.AffectsUndeadOnly && !monsterIsUndead)
            {
                results.Add(new SpellEffectivenessResult(s.Name, s.Short, element, 0,
                    s.ManaCostPerRound, Eligible: false, "Affects undead only — this monster isn't undead", IsAoe: isAoe));
                continue;
            }
            if (s.AffectsLivingOnly && monsterIsUndead)
            {
                results.Add(new SpellEffectivenessResult(s.Name, s.Short, element, 0,
                    s.ManaCostPerRound, Eligible: false, "Affects living only — this monster is undead", IsAoe: isAoe));
                continue;
            }

            int resistCode = Game.Combat.MonsterResistIndex.ElementalResistCode(s.AttType);
            int resistPercent = resistCode >= 0 && monsterElementalResists.TryGetValue(resistCode, out int pct)
                ? pct : 0;
            if (resistPercent >= 100)
            {
                results.Add(new SpellEffectivenessResult(s.Name, s.Short, element, 0,
                    s.ManaCostPerRound, Eligible: false,
                    resistPercent == 100 ? $"{element} fully resisted" : $"{element} heals this monster instead", IsAoe: isAoe));
                continue;
            }

            // Expected damage a round: the average of the resisted min and max, in
            // the realm's resist order, less the chance the cast is resisted outright
            // (GAME_MECHANICS "Spell damage — Stock vs Paradigm").
            long lo = Game.Spells.SpellDamageCalculator.AfterTargetResists(s.MinDamagePerRound ?? s.MaxDamagePerRound,
                s.UsesMagicResist, monsterMagicResist, monsterAntiMagic, resistPercent, realm);
            long hi = Game.Spells.SpellDamageCalculator.AfterTargetResists(s.MaxDamagePerRound,
                s.UsesMagicResist, monsterMagicResist, monsterAntiMagic, resistPercent, realm);
            double fullResist = Game.Spells.SpellDamageCalculator.FullResistChance(
                s.TypeOfResists, monsterMagicResist, monsterAntiMagic, realm);
            long effective = (long)System.Math.Round(
                System.Math.Max(0, (lo + hi) / 2.0) * (100 - fullResist) / 100.0, System.MidpointRounding.AwayFromZero);
            // Kill estimate against this monster (only when a HP was supplied):
            // rounds = ceil(HP / effective-per-round), mana-to-kill = rounds × mana/round.
            int roundsToKill = effective > 0 && monsterHp > 0
                ? (int)System.Math.Ceiling(monsterHp / (double)effective) : 0;
            long manaToKill = roundsToKill > 0 ? (long)roundsToKill * s.ManaCostPerRound : 0;
            results.Add(new SpellEffectivenessResult(
                s.Name, s.Short, element, effective, s.ManaCostPerRound, Eligible: true, BlockedReason: null,
                IsAoe: isAoe, ManaCostPerCast: s.ManaCostPerCast, RoundsToKill: roundsToKill, ManaToKill: manaToKill));
        }

        // Eligible first, then most mana-efficient (damage per mana) first — the
        // point of the panel is picking the cheapest kill — with raw effective
        // damage as the tiebreak; blocked spells trail in input order.
        return results
            .OrderByDescending(r => r.Eligible)
            .ThenByDescending(r => r.DamagePerMana)
            .ThenByDescending(r => r.EffectiveDamage)
            .ToList();
    }
}
