using System.Collections.Generic;

namespace FujinTerm.Game.Quests;

/// <summary>
/// One quest discovered by <see cref="QuestCrawler"/> from the active set's
/// <c>TBInfo</c> quest/textblock chains — the mechanical layer that
/// <c>QuestStore</c>'s user/seed text layer hangs names and visibility off of.
/// Identity is (<see cref="Flag"/>, <see cref="Step"/>):
/// <list type="bullet">
///   <item><see cref="Step"/> <c>= 0</c> for a single-part quest: the whole
///   <c>giveability &lt;flag&gt;</c> chain (any internal give-step) is one quest,
///   since the give-steps are progress within it, not separate quests. Per-class
///   level variants of the <em>same</em> give-step (e.g. Smash, Meditate) stay one
///   quest — they differ only in who can take it and when.</item>
///   <item><see cref="Step"/> <c>= band level</c> for a multi-part quest: a flag
///   whose give/test/check <c>minlevel</c> progress gates form a strict per-level
///   staircase is re-run once per level tier (the five-tier alignment flags are the
///   canonical case, but any realm flag with this shape splits the same way). The
///   band level is portable across realms where the internal give-step numbering is
///   not.</item>
/// </list>
/// </summary>
/// <param name="Flag">Quest-flag ability id — the <c>giveability &lt;flag&gt;</c> target.</param>
/// <param name="Step">Band level for a multi-part quest; <c>0</c> otherwise.</param>
/// <param name="RequiredLevel">
/// Lowest level this quest (or band) becomes attainable: the band level for a
/// multi-part quest, else the <c>minlevel</c> gate resolved to the requested class;
/// <c>0</c> when the quest imposes no level gate.
/// </param>
/// <param name="Bonuses">
/// Permanent stat bonuses this quest grants, from <c>addability</c> directives whose
/// target is <em>not</em> itself a granted quest flag (those are progress markers),
/// already resolved to the requested class (class-specific branch when present, else
/// the no-class default). Empty when the quest grants no stat reward.
/// </param>
/// <param name="AwardItems">
/// Keeper item ids the quest hands the player as the final reward — the equippable or
/// usable prize that signals the quest (or this band) is complete (a ring, a chest, a
/// tabard, a weapon). Only the keepers handed at the chain's <em>last</em> give-step
/// qualify: a quest's awards come at the very end, so earlier giveitems are quest-use
/// items the player consumes along the way, not rewards. Turn-in tokens
/// (<c>giveitem</c>'d then <c>takeitem</c>'d under the same flag) are excluded too.
/// Class-resolved like <paramref name="Bonuses"/>: the requested class's own item when
/// any final-step keeper is guarded to it, else the no-class default. Empty when the
/// quest awards no keeper item.
/// </param>
/// <param name="ClassIds">
/// Class <c>Number</c>s the quest is restricted to — non-null only when <em>every</em>
/// <c>giveability</c> chain that grants the flag carries a <c>class N</c> guard (the
/// allowed set is their union). <c>null</c> when any granting chain is unguarded, i.e.
/// the quest is open to all classes. Conservative by design: a quest some chain leaves
/// open to everyone is never reported as restricted. Drives class-based filtering of
/// the Quest Status list.
/// </param>
/// <param name="RaceIds">
/// Race <c>Number</c>s the quest is restricted to, by the same all-granting-chains-guarded
/// rule as <paramref name="ClassIds"/>; <c>null</c> when open to all races.
/// </param>
/// <param name="BandOrdinal">
/// 1-based position of this band among the flag's bands in ascending level order
/// (band 1 is the lowest tier). <c>0</c> for a single-part quest. Drives the
/// <c>"Good 1"</c>-style default band name.
/// </param>
/// <param name="StepRangeStart">
/// Lowest give-step <c>Order</c> that belongs to this band's followable checklist
/// (inclusive). <c>0</c> for a single-part quest (no band filtering). Band 1 starts
/// at 1 so it absorbs any pre-first-milestone give-steps.
/// </param>
/// <param name="StepRangeEnd">
/// Highest give-step <c>Order</c> that belongs to this band (inclusive). <c>0</c> for
/// a single-part quest (no filtering). The last band carries <see cref="int.MaxValue"/>
/// so it absorbs every overflow give-step past the final milestone — nothing is dropped.
/// </param>
/// <param name="AwardsAbility">
/// <c>true</c> when the quest's reward <em>is</em> the granted ability itself — a
/// single-part quest that hands no keeper item and no stat bonus (Smash, Meditate,
/// Perfect Stealth, SeeHidden). The presentation layer renders the flag's ability name
/// (<see cref="Flag"/>) as the award. Always <c>false</c> for a multi-part band, whose
/// flag is a shared progress marker across tiers rather than a per-tier prize.
/// </param>
public sealed record CrawledQuest(
    int Flag,
    int Step,
    int RequiredLevel,
    IReadOnlyList<QuestBonus> Bonuses,
    IReadOnlyList<int> AwardItems,
    IReadOnlyList<int>? ClassIds = null,
    IReadOnlyList<int>? RaceIds = null,
    int BandOrdinal = 0,
    int StepRangeStart = 0,
    int StepRangeEnd = 0,
    bool AwardsAbility = false);
