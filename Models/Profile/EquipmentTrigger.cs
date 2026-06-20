namespace FujinTerm.Models.Profile;

/// <summary>
/// Binds one <see cref="EquipTriggerType"/> game-state moment to the gear set
/// (<see cref="SetId"/>) to auto-equip when it fires. Six of these — one per
/// trigger type — live on <see cref="EquipmentSettings.Triggers"/>.
/// </summary>
public sealed class EquipmentTrigger
{
    /// <summary>Which game-state moment this trigger reacts to.</summary>
    public EquipTriggerType TriggerType { get; set; }

    /// <summary>Whether this trigger is armed.</summary>
    public bool Enabled { get; set; }

    /// <summary>The <see cref="EquipmentSet.Id"/> to apply when the trigger
    /// fires. Empty = no target set chosen yet.</summary>
    public string SetId { get; set; } = string.Empty;
}
