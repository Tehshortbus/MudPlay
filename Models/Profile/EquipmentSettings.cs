namespace MudPlay.Models.Profile;

// Per-character equipment-manager state — the trigger-purposed gear sets. One
// EquipmentSet per EquipTriggerType (the Equipment Manager seeds any that are
// missing). Persisted as the top-level CharacterProfile.Equipment blob (like
// CharacterProfile.CharacterPlan), not a tier-merged Settings section, since it
// is whole-character state rather than a per-tier delta.
public sealed class EquipmentSettings
{
    // The trigger-purposed gear sets, one per EquipTriggerType.
    public System.Collections.Generic.List<EquipmentSet> Sets { get; set; } = new();

    // When true, a fight that interrupts a rest is fought in the Default combat
    // loadout: the coordinator swaps to Default on combat entry and swaps back to
    // the pre-rest set on room-clear if the rest still isn't satisfied. Default
    // false = the long-standing behavior (keep the pre-rest loadout through the
    // fight, revert to Default only once recovered) — surfaced in the Equipment
    // Manager as the "Don't swap to default upon entering combat" checkbox (checked
    // = false here).
    public bool SwapToDefaultOnCombat { get; set; }
}
