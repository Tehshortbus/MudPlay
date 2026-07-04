using System.Collections.Generic;

namespace FujinTerm.Game.Quests;

// One auto-drafted, followable step in a quest, produced by QuestStepGraph from a
// TBInfo chain that advances the quest flag. This is the baseline checklist a user
// refines into QuestDefinition.Steps markdown — it surfaces the action the game data
// records (where, the command, what changes hands), ordered by the quest's own
// give-step, but not the prose the user adds.
//
// Fields:
//   Order — the chain's give-step, the quest's own progress counter, used to
//     sequence the checklist into a faithful walk order.
//   Command — the player command that fires the step (e.g. "sit throne", "go hole")
//     when the chain leads with a verb; null when the chain is guard-led (reached
//     by dialogue branch), in which case Location is the anchor.
//   Location — provenance from CalledFrom (e.g. "Room 10/245", "Monster #61"); null
//     when absent.
//   RequiredItems — checkitem ids the player must be carrying.
//   TurnInItems — takeitem ids the step takes from the player.
//   GrantedItems — giveitem ids the step hands the player.
public sealed record QuestStep(
    int Order,
    string? Command,
    string? Location,
    IReadOnlyList<int> RequiredItems,
    IReadOnlyList<int> TurnInItems,
    IReadOnlyList<int> GrantedItems);
