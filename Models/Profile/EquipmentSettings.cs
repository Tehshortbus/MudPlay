namespace FujinTerm.Models.Profile;

// Per-character equipment-manager state — the trigger-purposed gear sets. One
// EquipmentSet per EquipTriggerType (the Equipment Manager seeds any that are
// missing). Persisted as the top-level CharacterProfile.Equipment blob (like
// CharacterProfile.CharacterPlan), not a tier-merged Settings section, since it
// is whole-character state rather than a per-tier delta.
public sealed class EquipmentSettings
{
    // The trigger-purposed gear sets, one per EquipTriggerType.
    public System.Collections.Generic.List<EquipmentSet> Sets { get; set; } = new();
}
