using FujinTerm.Models.Profile;

namespace FujinTerm.Game.Inventory;

// Splits an InventorySnapshot into the two halves a deathpile is recorded as:
// items worn at death (re-equippable) and carried-but-unworn items ("inventory
// lost"). Shared by RoomTracker.NoteDeath (real deaths) and
// DeathRecoveryManager.SimulateDeath (the test button) so both capture
// identically.
public static class DeathLootCapture
{
    // Map a snapshot to (equipped, lost). A worn item that also lingers in the
    // carried list — possible between full 'i' dumps, since the worn set is
    // patched live on equip/remove but the carried set isn't — is shown only
    // under equipped, never double-counted as lost.
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
