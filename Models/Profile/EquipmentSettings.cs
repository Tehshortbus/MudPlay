namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character equipment-manager state — the trigger-purposed gear sets. One
/// <see cref="EquipmentSet"/> per <see cref="EquipTriggerType"/> (the Equipment
/// Manager seeds any that are missing). Persisted as the top-level
/// <see cref="CharacterProfile.Equipment"/> blob (like
/// <see cref="CharacterProfile.CharacterPlan"/>), not a tier-merged Settings
/// section, since it is whole-character state rather than a per-tier delta.
/// </summary>
public sealed class EquipmentSettings
{
    /// <summary>The trigger-purposed gear sets, one per <see cref="EquipTriggerType"/>.</summary>
    public System.Collections.Generic.List<EquipmentSet> Sets { get; set; } = new();
}
