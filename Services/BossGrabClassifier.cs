using System;
using System.Collections.Generic;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;

namespace MudPlay.Services;

// Resolves a boss row's Grab-All behavior against the active game data: is its name a
// monster (grab drop table on death), an item that sits in the room (get on entry),
// or neither (hide the checkbox). Shared by the Bosses tab (to show/hide the checkbox
// + tooltip) and AppServices (to fire the item-on-entry get). The pure Monster/Item
// priority lives in BossGrabAllCommands.ClassifyKind; this adds the game-data lookups.
public static class BossGrabClassifier
{
    private static readonly string[] Articles = { "the ", "a ", "an " };

    public static BossGrabKind Classify(GameDataCache gameData, BossDef def)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(def);
        bool isMonster = gameData.FindRowByName("Monsters", def.Name) is not null;
        bool isItem = ItemGetName(gameData, def.Name) is not null;
        return BossGrabAllCommands.ClassifyKind(def.MonsterNumber is not null, isMonster, isItem);
    }

    // The `get` argument for an item-boss — the boss name matched against the Items
    // table (tolerant of a leading article, so "the bogwood box" resolves to the
    // "bogwood box" item), or null when the name isn't an item.
    public static string? ItemGetName(GameDataCache gameData, string bossName)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        foreach (string candidate in NameCandidates(bossName))
            if (gameData.FindRowByName("Items", candidate) is not null) return candidate;
        return null;
    }

    // Ordered names to try for an item match: the trimmed name, then the same with a
    // leading article dropped ("the bogwood box" → "bogwood box"). Deduped; empty for
    // a blank name.
    internal static IReadOnlyList<string> NameCandidates(string bossName)
    {
        if (string.IsNullOrWhiteSpace(bossName)) return Array.Empty<string>();
        string raw = bossName.Trim();
        string stripped = StripLeadingArticle(raw);
        return string.Equals(stripped, raw, StringComparison.Ordinal)
            ? new[] { raw }
            : new[] { raw, stripped };
    }

    private static string StripLeadingArticle(string name)
    {
        foreach (string a in Articles)
            if (name.StartsWith(a, StringComparison.OrdinalIgnoreCase))
                return name[a.Length..].Trim();
        return name;
    }
}
