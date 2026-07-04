namespace FujinTerm.Models.Profile;

// A trigger-purposed loadout — the per-slot items a character wants worn when
// this set's Trigger moment fires. The Equipment Manager keeps one set per
// EquipTriggerType (Default / Backstab / Pre-rest HP / Pre-rest Mana); a set can
// be applied automatically when enabled (Enabled) or remotely via
// @equip-<keyword>.
public sealed class EquipmentSet
{
    // Stable identity used to reference the set (e.g. from automation). A GUID
    // string so it survives any rename.
    public string Id { get; set; } = System.Guid.NewGuid().ToString();

    // Which game-state moment this set is the loadout for.
    public EquipTriggerType Trigger { get; set; }

    // Whether automation may equip this set when its Trigger fires. Toggled by
    // the set list's Enable / Disable buttons; manual / remote apply ignores it.
    public bool Enabled { get; set; }

    // User-facing set name shown in the Workshop (e.g. "Pre-rest HP").
    public string Name { get; set; } = string.Empty;

    // Short suffix a party member appends to @equip- to apply this set (e.g.
    // @equip-backstab). Matched case-insensitively; the set Name is a fallback
    // when no keyword matches.
    public string Keyword { get; set; } = string.Empty;

    // Per-slot intent. A slot with no EquipmentSlotEntry.ItemName is "no change"
    // — left untouched on apply.
    public System.Collections.Generic.List<EquipmentSlotEntry> Slots { get; set; } = new();
}
