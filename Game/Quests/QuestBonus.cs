namespace FujinTerm.Game.Quests;

/// <summary>
/// One permanent stat bonus a quest grants, crawled from a reward chain's
/// <c>addability &lt;abilityId&gt; &lt;value&gt;</c> directive. <see cref="AbilityId"/>
/// is the same ability-id space the equipment crawler resolves through
/// <c>CharacterCalculator.MapAbilityToStat</c> (e.g. 117/118 = backstab min/max
/// damage, 27 = stealth, 69 = max mana), never a quest-flag id — quest-flag
/// <c>addability</c> entries are progress markers, not stat rewards, and are
/// filtered out at crawl time.
/// </summary>
public readonly record struct QuestBonus(int AbilityId, int Value);
