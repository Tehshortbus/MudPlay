using System;
using System.Collections.Generic;
using System.Text.Json;
using FujinTerm.Services;

namespace FujinTerm.Game.Calculators;

// Class / race capability lookups resolved from MajorMUD game data: which
// classes can learn Smash, and whether a class or race grants innate stealth.
// The smash set is discovered by scanning the TBInfo quest/textblock table for
// chains that pair giveability 32 1 (Smash, step 1) with a class N restriction;
// stealth is a direct Abil-0..9 scan (race ability 102 = RaceStealth, class
// ability 103 = ClassStealth). All methods read through GameDataCache and are
// stateless — recomputed each call, which is fine for the button-press refresh
// cadence and avoids stale caches across a set switch.
public static class ClassCapabilities
{
    private const int SmashAbilityId = 32;
    private const int RaceStealthAbilityId = 102;
    private const int ClassStealthAbilityId = 103;
    // Martial-arts strikes are class-innate abilities (Mystic carries all three);
    // a class grants a strike when it lists that ability in an Abil-0..9 slot.
    private const int PunchAbilityId = 29;
    private const int KickAbilityId = 30;
    private const int JumpKickAbilityId = 35;
    private const int MaxRecordAbilSlots = 10;

    // Class names that can learn Smash, or null meaning "assume every class can"
    // — no class-restricted smash chain exists in the active set, so hiding the
    // row would risk hiding it from a genuinely capable class. A TBInfo Action
    // chain qualifies when it contains giveability 32 1 AND one or more class N
    // steps; those class IDs map to names via the Classes table's Number → Name.
    // Any such chain makes the collected set authoritative.
    public static HashSet<string>? GetSmashCapableClasses(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);

        // The quest/textblock table imports as TBInfo.json (MDB "TBInfo"), not
        // "TextBlocks" — reading the wrong name silently disables smash gating.
        JsonDocument? textBlocks = cache.GetRawTable("TBInfo");
        if (textBlocks is null) return null;

        Dictionary<int, string> classIdToName = BuildClassIdToNameMap(cache);
        if (classIdToName.Count == 0) return null;

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement block in textBlocks.RootElement.EnumerateArray())
        {
            if (!block.TryGetProperty("Action", out JsonElement actionEl)) continue;
            if (actionEl.ValueKind != JsonValueKind.String) continue;
            string? action = actionEl.GetString();
            if (string.IsNullOrEmpty(action) || action == "\0") continue;

            foreach (string chain in action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                bool hasGiveSmash = false;
                var chainClassIds = new List<int>();
                foreach (string step in chain.Split(':'))
                {
                    string t = step.Trim();
                    if (t.StartsWith("giveability ", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = t.Split(' ');
                        if (parts.Length >= 3 && int.TryParse(parts[1], out int abilId)
                            && int.TryParse(parts[2], out int stepVal)
                            && abilId == SmashAbilityId && stepVal == 1)
                        {
                            hasGiveSmash = true;
                        }
                    }
                    else if (t.StartsWith("class ", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = t.Split(' ');
                        if (parts.Length >= 2 && int.TryParse(parts[1], out int classId))
                            chainClassIds.Add(classId);
                    }
                }

                // Only chains that pair giveability 32 1 with a class restriction
                // count — an open giveability chain is a reward block gated
                // upstream, not "everyone can smash".
                if (hasGiveSmash && chainClassIds.Count > 0)
                {
                    foreach (int cid in chainClassIds)
                    {
                        if (classIdToName.TryGetValue(cid, out string? name))
                            result.Add(name);
                    }
                }
            }
        }

        // Any class-restricted smash chain is authoritative; absent that, fall
        // back to null so we don't hide the row from genuinely capable classes.
        return result.Count > 0 ? result : null;
    }

    // True if the race row grants innate stealth (Abil-0..9 == 102).
    public static bool RaceHasStealth(JsonElement? raceRow) => HasAbility(raceRow, RaceStealthAbilityId);

    // True if the class row grants innate stealth (Abil-0..9 == 103).
    public static bool ClassHasStealth(JsonElement? classRow) => HasAbility(classRow, ClassStealthAbilityId);

    // Martial-arts strikes the class innately grants (Abil-0..9 == 29 / 30 / 35).
    // Only classes that carry the ability get the strike row in the combat panel.
    public static bool ClassHasPunch(JsonElement? classRow) => HasAbility(classRow, PunchAbilityId);
    public static bool ClassHasKick(JsonElement? classRow) => HasAbility(classRow, KickAbilityId);
    public static bool ClassHasJumpKick(JsonElement? classRow) => HasAbility(classRow, JumpKickAbilityId);

    private static bool HasAbility(JsonElement? row, int abilityId)
    {
        if (row is not JsonElement data || data.ValueKind != JsonValueKind.Object) return false;
        for (int i = 0; i < MaxRecordAbilSlots; i++)
        {
            if (GetInt(data, $"Abil-{i}") == abilityId) return true;
        }
        return false;
    }

    private static Dictionary<int, string> BuildClassIdToNameMap(GameDataCache cache)
    {
        var map = new Dictionary<int, string>();
        JsonDocument? classes = cache.GetRawTable("Classes");
        if (classes is null) return map;

        foreach (JsonElement cls in classes.RootElement.EnumerateArray())
        {
            int num = GetInt(cls, "Number");
            if (num <= 0) continue;
            if (cls.TryGetProperty("Name", out JsonElement nameEl)
                && nameEl.ValueKind == JsonValueKind.String)
            {
                string? name = nameEl.GetString();
                if (!string.IsNullOrEmpty(name)) map[num] = name;
            }
        }
        return map;
    }

    // Safe numeric read of a game-data field: missing / non-numeric reads as 0.
    private static int GetInt(JsonElement row, string property)
    {
        if (row.ValueKind != JsonValueKind.Object) return 0;
        if (!row.TryGetProperty(property, out JsonElement el)) return 0;
        return el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v) ? v : 0;
    }
}
