namespace FujinTerm.Models.Profile;

/// <summary>
/// The fixed game-state moments the Equipment Manager swaps gear for. Each maps
/// one-to-one to a trigger-purposed <see cref="EquipmentSet"/> in the left-hand
/// set list — the four are not free-form named loadouts but the conditions under
/// which automation re-equips. More moments may be added later; the enum is the
/// persisted schema, so order is display order.
/// </summary>
public enum EquipTriggerType
{
    /// <summary>The baseline loadout worn during normal weapon combat.</summary>
    Default,

    /// <summary>Worn while making backstab attacks; reverts to
    /// <see cref="Default"/> when falling back to normal combat.</summary>
    Backstab,

    /// <summary>Equipped fully before resting / <c>@wait</c> when the reason is HP.</summary>
    PreRestHp,

    /// <summary>Equipped fully before resting / <c>@wait</c> when the reason is mana.</summary>
    PreRestMana,
}
