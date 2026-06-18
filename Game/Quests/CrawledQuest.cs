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
///   whose <c>minlevel</c> gates climb across <em>different</em> give-steps is
///   re-run once per level tier (the alignment flags are the canonical case, but
///   any realm flag with this shape splits the same way). The band level is
///   portable across realms where the internal give-step numbering is not.</item>
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
/// Keeper item ids the quest hands the player and never takes back — the equippable
/// or usable rewards that signal a part is complete (a chest, a weapon). Distinct
/// from turn-in tokens, which are <c>giveitem</c>'d then <c>takeitem</c>'d under the
/// same flag and so are excluded. Empty when the quest awards no keeper item.
/// </param>
public sealed record CrawledQuest(
    int Flag,
    int Step,
    int RequiredLevel,
    IReadOnlyList<QuestBonus> Bonuses,
    IReadOnlyList<int> AwardItems);
