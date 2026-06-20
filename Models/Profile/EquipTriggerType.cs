namespace FujinTerm.Models.Profile;

/// <summary>
/// The six fixed game-state moments at which an <see cref="EquipmentTrigger"/>
/// can auto-swap to a gear set. Not user-extensible — these map one-to-one to
/// hooks the automation engines raise. Auto-equip trigger <i>evaluation</i> is
/// wired in a later Phase-10 PR; this enum defines the persisted schema now.
/// </summary>
public enum EquipTriggerType
{
    /// <summary>About to rest for HP — swap to e.g. an HP-regen set.</summary>
    PreRestHp,

    /// <summary>About to rest for mana — swap to e.g. a mana-regen set.</summary>
    PreRestMp,

    /// <summary>About to meditate.</summary>
    PreRestMeditate,

    /// <summary>Entering normal combat.</summary>
    Normal,

    /// <summary>Running / walking a route (out of combat).</summary>
    Running,

    /// <summary>Idle — no combat or movement for a short debounce window.</summary>
    Idle,
}
