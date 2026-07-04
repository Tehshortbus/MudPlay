using System;
using System.Text.Json;
using FujinTerm.Game.Inventory;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Calculators;

// The live inputs a CP plan is computed against: the raw-base baseline (current
// stats minus equipment bonuses, floored at the race minimum), the race min/max
// bounds, and the realm. Resolved from PlayerStats + game data + equipment;
// shared by the CP Allocation tab and the auto-train engine so the baseline math
// lives in exactly one place.
public readonly record struct CharacterPlanContext(
    bool HasCharacter,
    CpPlanEntry Baseline,
    CpPlanEntry RaceMin,
    CpPlanEntry RaceMax,
    RealmType Realm)
{
    // Resolve the plan context for the live character. HasCharacter is false
    // (entries defaulted) when no race resolves — no character or no game-data
    // set loaded.
    public static CharacterPlanContext Resolve(PlayerStats stats, GameDataCache gameData, InventoryManager inventory)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(inventory);

        RealmType realm = gameData.ActiveRealm;
        JsonElement? raceOpt = gameData.FindRowByName("Races", stats.Race);
        if (raceOpt is not JsonElement race || string.IsNullOrEmpty(stats.Race))
            return new CharacterPlanContext(false, new CpPlanEntry(), new CpPlanEntry(), new CpPlanEntry(), realm);

        var raceMin = new CpPlanEntry(0,
            GetInt(race, "mSTR"), GetInt(race, "mINT"), GetInt(race, "mWIL"),
            GetInt(race, "mAGL"), GetInt(race, "mHEA"), GetInt(race, "mCHM"));
        var raceMax = new CpPlanEntry(0,
            GetInt(race, "xSTR"), GetInt(race, "xINT"), GetInt(race, "xWIL"),
            GetInt(race, "xAGL"), GetInt(race, "xHEA"), GetInt(race, "xCHM"));

        // Raw base = live stats minus equipment bonuses (the `stat` screen shows
        // gear-boosted values), floored at the race minimum.
        EquipmentStatSummary eq = CharacterCalculator
            .AggregateEquipmentStats(inventory.Snapshot.EquippedItems, gameData).Totals;
        var baseline = new CpPlanEntry(stats.Level,
            RawBase(stats.Strength, eq.PlusStrength, raceMin.Strength),
            RawBase(stats.Intellect, eq.PlusIntellect, raceMin.Intellect),
            RawBase(stats.Willpower, eq.PlusWillpower, raceMin.Willpower),
            RawBase(stats.Agility, eq.PlusAgility, raceMin.Agility),
            RawBase(stats.Health, eq.PlusHealth, raceMin.Health),
            RawBase(stats.Charm, eq.PlusCharm, raceMin.Charm));

        return new CharacterPlanContext(true, baseline, raceMin, raceMax, realm);
    }

    private static int RawBase(int live, int equipBonus, int raceMin) => Math.Max(raceMin, live - equipBonus);

    private static int GetInt(JsonElement row, string property)
    {
        if (row.ValueKind != JsonValueKind.Object) return 0;
        if (!row.TryGetProperty(property, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }
}
