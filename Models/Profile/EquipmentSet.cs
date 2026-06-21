namespace FujinTerm.Models.Profile;

/// <summary>
/// A trigger-purposed loadout — the per-slot items a character wants worn when
/// this set's <see cref="Trigger"/> moment fires. The Equipment Manager keeps one
/// set per <see cref="EquipTriggerType"/> (Default / Backstab / Pre-rest HP /
/// Pre-rest Mana); a set can be applied automatically when enabled
/// (<see cref="Enabled"/>) or remotely via <c>@equip-&lt;keyword&gt;</c>.
/// </summary>
public sealed class EquipmentSet
{
    /// <summary>Stable identity used to reference the set (e.g. from automation).
    /// A GUID string so it survives any rename.</summary>
    public string Id { get; set; } = System.Guid.NewGuid().ToString();

    /// <summary>Which game-state moment this set is the loadout for.</summary>
    public EquipTriggerType Trigger { get; set; }

    /// <summary>Whether automation may equip this set when its
    /// <see cref="Trigger"/> fires. Toggled by the set list's Enable / Disable
    /// buttons; manual / remote apply ignores it.</summary>
    public bool Enabled { get; set; }

    /// <summary>User-facing set name shown in the Workshop (e.g. <c>"Pre-rest HP"</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Short suffix a party member appends to <c>@equip-</c> to apply
    /// this set (e.g. <c>@equip-backstab</c>). Matched case-insensitively; the set
    /// <see cref="Name"/> is a fallback when no keyword matches.</summary>
    public string Keyword { get; set; } = string.Empty;

    /// <summary>Per-slot intent. A slot with no <see cref="EquipmentSlotEntry.ItemName"/>
    /// is "no change" — left untouched on apply.</summary>
    public System.Collections.Generic.List<EquipmentSlotEntry> Slots { get; set; } = new();
}
