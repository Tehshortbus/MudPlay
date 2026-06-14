namespace FujinTerm.Game.Inventory;

/// <summary>
/// One worn item parsed from an <c>i</c> dump: the game prints equipped items
/// inline with a trailing <c>(&lt;Slot&gt;)</c> suffix (e.g.
/// <c>padded vest (Torso)</c>), while carried-but-unworn items have no suffix.
/// <see cref="InventoryManager"/> harvests these so the Character Workshop can
/// aggregate equipment bonuses against game data.
/// </summary>
/// <param name="Name">Bare item name, slot suffix stripped.</param>
/// <param name="Slot">
/// Normalized slot label (one of the 21-slot model values plus
/// <c>Weapon Hand</c> / <c>Off-Hand</c>). The game writes a two-handed weapon
/// as <c>(Two handed)</c> in your own inventory but <c>(Weapon Hand)</c> in
/// player listings; both normalize to <c>Weapon Hand</c> here.
/// </param>
public readonly record struct EquippedItem(string Name, string Slot);
