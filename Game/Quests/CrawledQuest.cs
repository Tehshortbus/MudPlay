using System.Collections.Generic;

namespace FujinTerm.Game.Quests;

// One quest discovered by QuestCrawler from the active set's TBInfo
// quest/textblock chains — the mechanical layer that QuestStore's user/seed text
// layer hangs names and visibility off of. Identity is (Flag, Step):
//   - Step = 0 for a single-part quest: the whole `giveability <flag>` chain (any
//     internal give-step) is one quest, since the give-steps are progress within
//     it, not separate quests. Per-class level variants of the same give-step
//     (e.g. Smash, Meditate) stay one quest — they differ only in who can take it
//     and when.
//   - Step = band level for a multi-part quest: a flag whose give/test/check
//     minlevel progress gates form a strict per-level staircase is re-run once per
//     level tier (the five-tier alignment flags are the canonical case, but any
//     realm flag with this shape splits the same way). The band level is portable
//     across realms where the internal give-step numbering is not.
//
// Fields:
//   Flag — quest-flag ability id, the `giveability <flag>` target.
//   Step — band level for a multi-part quest; 0 otherwise.
//   RequiredLevel — lowest level this quest (or band) becomes attainable: the band
//     level for a multi-part quest, else the minlevel gate resolved to the
//     requested class; 0 when the quest imposes no level gate.
//   Bonuses — permanent stat bonuses this quest grants, from addability directives
//     whose target is not itself a granted quest flag (those are progress markers),
//     already resolved to the requested class (class-specific branch when present,
//     else the no-class default). Empty when the quest grants no stat reward.
//   AwardItems — keeper item ids the quest hands the player as the final reward —
//     the equippable or usable prize that signals the quest (or this band) is
//     complete (a ring, a chest, a tabard, a weapon). Only the keepers handed at
//     the chain's last give-step qualify: a quest's awards come at the very end, so
//     earlier giveitems are quest-use items the player consumes along the way, not
//     rewards. Turn-in tokens (giveitem'd then takeitem'd under the same flag) are
//     excluded too. Class-resolved like Bonuses: the requested class's own item
//     when any final-step keeper is guarded to it, else the no-class default. Empty
//     when the quest awards no keeper item.
//   ClassIds — class Numbers the quest is restricted to — non-null only when every
//     giveability chain that grants the flag carries a `class N` guard (the allowed
//     set is their union). null when any granting chain is unguarded, i.e. the
//     quest is open to all classes. Conservative by design: a quest some chain
//     leaves open to everyone is never reported as restricted. Drives class-based
//     filtering of the Quest Status list.
//   RaceIds — race Numbers the quest is restricted to, by the same
//     all-granting-chains-guarded rule as ClassIds; null when open to all races.
//   ClassLevels — per-class lowest level gate for a class-restricted quest: class Number →
//     minlevel, for each class in ClassIds that carries a level gate. Lets the "Requires:
//     Classes …" line show each class's own requirement (Priest-20, Mage-15) instead of a
//     bare name — the point of a multi-class ability quest whose classes unlock it at
//     different levels. null when the quest isn't class-restricted, or when no restricted
//     class carries a gate.
//   BandOrdinal — 1-based position of this band among the flag's bands in ascending
//     level order (band 1 is the lowest tier). 0 for a single-part quest. Drives
//     the "Good 1"-style default band name.
//   StepRangeStart — lowest give-step Order that belongs to this band's followable
//     checklist (inclusive). 0 for a single-part quest (no band filtering). Band 1
//     starts at 1 so it absorbs any pre-first-milestone give-steps.
//   StepRangeEnd — highest give-step Order that belongs to this band (inclusive). 0
//     for a single-part quest (no filtering). The last band carries int.MaxValue so
//     it absorbs every overflow give-step past the final milestone — nothing is
//     dropped.
//   AwardsAbility — true when the quest's reward is the granted ability itself — a
//     single-part quest that hands no keeper item and no stat bonus (Smash,
//     Meditate, Perfect Stealth, SeeHidden). The presentation layer renders the
//     flag's ability name (Flag) as the award. Always false for a multi-part band,
//     whose flag is a shared progress marker across tiers rather than a per-tier
//     prize.
//   ProgressByValue — true when this band tiers by the flag's ability value (1, 2,
//     3, …) rather than by climbing give-step order — the shape of a quest advanced
//     by relative `addability <flag>` increments (MageBane). Step and
//     StepRangeStart/StepRangeEnd are that ability value, so the followable-step
//     draft walks the same axis (QuestStepGraph.Build(…, byAbilityValue: true)).
//     false for single-part and give-step-laddered quests.
public sealed record CrawledQuest(
    int Flag,
    int Step,
    int RequiredLevel,
    IReadOnlyList<QuestBonus> Bonuses,
    IReadOnlyList<int> AwardItems,
    IReadOnlyList<int>? ClassIds = null,
    IReadOnlyList<int>? RaceIds = null,
    IReadOnlyDictionary<int, int>? ClassLevels = null,
    int BandOrdinal = 0,
    int StepRangeStart = 0,
    int StepRangeEnd = 0,
    bool AwardsAbility = false,
    bool ProgressByValue = false);
