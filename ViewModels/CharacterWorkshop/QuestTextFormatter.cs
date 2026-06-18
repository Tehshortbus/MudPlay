using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FujinTerm.Game.GameData;
using FujinTerm.Game.Quests;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// Presentation formatting shared by the Quest Status tab and the Quest editor
/// window — turns crawled quest mechanics (<see cref="CrawledQuest"/> /
/// <see cref="QuestStep"/>) into the human-readable labels both surfaces render.
/// Pure functions over the active <see cref="GameDataCache"/>; no state.
/// </summary>
internal static class QuestTextFormatter
{
    /// <summary>Auto-draft title for a quest when the user hasn't named it: the flag's ability name, plus the band level for a multi-part quest.</summary>
    public static string FallbackTitle(CrawledQuest q)
    {
        string flagName = AbilityNames.FormatId(q.Flag);
        return q.Step > 0 ? $"{flagName} (Lv {q.Step})" : flagName;
    }

    /// <summary>Level-gate label (<c>"Level N"</c>), or empty when ungated.</summary>
    public static string Level(int level) =>
        level > 0 ? string.Create(CultureInfo.InvariantCulture, $"Level {level}") : string.Empty;

    /// <summary>Class-resolved permanent stat-bonus summary, or empty when the quest grants none.</summary>
    public static string Bonuses(IReadOnlyList<QuestBonus> bonuses) =>
        bonuses.Count == 0 ? string.Empty
            : AbilityNames.SummarizeAbilities(bonuses.Select(b => (b.AbilityId, b.Value)));

    /// <summary>Comma-joined keeper-item award names, or empty when none.</summary>
    public static string Awards(GameDataCache gameData, IReadOnlyList<int> awardItems) =>
        awardItems.Count == 0 ? string.Empty
            : string.Join(", ", awardItems.Select(id => ItemName(gameData, id)));

    /// <summary>One followable step rendered as <c>command · @ location · turn in … · need … · receive …</c>.</summary>
    public static string Step(GameDataCache gameData, QuestStep s)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.Command)) parts.Add(s.Command!);
        if (!string.IsNullOrWhiteSpace(s.Location)) parts.Add($"@ {s.Location}");
        if (s.TurnInItems.Count > 0) parts.Add("turn in " + string.Join(", ", s.TurnInItems.Select(id => ItemName(gameData, id))));
        if (s.RequiredItems.Count > 0) parts.Add("need " + string.Join(", ", s.RequiredItems.Select(id => ItemName(gameData, id))));
        if (s.GrantedItems.Count > 0) parts.Add("receive " + string.Join(", ", s.GrantedItems.Select(id => ItemName(gameData, id))));
        return parts.Count > 0
            ? string.Join("  ·  ", parts)
            : string.Create(CultureInfo.InvariantCulture, $"Step {s.Order}");
    }

    /// <summary>Item display name by id, falling back to <c>#id</c> when the active set has no such row.</summary>
    public static string ItemName(GameDataCache gameData, int id) =>
        gameData.FindNameByNumber("Items", id)
        ?? string.Create(CultureInfo.InvariantCulture, $"#{id}");
}
