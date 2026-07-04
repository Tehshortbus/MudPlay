namespace FujinTerm.Game.Quests;

// One permanent stat bonus a quest grants, crawled from a reward chain's
// `addability <abilityId> <value>` directive. AbilityId is the same ability-id
// space the equipment crawler resolves through CharacterCalculator.MapAbilityToStat
// (e.g. 117/118 = backstab min/max damage, 27 = stealth, 69 = max mana), never a
// quest-flag id — quest-flag addability entries are progress markers, not stat
// rewards, and are filtered out at crawl time.
public readonly record struct QuestBonus(int AbilityId, int Value);
