namespace FujinTerm.Models.Profile;

// The fixed game-state moments the Equipment Manager swaps gear for. Each maps
// one-to-one to a trigger-purposed EquipmentSet in the left-hand set list — the
// four are not free-form named loadouts but the conditions under which
// automation re-equips. More moments may be added later; the enum is the
// persisted schema, so order is display order.
public enum EquipTriggerType
{
    // The baseline loadout worn during normal weapon combat.
    Default,

    // Worn while making backstab attacks; reverts to Default when falling back
    // to normal combat.
    Backstab,

    // Equipped fully before resting / @wait when the reason is HP.
    PreRestHp,

    // Equipped fully before resting / @wait when the reason is mana.
    PreRestMana,
}
