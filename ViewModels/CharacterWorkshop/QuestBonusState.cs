using System;
using System.Collections.Generic;
using FujinTerm.Game.Quests;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// Live quest-reward state shared between the Quest Status tab (writer) and the
// Character Info tab (reader), so completing a quest folds its permanent stat
// bonus into Character Info's derived combat + a Quest Bonuses readout. The Quest
// Status VM republishes the union of every completed quest's bonuses on each
// completion change; the Character Info VM reads Bonuses and recomputes on
// Changed. Mirrors CpPlanState.
public sealed class QuestBonusState
{
    // Raised whenever the completed-quest bonus set changes.
    public event Action? Changed;

    // Flattened stat bonuses from every quest currently marked complete, in
    // discovery order. Empty when no completed quest grants a bonus.
    public IReadOnlyList<QuestBonus> Bonuses { get; private set; } = Array.Empty<QuestBonus>();

    // Replace the published bonus set and notify readers.
    public void Update(IReadOnlyList<QuestBonus> bonuses)
    {
        ArgumentNullException.ThrowIfNull(bonuses);
        Bonuses = bonuses;
        Changed?.Invoke();
    }
}
