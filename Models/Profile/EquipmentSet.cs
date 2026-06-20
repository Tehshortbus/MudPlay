namespace FujinTerm.Models.Profile;

/// <summary>
/// A named loadout — the per-slot items a character wants worn when this set is
/// applied. Sets live on <see cref="EquipmentSettings.Sets"/> and are applied
/// either from the Workshop UI or remotely via <c>@equip-&lt;keyword&gt;</c>.
/// </summary>
public sealed class EquipmentSet
{
    /// <summary>Stable identity used to reference the set (e.g. from a trigger).
    /// A GUID string so it survives renames.</summary>
    public string Id { get; set; } = System.Guid.NewGuid().ToString();

    /// <summary>User-facing set name shown in the Workshop.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Short suffix a party member appends to <c>@equip-</c> to apply
    /// this set (e.g. <c>@equip-fighting</c>). Matched case-insensitively; the set
    /// <see cref="Name"/> is a fallback when no keyword matches.</summary>
    public string Keyword { get; set; } = string.Empty;

    /// <summary>Per-slot intent. Only <see cref="EquipmentSlotEntry.Controlled"/>
    /// entries take part in an apply.</summary>
    public System.Collections.Generic.List<EquipmentSlotEntry> Slots { get; set; } = new();
}
