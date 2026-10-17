using System.Collections.Generic;
using MudPlay.Game.GameData;

namespace MudPlay.Game.Quests;

// Resolves a @quest argument to a quest-flag number, and a flag back to a friendly
// display label. Pure/static.
//
// Resolution order (first hit wins): an explicit flag number, a curated alias (the
// alignment quests the seed leaves unnamed — "good align" → 126), an exact name
// match against the supplied candidates (AbilityNames flag names + user-set quest
// names), then a loose substring match. The candidates come from the handler so this
// stays free of GameDataCache / profile dependencies.
public static class QuestNameResolver
{
    // Curated aliases for flags the seed doesn't name — chiefly the three alignment
    // quests, whose bands carry empty Name fields. Keys are normalised (lower, single
    // spaces); AbilityNames' own "GoodQuest" spelling is covered too so a sender can
    // use either. Extend here as more well-known unnamed quests surface.
    private static readonly Dictionary<string, int> Aliases = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["good align"] = 126, ["good alignment"] = 126, ["good"] = 126,
        ["good quest"] = 126, ["goodquest"] = 126,
        ["neutral align"] = 127, ["neutral alignment"] = 127, ["neutral"] = 127,
        ["neutral quest"] = 127, ["neutralquest"] = 127,
        ["evil align"] = 128, ["evil alignment"] = 128, ["evil"] = 128,
        ["evil quest"] = 128, ["evilquest"] = 128,
    };

    // Friendly display labels for the curated flags, matching how a player refers to
    // them (the alignment quests read "Good align", not AbilityNames' "GoodQuest").
    private static readonly Dictionary<int, string> Labels = new()
    {
        [126] = "Good align", [127] = "Neutral align", [128] = "Evil align",
    };

    // Resolve a @quest arg to a flag number, or null when nothing matches. candidates
    // is (flag, name) pairs the handler already knows — flag names from AbilityNames
    // and any user-set quest names.
    public static int? Resolve(string? query, IEnumerable<(int Flag, string Name)> candidates)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        string q = Normalize(query);

        if (int.TryParse(q, out int flag) && flag > 0) return flag;
        if (Aliases.TryGetValue(q, out int aliasFlag)) return aliasFlag;

        int? partial = null;
        foreach ((int f, string name) in candidates)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            string n = Normalize(name);
            if (n == q) return f;                                   // exact name wins
            if (partial is null && (n.Contains(q) || q.Contains(n)))
                partial = f;                                        // remember first loose hit
        }
        return partial;
    }

    // A friendly label for a flag: the curated alignment label, else a user-set quest
    // name, else the AbilityNames flag name, else "Flag <n>". userName is the resolved
    // QuestDefinition.Name (may be empty).
    public static string Label(int flag, string? userName)
    {
        if (Labels.TryGetValue(flag, out string? curated)) return curated;
        if (!string.IsNullOrWhiteSpace(userName)) return userName!.Trim();
        return AbilityNames.GetName(flag) ?? $"Flag {flag}";
    }

    private static string Normalize(string s)
    {
        s = s.Trim().ToLowerInvariant();
        // Collapse internal whitespace so "good   align" == "good align".
        return string.Join(' ', s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries));
    }
}
