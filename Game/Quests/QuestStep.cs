using System.Collections.Generic;

namespace FujinTerm.Game.Quests;

/// <summary>
/// One auto-drafted, followable step in a quest, produced by
/// <see cref="QuestStepGraph"/> from a TBInfo chain that advances the quest flag.
/// This is the baseline checklist a user refines into <c>QuestDefinition.Steps</c>
/// markdown — it surfaces the action the game data records (where, the command, what
/// changes hands), ordered by the quest's own give-step, but not the prose the user
/// adds.
/// </summary>
/// <param name="Order">
/// The chain's give-step — the quest's own progress counter, used to sequence the
/// checklist into a faithful walk order.
/// </param>
/// <param name="Command">
/// The player command that fires the step (e.g. <c>"sit throne"</c>,
/// <c>"go hole"</c>) when the chain leads with a verb; <c>null</c> when the chain
/// is guard-led (reached by dialogue branch), in which case <see cref="Location"/>
/// is the anchor.
/// </param>
/// <param name="Location">Provenance from <c>CalledFrom</c> (e.g. <c>"Room 10/245"</c>, <c>"Monster #61"</c>); <c>null</c> when absent.</param>
/// <param name="RequiredItems"><c>checkitem</c> ids the player must be carrying.</param>
/// <param name="TurnInItems"><c>takeitem</c> ids the step takes from the player.</param>
/// <param name="GrantedItems"><c>giveitem</c> ids the step hands the player.</param>
public sealed record QuestStep(
    int Order,
    string? Command,
    string? Location,
    IReadOnlyList<int> RequiredItems,
    IReadOnlyList<int> TurnInItems,
    IReadOnlyList<int> GrantedItems);
