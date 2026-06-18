using System.Collections.Generic;

namespace FujinTerm.Game.Quests;

/// <summary>
/// One auto-drafted, followable step in a quest, produced by
/// <see cref="QuestStepGraph"/> from a TBInfo chain that advances the quest flag.
/// This is the baseline checklist a user refines into <c>QuestDefinition.Steps</c>
/// markdown — it surfaces what the raw game data knows (where, what to do, what's
/// gated, what changes hands) but not the prose the user adds.
/// </summary>
/// <param name="Order">
/// The chain's give-step — the quest's own progress counter, used to sequence the
/// checklist. Steps gate on each other in this order via same-flag
/// <c>checkability</c>/<c>testability</c>, so it's a faithful walk order.
/// </param>
/// <param name="Command">
/// The player command that fires the step (e.g. <c>"sit throne"</c>,
/// <c>"go hole"</c>) when the chain leads with a verb; <c>null</c> when the chain
/// is guard-led (reached by dialogue branch), in which case <see cref="Location"/>
/// is the anchor.
/// </param>
/// <param name="Location">Provenance from <c>CalledFrom</c> (e.g. <c>"Room 10/245"</c>, <c>"Monster #61"</c>); <c>null</c> when absent.</param>
/// <param name="RequiredLevel"><c>minlevel</c> gate, or <c>0</c> when none.</param>
/// <param name="Alignment">Alignment gate, or <see cref="QuestAlignment.None"/>.</param>
/// <param name="RequiredClasses"><c>class N</c> restrictions; empty when open to all.</param>
/// <param name="RequiredQuests">
/// Cross-flag prerequisites — (flag, step) pairs this step's
/// <c>checkability</c>/<c>testability</c> requires of <em>other</em> quests.
/// Same-flag gates are omitted since <see cref="Order"/> already implies them.
/// </param>
/// <param name="RequiredItems"><c>checkitem</c> ids the player must be carrying.</param>
/// <param name="TurnInItems"><c>takeitem</c> ids consumed by the step.</param>
/// <param name="GrantedItems"><c>giveitem</c> ids handed to the player.</param>
public sealed record QuestStep(
    int Order,
    string? Command,
    string? Location,
    int RequiredLevel,
    QuestAlignment Alignment,
    IReadOnlyList<int> RequiredClasses,
    IReadOnlyList<(int Flag, int Step)> RequiredQuests,
    IReadOnlyList<int> RequiredItems,
    IReadOnlyList<int> TurnInItems,
    IReadOnlyList<int> GrantedItems);
