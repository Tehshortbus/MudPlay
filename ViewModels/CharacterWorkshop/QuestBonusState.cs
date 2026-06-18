using System;
using System.Collections.Generic;
using FujinTerm.Game.Quests;

namespace FujinTerm.ViewModels.CharacterWorkshop;

/// <summary>
/// Live quest-reward state shared between the Quest Status tab (writer) and the
/// Character Info tab (reader), so completing a quest folds its permanent stat
/// bonus into Character Info's derived combat + a Quest Bonuses readout. The Quest
/// Status VM republishes the union of every completed quest's bonuses on each
/// completion change; the Character Info VM reads <see cref="Bonuses"/> and
/// recomputes on <see cref="Changed"/>. Mirrors <see cref="CpPlanState"/>.
/// </summary>
public sealed class QuestBonusState
{
    /// <summary>Raised whenever the completed-quest bonus set changes.</summary>
    public event Action? Changed;

    /// <summary>
    /// Flattened stat bonuses from every quest currently marked complete, in
    /// discovery order. Empty when no completed quest grants a bonus.
    /// </summary>
    public IReadOnlyList<QuestBonus> Bonuses { get; private set; } = Array.Empty<QuestBonus>();

    /// <summary>Replace the published bonus set and notify readers.</summary>
    public void Update(IReadOnlyList<QuestBonus> bonuses)
    {
        ArgumentNullException.ThrowIfNull(bonuses);
        Bonuses = bonuses;
        Changed?.Invoke();
    }
}
