using System.Collections.Generic;

namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character Auto-Trainer settings — the <c>"AutoTrainer"</c> entry in
/// <see cref="CharacterProfile.Settings"/>. Surfaced by the Settings →
/// Auto-Trainer tab.
/// </summary>
public sealed class AutoTrainerSettings
{
    /// <summary>
    /// Master toggle: when running a loop / auto-lair and a level-up is
    /// available, detour to the appropriate trainer and <c>train</c>.
    /// </summary>
    public bool AutoTrain { get; set; }

    /// <summary>
    /// Cascading toggle (only meaningful when <see cref="AutoTrain"/> is on):
    /// after training a level, drive the <c>train stats</c> screen to apply the
    /// CP plan's row for the new level.
    /// </summary>
    public bool AutoTrainStats { get; set; }

    /// <summary>
    /// Shop numbers the user has switched OFF for auto-train. Storing the
    /// disabled set (rather than the allowed set) keeps the JSON small and
    /// means newly-discovered trainers default to allowed. <c>null</c> / empty
    /// = every discovered trainer is allowed.
    /// </summary>
    public List<int>? DisabledTrainers { get; set; }
}
