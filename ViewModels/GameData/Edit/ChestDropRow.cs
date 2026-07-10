namespace FujinTerm.ViewModels.GameData.Edit;

// One row of the item dialog's chest-contents table: the item a container can
// yield and the chance (already formatted, e.g. "42%") that a single `open`
// produces at least one of it. Read-only — the item dialog has no item-opener
// plumbing, so unlike the room popup's shop rows these names aren't clickable.
public sealed class ChestDropRow
{
    public string Name { get; }
    public string Chance { get; }

    public ChestDropRow(string name, string chance)
    {
        Name = name;
        Chance = chance;
    }
}
