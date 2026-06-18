using System.Collections.Generic;

namespace FujinTerm.Game.Quests;

/// <summary>
/// One quest discovered by <see cref="QuestCrawler"/> from the active set's
/// <c>TBInfo</c> quest/textblock chains — the mechanical layer that
/// <c>QuestStore</c>'s user/seed text layer hangs names and visibility off of.
/// Identity is (<see cref="Flag"/>, <see cref="Step"/>):
/// <list type="bullet">
///   <item><see cref="Step"/> <c>= 0</c> for a single-flag quest: the whole
///   <c>giveability &lt;flag&gt;</c> chain (any internal step) is one quest, since
///   steps are progress within it, not separate quests.</item>
///   <item><see cref="Step"/> <c>= band level</c> (10 / 20 / 30 / …) for the
///   alignment flags (126/127/128), which are a sequence of "Nth Alignment"
///   quests split by <c>minlevel</c> milestones; the band level is portable
///   across realms where the internal give-step numbering is not.</item>
/// </list>
/// </summary>
/// <param name="Flag">Quest-flag ability id — the <c>giveability &lt;flag&gt;</c> target.</param>
/// <param name="Step">Band level for alignment quests; <c>0</c> otherwise.</param>
/// <param name="RequiredLevel">
/// Lowest level the bonus reward becomes attainable (the reward chain's
/// <c>minlevel</c>, or the band level for alignment quests); <c>0</c> when the
/// quest grants no stat reward.
/// </param>
/// <param name="Bonuses">
/// Permanent stat bonuses this quest grants, already resolved to the requested
/// class (class-specific branch when present, else the no-class default).
/// Empty for gate-only quests that grant no stats.
/// </param>
public sealed record CrawledQuest(
    int Flag,
    int Step,
    int RequiredLevel,
    IReadOnlyList<QuestBonus> Bonuses);
