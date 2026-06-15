using FujinTerm.Models.Profile;

namespace FujinTerm.Game.Inventory;

/// <summary>
/// Splits an <see cref="InventorySnapshot"/> into the two halves a deathpile is
/// recorded as: items worn at death (re-equippable) and carried-but-unworn
/// items ("inventory lost"). Shared by <see cref="Map.RoomTracker.NoteDeath"/>
/// (real deaths) and <see cref="Recovery.DeathRecoveryManager.SimulateDeath"/>
/// (the test button) so both capture identically.
/// </summary>
public static class DeathLootCapture
{
    /// <summary>
    /// Map a snapshot to <c>(equipped, lost)</c>. A worn item that also lingers
    /// in the carried list — possible between full <c>i</c> dumps, since the worn
    /// set is patched live on equip/remove but the carried set isn't — is shown
    /// only under <c>equipped</c>, never double-counted as lost.
    /// </summary>
    public static (List<DeathItem> Equipped, List<DeathItem> Lost) FromSnapshot(InventorySnapshot snapshot)
    {
        var equipped = new List<DeathItem>(snapshot.EquippedItems.Count);
        var equippedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (EquippedItem item in snapshot.EquippedItems)
        {
            equipped.Add(new DeathItem(item.Name, item.Slot));
            equippedNames.Add(item.Name);
        }

        var lost = new List<DeathItem>();
        foreach (string name in snapshot.CarriedItems)
        {
            if (equippedNames.Contains(name)) continue;
            lost.Add(new DeathItem(name));
        }

        return (equipped, lost);
    }
}
