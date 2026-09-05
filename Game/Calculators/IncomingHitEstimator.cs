using System;
using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Game.Quests;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Calculators;

// The player's live defense against a monster's swing — the AC / Dodge / wards a
// monster's attack lands against. Assembled the one way Monster Intel's "Hits You
// %" column does it: worn gear + the permanent race/class innate + completed-quest
// bonuses + the character's configured (assumed-up) AC buffs. Evil is the player's
// own alignment tier (gear-independent); it only scales an evil-only Vile Ward, so
// it carries the same Fiend default Monster Intel seeds (a worn Vile Ward implies an
// evil character).
public readonly record struct PlayerDefenseProfile(
    int Ac, int Dodge, int ProtEvil, int ProtGood, bool Shadow, int VileWard, EvilLevel Evil);

// Shared source for "how likely is this monster to hit me right now" — the single
// weighted incoming-hit figure Monster Intel's master list surfaces, extracted here
// so the route Details window colours monster names from the SAME number Monster
// Intel shows. Pure domain: takes explicit character inputs, no service holder.
public static class IncomingHitEstimator
{
    // Assemble the live defense profile from explicit character inputs — the exact
    // recipe MonsterIntelViewModel uses to seed its Hits-You-% sim: worn-gear
    // aggregate + race/class innate + completed-quest folds + configured AC buffs.
    // AC rides the WORN gear + permanent base + configured buffs, not the live `stat`
    // ArmourClass (which already folds any buffs active at capture — adding the
    // configured-buff AC on top would double-count them).
    public static PlayerDefenseProfile BuildLiveDefense(
        PlayerStats stats, IReadOnlyList<EquippedItem> worn, EncumbranceReading encum,
        GameDataCache gameData, BuffSettings? buffs, IReadOnlyList<KnownSpell>? spells,
        IReadOnlyList<QuestBonus>? questBonuses, EvilLevel evil = EvilLevel.Fiend)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(worn);
        ArgumentNullException.ThrowIfNull(gameData);

        EquipmentStatBreakdown gear = CharacterCalculator.AggregateEquipmentStats(worn, gameData);
        if (gameData.FindRowByName("Races", stats.Race) is JsonElement raceRow)
            CharacterCalculator.ApplyAbilityBonuses(gear, raceRow, stats.Race);
        if (gameData.FindRowByName("Classes", stats.Class) is JsonElement classRow)
            CharacterCalculator.ApplyAbilityBonuses(gear, classRow, stats.Class);
        if (questBonuses is not null)
            CharacterCalculator.ApplyQuestBonuses(gear, questBonuses, "Quests");
        EquipmentStatSummary totals = gear.Totals;

        BuffDefense buff = BuffDefenseCalculator.Compute(buffs, stats.Level, spells);
        // Truncate the fractional AC, don't round: item AC is stored ×10 so the
        // summed PlusAC carries tenths (e.g. gear +36.5), and the game's integer
        // combat AC is the FLOOR of that total — the same value Character Info's
        // "Projected AC" reflects. Math.Round pushed a trailing .5 up to the next
        // integer, over-stating AC by 1 (user-confirmed: projected 61.5 = actual,
        // but the sim was seeding 62). buff.Ac is already an integer, so truncating
        // PlusAC alone floors the whole.
        int ac = (int)totals.PlusAC + buff.Ac;
        int dodge = CombatCalculator.CalcDodge(
            stats.Level, stats.Agility, stats.Charm, totals.PlusDodge,
            encum.CurrentWeight, encum.MaxWeight);
        int protEvil = totals.PlusProtEvil + buff.ProtEvil;
        bool shadow = totals.PlusShadowResist > 0 || buff.HasShadow;
        return new PlayerDefenseProfile(
            ac, dodge, protEvil, totals.PlusProtGood, shadow, totals.PlusVileWard, evil);
    }

    // A monster's blended chance to land a hit on the player across ALL its physical
    // attacks — the same weighted figure Monster Intel's Hits-You-% column shows.
    // Null when the monster has no catalogued physical attack (an NPC/caster record).
    public static int? WeightedHitPercent(
        MonsterCatalogEntry monster, in PlayerDefenseProfile def, RealmType realm)
    {
        ArgumentNullException.ThrowIfNull(monster);
        return MonsterMatchupCalculatorSpells.WeightedIncomingHitPercent(
            monster.PhysicalAttacks, accuracyDelta: 0, monster.Align,
            def.Ac, def.Dodge, def.ProtEvil, def.ProtGood,
            realm, def.Shadow, def.VileWard, def.Evil);
    }
}
