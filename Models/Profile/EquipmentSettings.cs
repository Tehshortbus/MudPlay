namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character equipment-manager state — the saved gear sets and the
/// auto-equip triggers that swap between them. Persisted as the top-level
/// <see cref="CharacterProfile.Equipment"/> blob (like
/// <see cref="CharacterProfile.CharacterPlan"/>), not a tier-merged Settings
/// section, since it is whole-character state rather than a per-tier delta.
/// </summary>
public sealed class EquipmentSettings
{
    /// <summary>Saved loadouts, in user order.</summary>
    public System.Collections.Generic.List<EquipmentSet> Sets { get; set; } = new();

    /// <summary>The six fixed auto-equip triggers (see
    /// <see cref="EquipTriggerType"/>). Evaluation lands in a later PR.</summary>
    public System.Collections.Generic.List<EquipmentTrigger> Triggers { get; set; } = new();

    /// <summary>Master gate for the auto-equip triggers. When false, no trigger
    /// fires regardless of its own <see cref="EquipmentTrigger.Enabled"/>.</summary>
    public bool AutoEquipTriggersEnabled { get; set; } = true;
}
